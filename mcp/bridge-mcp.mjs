#!/usr/bin/env node
// Zero-dependency stdio MCP server that bridges the Model Context Protocol to the
// AI-MCP-Containers HTTP API (POST /chat). Claude Code (or any MCP client) launches this
// over stdio; each `tools/call` is translated into one authenticated HTTP request to
// the bridge and the answer is returned as MCP text content.
//
// Why a shim at all: AI-MCP-Containers speaks a bespoke HTTP contract (/chat), NOT MCP.
// An MCP client cannot talk to it directly, so this process is the adapter at the seam.
//
// Transport: newline-delimited JSON-RPC 2.0 over stdin/stdout (the MCP stdio transport).
// stdout carries ONLY protocol frames; all diagnostics go to stderr (structured JSON).
//
// Config (env, with safe fallbacks):
//   AI_MCP_URL        base URL of the bridge        (default http://127.0.0.1:8788  = codex)
//   AI_MCP_TOKEN      bearer token                  (else read AI_MCP_TOKEN from AI_MCP_ENV_FILE)
//   AI_MCP_ENV_FILE   path to AI-MCP-Containers .env     (default <thisdir>/../.env)
//   AI_MCP_PROVIDER   provider passed to /chat      (default "codex"; "claude" for :8789)
//   AI_MCP_TOOL_NAME  exposed MCP tool name         (default "ask_codex")
//   AI_MCP_TOOL_DESC  exposed MCP tool description   (sensible default per provider)
//   AI_MCP_DEFAULT_CWD default cwd for the agent run (optional; must be inside bridge roots)
//   AI_MCP_TIMEOUT_MS  per-call timeout in ms        (default 300000)
import { readFileSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const HERE = dirname(fileURLToPath(import.meta.url));
const SERVER_NAME = 'ai-mcp-containers';
const SERVER_VERSION = '1.1.0';
const MAX_PROMPT = 100_000; // mirror ChatRequestValidator.MaxPromptLength in the bridge
const SUPPORTED_PROTOCOL = '2025-06-18';

// --- structured stderr logging (never touches stdout) -----------------------
function log(level, msg, extra) {
  try {
    process.stderr.write(JSON.stringify({ level, msg, ...extra }) + '\n');
  } catch {
    /* logging must never throw into the protocol path */
  }
}

// --- config resolution ------------------------------------------------------
function parseEnvFile(path) {
  try {
    const out = {};
    for (const line of readFileSync(path, 'utf8').split('\n')) {
      const m = /^\s*([A-Z0-9_]+)\s*=\s*(.*)\s*$/.exec(line);
      if (m) out[m[1]] = m[2].replace(/^["']|["']$/g, '');
    }
    return out;
  } catch {
    return {};
  }
}

// Existing MCP launcher configurations can migrate independently of the service.
// Only the adapter accepts deprecated client aliases; new configuration uses AI_MCP_*.
const clientEnv = {...process.env};
for (const [key, value] of Object.entries(process.env)) {
  if (key.startsWith('BRIDGE_') && clientEnv['AI_MCP_' + key.slice(7)] === undefined)
    clientEnv['AI_MCP_' + key.slice(7)] = value;
}

const ENV_FILE = clientEnv.AI_MCP_ENV_FILE || resolve(HERE, '..', '.env');
const fileEnv = parseEnvFile(ENV_FILE);

// ── EXACTLY TWO BRIDGES, BOTH GLOBAL (owner rule 2026-08-25 — permanent) ───────────────
// This shim points at ONE fixed bridge per provider — codex on :8788, claude on :8789 — and it
// never starts, names or discovers a container. It supersedes the 2026-07-25 rule that gave each
// project its own on-demand instance: those accumulated one per project with nothing reaping them,
// and the owner chose a single global endpoint per provider instead.
//
// The launcher machinery is DELETED rather than switched off, deliberately. A flag that defaults
// to "global" still leaves the per-project path one env var away, and the accumulation would come
// back the first time something set it. With no launcher to call, this shim CANNOT create a
// bridge — which is the difference between a rule that is written down and one that holds.
//
// The per-request `cwd` still decides which project a run works in; the bridge validates it
// against its global AI_MCP_ALLOWED_ROOTS allow-list and answers 403 `cwd_forbidden` outside them.
const PROVIDER = clientEnv.AI_MCP_PROVIDER || 'codex';

const CFG = {
  url: (clientEnv.AI_MCP_URL || (PROVIDER === 'claude' ? 'http://127.0.0.1:8789' : 'http://127.0.0.1:8788')).replace(/\/+$/, ''),
  token: clientEnv.AI_MCP_TOKEN || fileEnv.AI_MCP_TOKEN || '',
  provider: PROVIDER,
  // Only an explicit override. A call that passes no `cwd` and sets none falls back to the
  // bridge's own AI_MCP_DEFAULT_CWD, which is where the non-interactive consumers land.
  defaultCwd: clientEnv.AI_MCP_DEFAULT_CWD || undefined,
  requireCwd: /^(?:1|true|yes)$/i.test(clientEnv.AI_MCP_REQUIRE_CWD || ''),
  // A NaN / zero / negative override would abort every call instantly; fall back rather than
  // fail in a way that reads like the bridge being down.
  timeoutMs: (() => {
    const n = Number(clientEnv.AI_MCP_TIMEOUT_MS || 300_000);
    return Number.isFinite(n) && n > 0 ? n : 300_000;
  })(),
};
const TOOL_NAME = clientEnv.AI_MCP_TOOL_NAME || (CFG.provider === 'claude' ? 'ask_claude' : 'ask_codex');
const TOOL_DESC =
  clientEnv.AI_MCP_TOOL_DESC ||
  (CFG.provider === 'claude'
    ? 'Ask the Claude agent (via AI-MCP-Containers) a question or to perform a task in the mounted workspace. Returns its full text answer.'
    : 'Ask Codex (OpenAI GPT-5.5, via AI-MCP-Containers) a question or for a second opinion. Read-only over the mounted workspace. Returns its full text answer.');

if (!CFG.token) {
  log('warn', 'no bearer token resolved; configure AI_MCP_TOKEN before calling the service', {
    envFile: ENV_FILE,
  });
}

// --- the one tool's input contract -----------------------------------------
const INPUT_SCHEMA = {
  type: 'object',
  additionalProperties: false,
  properties: {
    prompt: {
      type: 'string',
      description: 'The question or task for the agent. Plain text, 1..100000 chars.',
    },
    cwd: {
      type: 'string',
      description:
        'Absolute working directory for the run — the project the agent should work in. Must be inside the global bridge roots, else the bridge answers 403 cwd_forbidden.',
    },
    sandbox: {
      type: 'string',
      enum: CFG.provider === 'claude' ? ['read-only', 'read-only-web'] : ['read-only'],
      description:
        "Permission policy for this run. Leave it unset to use the service default. A service that cannot honour a value refuses it with sandbox_unavailable. Project writes are disabled.",
    },
  },
  required: CFG.requireCwd ? ['prompt', 'cwd'] : ['prompt'],
};

// --- JSON-RPC framing -------------------------------------------------------
function send(obj) {
  process.stdout.write(JSON.stringify(obj) + '\n');
}
function reply(id, result) {
  send({ jsonrpc: '2.0', id, result });
}
function replyError(id, code, message) {
  send({ jsonrpc: '2.0', id, error: { code, message } });
}

// --- the bridge call --------------------------------------------------------
async function callBridge(prompt, cwd, sandbox, cancellationSignal) {
  // ONE fixed global bridge per provider (owner rule 2026-08-25). There is no routing step and no
  // container to start: the `cwd` is passed through as given and the bridge decides whether it is
  // inside its global roots.
  const url = CFG.url;
  const runCwd = cwd || CFG.defaultCwd;

  const headers = { 'content-type': 'application/json' };
  if (CFG.token) headers.authorization = `Bearer ${CFG.token}`;
  // Keep a distinct correlation ID per MCP call. The supplied Compose deployment
  // disables provider session reuse for all callers.
  const body = { prompt, provider: CFG.provider, conversationId: `mcp-${randomUUID()}` };
  if (runCwd) body.cwd = runCwd;
  if (sandbox) body.sandbox = sandbox;

  const ac = new AbortController();
  let timedOut = false;
  const t = setTimeout(() => {
    timedOut = true;
    ac.abort();
  }, CFG.timeoutMs);
  const cancel = () => ac.abort();
  cancellationSignal?.addEventListener('abort', cancel, { once: true });
  if (cancellationSignal?.aborted) cancel();
  let res;
  let text;
  try {
    res = await fetch(`${url}/chat`, {
      method: 'POST',
      headers,
      body: JSON.stringify(body),
      signal: ac.signal,
    });
    // Keep the deadline and client cancellation active until the body is consumed.
    text = await res.text();
  } catch (e) {
    if (ac.signal.aborted && cancellationSignal?.aborted)
      throw new Error('bridge request cancelled by MCP client');
    const reason = timedOut
      ? `timeout after ${CFG.timeoutMs}ms`
      : `connection failed (${e?.code || e?.message || 'unknown'})`;
    throw new Error(`Cannot reach codex-bridge at ${url}: ${reason}. Is the container up? (docker compose up -d)`);
  } finally {
    clearTimeout(t);
    cancellationSignal?.removeEventListener('abort', cancel);
  }

  let data = null;
  try {
    data = text ? JSON.parse(text) : null;
  } catch {
    /* non-JSON body — fall through to status-based error */
  }

  if (!res.ok) {
    // Surface the bridge's typed error code/message; never echo auth headers.
    const code = data?.code || `http_${res.status}`;
    const message = data?.error || `HTTP ${res.status}`;
    throw new Error(`bridge error [${code}]: ${message}`);
  }
  if (!data || typeof data.answer !== 'string') {
    throw new Error('bridge returned an unexpected response shape (no "answer" field)');
  }
  return data;
}

// In-flight request tracking so a stdin close (client disconnect) doesn't drop a
// response that's still being produced by the bridge. We exit only once drained.
let inFlight = 0;
let stdinEnded = false;
const requestControllers = new Map();
function maybeExit() {
  if (stdinEnded && inFlight === 0) process.exit(0);
}

// --- request dispatch -------------------------------------------------------
async function handle(msg) {
  const { id, method, params } = msg;
  const isNotification = id === undefined || id === null;

  if (isNotification && method === 'notifications/cancelled') {
    requestControllers.get(params?.requestId)?.abort();
    return;
  }

  // JSON-RPC: a message with no id is a NOTIFICATION — it must receive no response at all.
  // `tools/call` additionally must not RUN: an id-less call starts a model run nobody can
  // correlate to a request, and a retrying client would run it twice.
  if (isNotification && method !== 'notifications/initialized' && method !== 'initialized') {
    log('warn', 'ignoring notification for a request-only method', { method });
    return;
  }

  switch (method) {
    case 'initialize': {
      // MCP lifecycle: when the client asks for a version we do not support, answer with one we DO
      // support so it can decide — never echo an arbitrary string back as though we spoke it.
      const clientProto = params?.protocolVersion;
      reply(id, {
        protocolVersion: clientProto === SUPPORTED_PROTOCOL ? clientProto : SUPPORTED_PROTOCOL,
        capabilities: { tools: {} },
        serverInfo: { name: SERVER_NAME, version: SERVER_VERSION },
      });
      return;
    }
    case 'notifications/initialized':
    case 'initialized':
      return; // notification, no response
    case 'ping':
      reply(id, {});
      return;
    case 'tools/list':
      reply(id, { tools: [{ name: TOOL_NAME, description: TOOL_DESC, inputSchema: INPUT_SCHEMA }] });
      return;
    case 'tools/call': {
      const name = params?.name;
      const args = params?.arguments || {};
      if (name !== TOOL_NAME) {
        replyError(id, -32602, `Unknown tool: ${name}`);
        return;
      }
      // Validate at the trust boundary (input is hostile until proven otherwise).
      const prompt = args.prompt;
      if (typeof prompt !== 'string' || prompt.trim().length === 0) {
        reply(id, { isError: true, content: [{ type: 'text', text: "Invalid arguments: 'prompt' must be a non-empty string." }] });
        return;
      }
      if (prompt.length > MAX_PROMPT) {
        reply(id, { isError: true, content: [{ type: 'text', text: `Invalid arguments: 'prompt' exceeds ${MAX_PROMPT} characters.` }] });
        return;
      }
      // The schema promises an ABSOLUTE path. Validate it here rather than letting an empty or
      // relative string fall through to defaultCwd, which would silently run in a different
      // project than the caller named.
      if (args.cwd !== undefined && (typeof args.cwd !== 'string' || !args.cwd.startsWith('/'))) {
        reply(id, { isError: true, content: [{ type: 'text', text: "Invalid arguments: 'cwd' must be an absolute path string." }] });
        return;
      }
      if (CFG.requireCwd && args.cwd === undefined) {
        reply(id, { isError: true, content: [{ type: 'text', text: "Invalid arguments: 'cwd' is required for this MCP server." }] });
        return;
      }
      if (args.sandbox !== undefined && !INPUT_SCHEMA.properties.sandbox.enum.includes(args.sandbox)) {
        reply(id, { isError: true, content: [{ type: 'text', text: "Invalid arguments: 'sandbox' must be a supported read-only mode." }] });
        return;
      }
      const invocationId = randomUUID();
      const started = process.hrtime.bigint();
      const receipt = usage => ({ invocationId, provider: CFG.provider,
        durationMs: Number((process.hrtime.bigint() - started) / 1_000_000n), usage: usage ?? null });
      const receiptContent = value => ({ type: 'text', text: `AI-MCP usage receipt: ${JSON.stringify(value)}` });
      inFlight++;
      const requestController = new AbortController();
      requestControllers.set(id, requestController);
      try {
        const t0 = process.hrtime.bigint();
        const data = await callBridge(prompt, args.cwd, args.sandbox, requestController.signal);
        const ms = Number((process.hrtime.bigint() - t0) / 1_000_000n);
        log('info', 'chat ok', { provider: CFG.provider, model: data.model, ms });
        const usageReceipt = receipt(data.usage);
        reply(id, { content: [{ type: 'text', text: data.answer }, receiptContent(usageReceipt)],
          structuredContent: { receipt: usageReceipt } });
      } catch (e) {
        log('warn', 'chat failed', { provider: CFG.provider, error: e.message });
        // Tool-level error → isError content so the model sees it (not a protocol error).
        const usageReceipt = receipt(null);
        reply(id, { isError: true, content: [{ type: 'text', text: e.message }, receiptContent(usageReceipt)],
          structuredContent: { receipt: usageReceipt } });
      } finally {
        requestControllers.delete(id);
        inFlight--;
        maybeExit();
      }
      return;
    }
    default:
      if (!isNotification) replyError(id, -32601, `Method not found: ${method}`);
      return;
  }
}

// --- stdin reader: newline-delimited JSON -----------------------------------
let buf = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', (chunk) => {
  buf += chunk;
  let nl;
  while ((nl = buf.indexOf('\n')) >= 0) {
    const line = buf.slice(0, nl).trim();
    buf = buf.slice(nl + 1);
    if (!line) continue;
    let msg;
    try {
      msg = JSON.parse(line);
    } catch {
      log('warn', 'dropping non-JSON line on stdin');
      continue;
    }
    Promise.resolve(handle(msg)).catch((e) => {
      log('error', 'unhandled dispatch error', { error: e?.message });
      if (msg && msg.id !== undefined && msg.id !== null) replyError(msg.id, -32603, 'Internal error');
    });
  }
});
process.stdin.on('end', () => {
  stdinEnded = true;
  maybeExit(); // exits now if idle; otherwise after the in-flight call settles
});

log('info', 'codex-bridge MCP shim started', {
  url: CFG.url,
  provider: CFG.provider,
  tool: TOOL_NAME,
  hasToken: Boolean(CFG.token),
});
