// E2E test for the codex-bridge MCP shim. Spawns the real shim process over stdio,
// drives it with actual MCP JSON-RPC frames, and points it at a MOCK bridge HTTP
// server (Node built-in http) so we exercise the full MCP<->HTTP wiring without
// needing a live codex/ChatGPT backend. Zero external deps (node:test).
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { readFileSync, readdirSync } from 'node:fs';
import http from 'node:http';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const HERE = dirname(fileURLToPath(import.meta.url));
const SHIM = resolve(HERE, '..', 'bridge-mcp.mjs');
const TOKEN = 'test-token-abc';

let server, baseUrl, lastRequest, requestCount = 0;

before(async () => {
  server = http.createServer((req, res) => {
    let body = '';
    req.on('data', (c) => (body += c));
    req.on('end', () => {
      requestCount++;
      lastRequest = { auth: req.headers.authorization, body: JSON.parse(body || '{}') };
      const prompt = lastRequest.body.prompt || '';
      // Mock the bridge's auth + happy/error behaviour.
      if (req.headers.authorization !== `Bearer ${TOKEN}`) {
        res.writeHead(401, { 'content-type': 'application/json' });
        res.end(JSON.stringify({ error: 'Unauthorized.', code: 'unauthorized' }));
        return;
      }
      if (prompt.includes('NOSANDBOX')) {
        res.writeHead(400, { 'content-type': 'application/json' });
        res.end(JSON.stringify({ error: "Sandbox 'read-only' cannot run for provider 'codex' on this bridge.", code: 'sandbox_unavailable' }));
        return;
      }
      if (prompt.includes('BOOM')) {
        res.writeHead(429, { 'content-type': 'application/json' });
        res.end(JSON.stringify({ error: 'slow down', code: 'rate_limited' }));
        return;
      }
      if (prompt.includes('SLOW')) {
        setTimeout(() => {
          if (!res.writableEnded) {
            res.writeHead(200, { 'content-type': 'application/json' });
            res.end(JSON.stringify({ answer: 'too late', provider: 'codex', model: 'gpt-5.5', sandbox: 'read-only', durationMs: 1000 }));
          }
        }, 1000);
        return;
      }
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ answer: `echo: ${prompt}`, provider: 'codex', model: 'gpt-5.5', sandbox: 'read-only', durationMs: 1, usage: prompt === 'USAGE' ? { source: 'codex.turn.completed', scope: 'per_call', models: [{ model: null, inputTokens: 100, outputTokens: 20, cachedInputTokens: 70, inputIncludesCache: true }], estimatedApiCostUsd: null, subscriptionCredits: null } : null }));
    });
  });
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  baseUrl = `http://127.0.0.1:${server.address().port}`;
});

after(() => server.close());

// Spawn the shim, send the given frames, collect one JSON response per frame that has an id.
function drive(frames, env = {}) {
  return new Promise((resolveP, reject) => {
    const child = spawn(process.execPath, [SHIM], {
      env: { ...process.env, AI_MCP_URL: baseUrl, AI_MCP_TOKEN: env.token ?? TOKEN, AI_MCP_PROVIDER: 'codex', ...env.extra },
      stdio: ['pipe', 'pipe', 'pipe'],
    });
    const wantIds = new Set(frames.filter((f) => f.id !== undefined).map((f) => f.id));
    const got = new Map();
    let out = '';
    child.stdout.setEncoding('utf8');
    child.stdout.on('data', (chunk) => {
      out += chunk;
      let nl;
      while ((nl = out.indexOf('\n')) >= 0) {
        const line = out.slice(0, nl).trim();
        out = out.slice(nl + 1);
        if (!line) continue;
        const msg = JSON.parse(line);
        if (msg.id !== undefined) got.set(msg.id, msg);
        if ([...wantIds].every((id) => got.has(id))) {
          child.kill();
          resolveP(got);
        }
      }
    });
    child.on('error', reject);
    setTimeout(() => { child.kill(); reject(new Error('shim timed out')); }, 5000);
    for (const f of frames) child.stdin.write(JSON.stringify(f) + '\n');
  });
}

test('initialize returns serverInfo + tools capability', async () => {
  const got = await drive([{ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-06-18' } }]);
  const r = got.get(1).result;
  assert.equal(r.serverInfo.name, 'ai-mcp-containers');
  assert.ok(r.capabilities.tools);
  assert.equal(r.protocolVersion, '2025-06-18');
});

test('tools/list advertises ask_codex with a prompt input', async () => {
  const got = await drive([{ jsonrpc: '2.0', id: 2, method: 'tools/list' }]);
  const tools = got.get(2).result.tools;
  assert.equal(tools.length, 1);
  assert.equal(tools[0].name, 'ask_codex');
  assert.deepEqual(tools[0].inputSchema.required, ['prompt']);
  assert.deepEqual(tools[0].inputSchema.properties.sandbox.enum, ['read-only']);
  // Callers must be told to leave it unset, and what a refusal looks like.
  assert.match(tools[0].inputSchema.properties.sandbox.description, /Leave it unset/);
  assert.match(tools[0].inputSchema.properties.sandbox.description, /sandbox_unavailable/);
});

test('a sandbox the bridge cannot honour reaches the caller as a typed error', async () => {
  const got = await drive([
    { jsonrpc: '2.0', id: 93, method: 'tools/call', params: { name: 'ask_codex', arguments: { prompt: 'NOSANDBOX', cwd: '/tmp', sandbox: 'read-only' } } },
  ]);
  assert.equal(lastRequest.body.sandbox, 'read-only');
  assert.equal(got.get(93).result.isError, true);
  assert.match(got.get(93).result.content[0].text, /\[sandbox_unavailable\]/);
});

test('tools/call happy path forwards prompt and returns the answer', async () => {
  const got = await drive([{ jsonrpc: '2.0', id: 3, method: 'tools/call', params: { name: 'ask_codex', arguments: { prompt: 'hello' } } }]);
  const r = got.get(3).result;
  assert.equal(r.isError, undefined);
  assert.equal(r.content[0].text, 'echo: hello');
  const receipt = r.structuredContent.receipt;
  assert.match(receipt.invocationId, /^[0-9a-f-]{36}$/);
  assert.equal(receipt.provider, 'codex');
  assert.equal(receipt.usage, null);
  assert.ok(receipt.durationMs >= 0);
  assert.deepEqual(JSON.parse(r.content[1].text.replace('AI-MCP usage receipt: ', '')), receipt);
  assert.equal(lastRequest.body.provider, 'codex');
  assert.match(lastRequest.body.conversationId, /^mcp-[0-9a-f-]{36}$/);
  assert.equal(lastRequest.auth, `Bearer ${TOKEN}`);
});

test('each tools/call gets a fresh conversation id', async () => {
  await drive([{ jsonrpc: '2.0', id: 31, method: 'tools/call', params: { name: 'ask_codex', arguments: { prompt: 'one' } } }]);
  const first = lastRequest.body.conversationId;
  await drive([{ jsonrpc: '2.0', id: 32, method: 'tools/call', params: { name: 'ask_codex', arguments: { prompt: 'two' } } }]);
  assert.notEqual(lastRequest.body.conversationId, first);
});

test('tools/call rejects empty prompt without hitting the bridge (400-shape)', async () => {
  const got = await drive([{ jsonrpc: '2.0', id: 4, method: 'tools/call', params: { name: 'ask_codex', arguments: { prompt: '   ' } } }]);
  const r = got.get(4).result;
  assert.equal(r.isError, true);
  assert.match(r.content[0].text, /non-empty string/);
});

test('tools/call surfaces bridge auth failure as isError, not a crash', async () => {
  const got = await drive(
    [{ jsonrpc: '2.0', id: 5, method: 'tools/call', params: { name: 'ask_codex', arguments: { prompt: 'hi' } } }],
    { token: 'wrong-token' },
  );
  const r = got.get(5).result;
  assert.equal(r.isError, true);
  assert.match(r.content[0].text, /unauthorized/i);
  assert.equal(r.structuredContent.receipt.usage, null);
  assert.match(r.structuredContent.receipt.invocationId, /^[0-9a-f-]{36}$/);
});

test('tools/call maps a typed bridge error (rate_limited) into isError content', async () => {
  const got = await drive([{ jsonrpc: '2.0', id: 6, method: 'tools/call', params: { name: 'ask_codex', arguments: { prompt: 'BOOM' } } }]);
  const r = got.get(6).result;
  assert.equal(r.isError, true);
  assert.match(r.content[0].text, /rate_limited/);
});

test('unknown tool name returns a JSON-RPC error', async () => {
  const got = await drive([{ jsonrpc: '2.0', id: 7, method: 'tools/call', params: { name: 'nope', arguments: {} } }]);
  assert.ok(got.get(7).error);
  assert.match(got.get(7).error.message, /Unknown tool/);
});

test('token is read from .env file when AI_MCP_TOKEN is unset', async () => {
  // Point AI_MCP_ENV_FILE at a fixture containing the token; clear AI_MCP_TOKEN.
  const fixture = resolve(HERE, 'fixture.env');
  const got = await drive(
    [{ jsonrpc: '2.0', id: 8, method: 'tools/call', params: { name: 'ask_codex', arguments: { prompt: 'fromfile' } } }],
    { token: '', extra: { AI_MCP_TOKEN: '', AI_MCP_ENV_FILE: fixture } },
  );
  const r = got.get(8).result;
  assert.equal(r.isError, undefined, JSON.stringify(r));
  assert.equal(r.content[0].text, 'echo: fromfile');
});

// ── EXACTLY TWO BRIDGES, BOTH GLOBAL (owner rule 2026-08-25) ────────────────────────────
// These three are the gate, not decoration. The previous model let this shim start a container
// per project; those piled up with nothing reaping them. Prose alone did not stop that, so the
// launcher machinery is gone and these tests fail loudly if any of it comes back.

// Walk the PRODUCTION tree (everything under mcp/ except this test dir). A gate that reads only
// one file is bypassed by moving the launch into a helper module; a gate that greps four literal
// strings is bypassed by `from 'child_process'` without the node: prefix, or by exec/fork.
function productionSources(dir, acc = []) {
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    if (e.name === 'test') continue; // the suite itself legitimately spawns processes
    const full = resolve(dir, e.name);
    if (e.isDirectory()) productionSources(full, acc);
    else acc.push(full);
  }
  return acc;
}

function stripComments(src) {
  return src
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .split('\n')
    .filter((l) => !/^\s*\/\//.test(l))
    .join('\n');
}

// The production tree is an ALLOWLIST. This is the control the pattern scan below cannot be:
// a scan is defeated by a computed specifier (`'child_' + 'process'`), createRequire, computed
// property access, or a launcher named `launcher.bash`. An allowlist is defeated by none of those,
// because adding ANY file at all fails until someone edits this list in the open.
const ALLOWLISTED_TREE = [
  'README.md',
  'bridge-mcp.mjs',
  'test/fixture.env',
  'test/mcp.test.mjs',
];

function walk(dir, base = dir, acc = []) {
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    const full = resolve(dir, e.name);
    if (e.isDirectory()) walk(full, base, acc);
    else acc.push(full.slice(base.length + 1));
  }
  return acc;
}

test('the whole mcp/ tree is exactly the allowlist — nothing can appear quietly, not even under test/', () => {
  // RECURSIVE and with no ignored directory: excluding test/ or node_modules/ would leave exactly
  // the hiding places a helper module needs. A new dependency tree failing this is correct, not a
  // nuisance — this shim is deliberately zero-dependency.
  assert.deepEqual(walk(resolve(HERE, '..')).sort(), [...ALLOWLISTED_TREE].sort());
});

// NOTE ON WHAT THIS NEXT TEST DOES *NOT* PROVE (the reviewer was right to reject the earlier claim):
// a source scan cannot establish "this can never start a process". It recognises the ordinary
// spellings; it does not defeat a determined obfuscation. It is a cheap first line. The allowlist
// above and the runtime-permission test below are what actually carry the weight.
test('no allowlisted source declares a process launch in any ordinary form', () => {
  const files = productionSources(resolve(HERE, '..'));

  // Kept as a cheap belt-and-braces alongside the allowlist above.
  const scripts = files.filter((f) => f.endsWith('.sh'));
  assert.deepEqual(scripts, [], `mcp/ must contain no shell script: ${scripts.join(', ')}`);

  // 2. No JS file may reach child_process by ANY spelling, or name docker directly.
  const FORBIDDEN = [
    /from\s+['"](?:node:)?child_process['"]/,
    /require\(\s*['"](?:node:)?child_process['"]\s*\)/,
    /\bimport\(\s*['"](?:node:)?child_process['"]\s*\)/,
    // Not preceded by a dot or word char, so `someRegex.exec(line)` — a legitimate, unrelated
    // API — does not read as a process launch.
    /(?<![.\w])(?:spawn|spawnSync|exec|execSync|execFile|execFileSync|fork)\s*\(/,
    // Narrow on purpose: a help string may say "run docker compose up -d". What must not appear
    // is a CONSTRUCTED container command. Without child_process above, none of these can run
    // anyway — this is the second line, not the first.
    /docker\s+(?:run|start|create)\b/,
  ];
  for (const f of files.filter((f) => /\.(mjs|js|cjs)$/.test(f))) {
    const code = stripComments(readFileSync(f, 'utf8'));
    for (const re of FORBIDDEN) {
      assert.ok(
        !re.test(code),
        `${f} matches ${re}: a production file that can start a process brings per-project bridges back`,
      );
    }
  }
});

test('the shim REALLY defaults to the two fixed endpoints (observed, not grepped)', async () => {
  // Behavioural, deliberately: the previous version asserted the two URL strings appeared somewhere
  // in the source, which the comments alone satisfied — the defaults could change and stay green.
  // Here the shim is started with no AI_MCP_URL and we read the endpoint it actually resolved.
  for (const [provider, expected] of [['codex', 'http://127.0.0.1:8788'], ['claude', 'http://127.0.0.1:8789']]) {
    const env = { ...process.env, AI_MCP_PROVIDER: provider, AI_MCP_TOKEN: TOKEN };
    delete env.AI_MCP_URL;
    const child = spawn(process.execPath, [SHIM], { env, stdio: ['pipe', 'pipe', 'pipe'] });
    const url = await new Promise((res, rej) => {
      let buf = '';
      const timer = setTimeout(() => rej(new Error(`no startup log for ${provider}`)), 5000);
      child.stderr.setEncoding('utf8');
      child.stderr.on('data', (c) => {
        buf += c;
        for (const line of buf.split('\n').filter(Boolean)) {
          try {
            const m = JSON.parse(line);
            if (m.msg?.includes('shim started')) { clearTimeout(timer); res(m.url); }
          } catch { /* partial line */ }
        }
      });
    }).finally(() => child.kill());
    assert.equal(url, expected, `${provider} must default to ${expected}`);
  }
});

test('cwd is forwarded verbatim — no routing step, no Git-worktree requirement', async () => {
  const got = await drive([
    { jsonrpc: '2.0', id: 9, method: 'tools/call', params: { name: 'ask_codex', arguments: { prompt: 'review', cwd: '/tmp' } } },
  ]);
  assert.equal(got.get(9).result.isError, undefined, JSON.stringify(got.get(9)));
  assert.equal(lastRequest.body.cwd, '/tmp');
});

test('sandbox is validated and forwarded explicitly', async () => {
  const got = await drive([
    { jsonrpc: '2.0', id: 91, method: 'tools/call', params: { name: 'ask_codex', arguments: { prompt: 'read', cwd: '/tmp', sandbox: 'read-only' } } },
  ]);
  assert.equal(got.get(91).result.isError, undefined, JSON.stringify(got.get(91)));
  assert.equal(lastRequest.body.sandbox, 'read-only');

  const bad = await drive([
    { jsonrpc: '2.0', id: 92, method: 'tools/call', params: { name: 'ask_codex', arguments: { prompt: 'edit', cwd: '/tmp', sandbox: 'danger-full-access' } } },
  ]);
  assert.equal(bad.get(92).result.isError, true);
  assert.match(bad.get(92).result.content[0].text, /sandbox/);
});

test('a call with no cwd still reaches the bridge, which applies its own default', async () => {
  const got = await drive([
    { jsonrpc: '2.0', id: 10, method: 'tools/call', params: { name: 'ask_codex', arguments: { prompt: 'nocwd' } } },
  ]);
  assert.equal(got.get(10).result.isError, undefined, JSON.stringify(got.get(10)));
  assert.equal(lastRequest.body.cwd, undefined);
});

test('AI_MCP_REQUIRE_CWD makes cwd a schema requirement and rejects an omitted cwd', async () => {
  const got = await drive(
    [
      { jsonrpc: '2.0', id: 101, method: 'tools/list' },
      { jsonrpc: '2.0', id: 102, method: 'tools/call', params: { name: 'ask_codex', arguments: { prompt: 'nocwd' } } },
    ],
    { extra: { AI_MCP_REQUIRE_CWD: 'true' } },
  );
  assert.deepEqual(got.get(101).result.tools[0].inputSchema.required, ['prompt', 'cwd']);
  assert.equal(got.get(102).result.isError, true);
  assert.match(got.get(102).result.content[0].text, /cwd.*required/i);
});

test('the claude provider is forwarded as claude', async () => {
  const got = await drive(
    [{ jsonrpc: '2.0', id: 11, method: 'tools/call', params: { name: 'ask_claude', arguments: { prompt: 'review', cwd: '/mnt/projects' } } }],
    { extra: { AI_MCP_PROVIDER: 'claude' } },
  );
  assert.equal(got.get(11).result.isError, undefined, JSON.stringify(got.get(11)));
  assert.equal(lastRequest.body.provider, 'claude');
  assert.equal(lastRequest.body.cwd, '/mnt/projects');
});

// --- docker-compose volume parsing, used by the three mount-invariant tests below --------------
// SCOPE, stated honestly: this parses the SUBSET of YAML this one file actually uses; it is not a
// YAML implementation and it reads ONE file. It is a regression guard against a careless edit
// reintroducing a root-creating or over-broad mount — NOT proof of what the running containers
// mount. Accepted as outside its reach: anchors/aliases and merge keys, `-f` override files,
// values resolved from the environment, symlinked host paths, and live container state; the
// ground truth for those is `docker compose config` and `docker inspect`. Anything this parser
// cannot classify — a service key, a volumes form, a mount entry, a named-volume definition —
// FAILS rather than passing
// quietly, within the one file it reads.
// Hand-written because the shim tree is deliberately dependency-free, and deliberately STRICT:
// anything it cannot classify is a failure, so a new mount form cannot slip past by being novel.
function splitKV(text) {
  const m = /^\s*-?\s*([a-z_]+):\s*(.*)$/.exec(text);
  if (!m) return null;
  let v = m[2].replace(/\s+#.*$/, '').trim();       // strip an inline comment: `false # why`
  v = v.replace(/^(["'])(.*)\1$/, '$2');            // strip quotes: `"bind"` -> bind
  return [m[1], v];
}

// One list item, with its keys kept PATH-QUALIFIED (`bind.create_host_path`, not
// `create_host_path`). Flattening would let an unrelated nested block — a compose `x-` extension,
// say — silently overwrite the value the real one declares.
function parseItem(lines, from, itemIndent) {
  const keys = {};
  const stack = [];                                  // [{ indent, name }]
  let scalar;
  const first = splitKV(lines[from]);
  if (first) keys[first[0]] = first[1];
  else scalar = /^\s*-\s*(.*)$/.exec(lines[from])[1];
  if (first && first[1] === '') stack.push({ indent: itemIndent, name: first[0] });
  let i = from + 1;
  for (; i < lines.length; i++) {
    if (!lines[i].trim()) continue;
    if (/^\s*#/.test(lines[i])) continue;
    const ind = lines[i].search(/\S/);
    if (ind <= itemIndent) break;
    while (stack.length && ind <= stack[stack.length - 1].indent) stack.pop();
    const kv = splitKV(lines[i]);
    if (!kv) {
      // An un-modelled nested block — a compose `x-` extension, say. Push an opaque frame so its
      // children cannot be mistaken for the real ones (`bind.create_host_path` stays the only
      // path that counts). Dropping the frame instead is how a nested block silently overwrites.
      if (/^\s*["']?[a-z][a-z0-9_.-]*["']?:\s*(#.*)?$/.test(lines[i])) stack.push({ indent: ind, name: '?' });
      continue;
    }
    const path = stack.map((f) => f.name).concat(kv[0]).join('.');
    keys[path] = kv[1];
    if (kv[1] === '') stack.push({ indent: ind, name: kv[0] });
  }
  return { keys, scalar, next: i };
}

// Entries of ONE service's `volumes:` list. Never the file's top-level `volumes:` key.
function volumeEntries(serviceBlock) {
  const lines = serviceBlock.split('\n');
  // The property indent is DERIVED, never assumed to be four spaces: six-space properties are
  // ordinary block YAML, and a hard-coded width silently reports "this service has no volumes".
  const hits = lines.map((l, i) => (/^\s+["']?volumes["']?:/.test(l) ? i : -1)).filter((i) => i !== -1);
  if (hits.length === 0) return [];
  // More than one `volumes:` key in one service means either a duplicate key or — the case that
  // actually bit — a block this parser failed to recognise as a separate service, whose lines got
  // appended to this one. Taking the first and ignoring the rest would hide exactly that.
  assert.equal(hits.length, 1, 'a service declares ' + hits.length + ' volumes: keys; expected 1');
  const at = hits[0];
  const base = lines[at].search(/\S/);          // this service's own property indent
  // FAIL CLOSED on any form this parser does not model. An inline flow sequence
  // (`volumes: ["/:/host"]`) would otherwise read as "this service has no volumes" and the whole
  // service would escape inspection — silence is the dangerous answer here.
  assert.match(lines[at], /^\s+["']?volumes["']?:\s*(#.*)?$/,
    'volumes: must be a plain block sequence this parser can inspect, found: ' + lines[at].trim());
  const entries = [];
  for (let i = at + 1; i < lines.length; ) {
    const l = lines[i];
    if (!l.trim() || /^\s*#/.test(l)) { i++; continue; }   // comments at ANY indent, including the
    const ind = l.search(/\S/);                            // block header level with `volumes:`
    const isItem = /^\s*-\s/.test(l);
    if (!isItem && ind <= base) break;                     // the service's next key ends the list
    if (isItem && ind < base) break;                       // dedented out of this service entirely
    if (!isItem) { i++; continue; }
    const item = parseItem(lines, i, ind);                 // ind === base is the indentless form
    entries.push(item);
    i = item.next;
  }
  return entries;
}

// EVERY service in the file, not only the two that declare bridge roots — a third service with a
// wide-open bind would otherwise never be inspected at all.
function allServices() {
  const compose = readFileSync(resolve(HERE, '..', '..', 'docker-compose.yml'), 'utf8');
  const lines = compose.split('\n');
  const at = lines.findIndex((l) => /^services:\s*$/.test(l));
  assert.notEqual(at, -1, 'docker-compose.yml declares no services');
  const out = {};
  let name = null, buf = [];
  for (let i = at + 1; i < lines.length; i++) {
    // ANY service key, not a guessed charset: compose service names legitimately contain dots and
    // capitals (`backup.v2`), and a name this regex missed used to make the whole service — and
    // its mounts — invisible instead of failing.
    const svc = /^ {2}["']?([^\s:#][^:]*?)["']?:\s*(?:#.*)?$/.exec(lines[i]);
    const topLevel = /^[a-z]/.test(lines[i]);
    if (svc || topLevel) {
      if (name) out[name] = buf.join('\n');
      if (topLevel) return out;
      name = svc[1]; buf = [];
      continue;
    }
    if (name) { buf.push(lines[i]); continue; }
    // Before the first recognised service, nothing but blanks and comments may appear. A line
    // here means a service key this parser failed to recognise, whose mounts would be dropped.
    assert.match(lines[i], /^\s*(#.*)?$/,
      'unrecognised line under services: before any service key: ' + JSON.stringify(lines[i]));
  }
  if (name) out[name] = buf.join('\n');
  return out;
}

// `${VAR:?message}` and `${VAR}` name the same thing; compare on the normalised form so an edited
// error message is not a test failure, while a changed PATH still is.
// ONLY the `:?message` form is normalised: it FAILS when the variable is unset, so its message
// is cosmetic. `:-default` and `:+alt` SUBSTITUTE a value instead — `${EXAMPLE_UNSAFE_ROOT:-/}`
// would mount / when unset — so they stay in the compared string and must be justified in the
// inventory table rather than normalised away.
const normVar = (v) => (v || '').replace(/\$\{([A-Z_]+):\?[^}]*\}/g, '${$1}');

test('no volume in ANY service can make docker create a host path', () => {
  // With short `src:dst:ro` syntax docker CREATES a missing host source, and dockerd runs as root,
  // so the directory lands as root:root. That is how a boot-time restart of these bridges produced
  // a root-owned /tmp/claude-1000 and made the host's own Claude Code refuse to start ("Temp
  // directory ... is owned by uid 0, expected 1000"). Rejecting the short form outright is the
  // point: a rule that only inspects long-syntax entries is bypassed by writing it the old way.
  const services = allServices();
  assert.ok(Object.keys(services).length >= 2, 'expected at least the two bridge services');
  for (const [name, block] of Object.entries(services)) {
    for (const e of volumeEntries(block)) {
      if (e.scalar !== undefined) {
        // Only a NAMED volume may be a bare scalar: `name:/container/path`, never a host path.
        assert.match(
          e.scalar, /^[a-z][a-z0-9_-]*:\/[^:]*$/,
          name + ': short-syntax host bind is forbidden (docker would create the source as root): ' + e.scalar,
        );
        continue;
      }
      assert.equal(e.keys.type, 'bind', name + ': unrecognised volume entry: ' + JSON.stringify(e.keys));
      assert.equal(
        e.keys['bind.create_host_path'], 'false',
        name + ': bind ' + e.keys.source + ' must set bind.create_host_path: false (found ' +
          e.keys['bind.create_host_path'] + ')',
      );
    }
  }
});

test('no named volume is a bind in disguise', () => {
  // A named volume can BE a host bind: `driver_opts: {type: none, o: bind, device: /}` mounts a
  // host path while the service entry still reads as an innocuous `name:/container/path`. The
  // service-level tests deliberately allow bare named volumes, so the definition itself must be
  // plain: a name and nothing else. Any option here is a mount decision and fails until justified.
  const lines = readFileSync(resolve(HERE, '..', '..', 'docker-compose.yml'), 'utf8').split('\n');
  // Find the KEY independently of its value, then fail closed on any form not modelled — an
  // inline mapping (`volumes: {name: {driver_opts: ...}}`) would otherwise read as "no named
  // volumes here" and skip the whole section. Ordinary YAML, so it must fail, not vanish.
  const at = lines.findIndex((l) => /^["']?volumes["']?:/.test(l));
  if (at === -1) return;                                   // no named volumes at all
  assert.match(lines[at], /^["']?volumes["']?:\s*(#.*)?$/,
    'the top-level volumes: key must be a plain block mapping this test can inspect, found: ' +
    lines[at].trim());
  for (let i = at + 1; i < lines.length; i++) {
    if (!lines[i].trim() || /^\s*#/.test(lines[i])) continue;
    if (/^\S/.test(lines[i])) break;                       // next top-level key ends the section
    assert.match(
      lines[i], /^ {2}["']?[^\s:#][^:]*["']?:\s*(#.*)?$/,
      'a named volume must be a bare name with no options — an option like driver_opts can make it\n' +
      'a host bind in disguise: ' + JSON.stringify(lines[i]),
    );
  }
});

test('the bind inventory of each bridge service is EXACTLY the declared one', () => {
  // An allow-list, not a spot-check. Any bind that is added, removed, re-pathed, moved between
  // services, or has its read-only flag flipped fails here and has to be justified by editing this
  // table — which is the review step. `${VAR:?msg}` is normalised to `${VAR}` first.
  const ROOTS = ['AI_MCP_PROJECT_ROOT'];
  const sameP = (v) => ['${' + v + '}', '${' + v + '}', true];   // [source, target, readOnly]
  const expected = {
    'codex-mcp': [
      ['${AI_MCP_CODEX_EXT_BIN}', '/opt/codex', true],
      ['${HOME}/.codex', '${HOME}/.codex', false],                 // shared auth.json: rotation
      // config stays inside the shared directory so atomic editor saves remain visible.
      ...ROOTS.map(sameP),
    ],
    'claude-mcp': [
      ['${HOME}/.claude', '/seed', true],                          // host auth seed, never written
      ...ROOTS.map(sameP),
      ['${AI_MCP_CLAUDE_EXTENSIONS}', '/vscode-extensions', true],
    ],
  };
  const services = allServices();
  for (const name of Object.keys(expected)) {
    assert.ok(services[name], 'service ' + name + ' is missing from docker-compose.yml');
  }
  // EVERY service, not only the two in the table: a service absent from it must declare NO host
  // bind at all, so adding one is a deliberate edit here rather than a silent new disclosure.
  for (const [name, block] of Object.entries(services)) {
    const want = expected[name] || [];
    const got = volumeEntries(block)
      .filter((e) => e.keys.type === 'bind')
      .map((e) => [normVar(e.keys.source), normVar(e.keys.target), e.keys.read_only === 'true']);
    assert.deepEqual(
      got.slice().sort(), want.slice().sort(),
      name + ': bind inventory drifted. Every host bind is a disclosure decision — if this change is\n' +
      'intended, update the table in this test deliberately rather than making the test looser.',
    );
  }
});

test('every declared bridge root is mounted read-only at the SAME path in BOTH services', () => {
  // The allow-list (AI_MCP_ALLOWED_ROOTS) and the mount list are different lines. If they drift, a
  // root is allowed but not mounted (calls fail on a missing directory) or mounted without being
  // allowed (a silent grant nobody declared).
  const services = allServices();
  const bridges = Object.entries(services);
  assert.equal(bridges.length, 2, 'expected exactly two services declaring roots');
  const compose = readFileSync(resolve(HERE, '..', '..', 'docker-compose.yml'), 'utf8');
  const rootsLine = compose.split('\n').find(l => l.trim().startsWith('AI_MCP_ALLOWED_ROOTS:'));
  const declared = [...rootsLine.matchAll(/\$\{(AI_MCP_(?:PROJECT|REVIEW)_ROOT)/g)].map(m => m[1]);
  assert.deepEqual(declared, ['AI_MCP_PROJECT_ROOT']);
  for (const [name, block] of bridges) {
    assert.ok(block.includes('<<: *environment'), name + ': common roots must be inherited');
    const binds = volumeEntries(block).filter((e) => e.keys.type === 'bind');

    for (const v of declared) {
      const hits = binds.filter((b) => normVar(b.keys.source) === '${' + v + '}' && b.keys.target === '${' + v + '}');
      assert.equal(hits.length, 1, name + ': ' + v + ' must be mounted at the same path exactly once (found ' + hits.length + ')');
      assert.equal(hits[0].keys.read_only, 'true', name + ': ' + v + ' must be mounted read_only');
    }
    for (const b of binds) {
      const m = /\$\{(BRIDGE_ROOT_[A-Z_]+)/.exec(b.keys.source || '');
      if (m) assert.ok(declared.includes(m[1]), name + ': ' + m[1] + ' is mounted but never declared in AI_MCP_ALLOWED_ROOTS');
    }
  }
});

test('the shim runs with child-process creation DENIED by the Node permission model', async () => {
  // This is the one real enforcement available here: `--experimental-permission` makes Node refuse
  // to create a child process at all (ERR_ACCESS_DENIED), regardless of how the code asks. Proving
  // the shim serves a full tools/call under it establishes that it needs no such capability — so a
  // deployment can hard-deny it, and a reintroduced launcher would fail at runtime, not just in a
  // grep. Register the MCP server with these flags to get that guarantee in production.
  const child = spawn(
    process.execPath,
    ['--experimental-permission', '--allow-fs-read=*', SHIM],
    { env: { ...process.env, AI_MCP_URL: baseUrl, AI_MCP_TOKEN: TOKEN, AI_MCP_PROVIDER: 'codex' }, stdio: ['pipe', 'pipe', 'pipe'] },
  );
  for (const f of [
    { jsonrpc: '2.0', id: 41, method: 'initialize', params: { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 't', version: '1' } } },
    { jsonrpc: '2.0', id: 42, method: 'tools/call', params: { name: 'ask_codex', arguments: { prompt: 'permissioned' } } },
  ]) child.stdin.write(JSON.stringify(f) + '\n');

  const answer = await new Promise((res, rej) => {
    let buf = '';
    const timer = setTimeout(() => rej(new Error('no reply under permission model')), 15000);
    child.stdout.setEncoding('utf8');
    child.stdout.on('data', (c) => {
      buf += c;
      for (const line of buf.split('\n').filter(Boolean)) {
        try {
          const m = JSON.parse(line);
          if (m.id === 42) { clearTimeout(timer); res(m.result); }
        } catch { /* partial line */ }
      }
    });
  }).finally(() => child.kill());
  assert.equal(answer.isError, undefined, JSON.stringify(answer));
  assert.equal(answer.content[0].text, 'echo: permissioned');
  child.kill();
});

test('an id-less tools/call neither runs nor replies (JSON-RPC notification)', async () => {
  // B2 regression: an id-less call used to start a model run nobody could correlate, then emit a
  // response with no id. Both are protocol violations and the run is billable.
  const before = requestCount;
  const got = await drive([
    { jsonrpc: '2.0', method: 'tools/call', params: { name: 'ask_codex', arguments: { prompt: 'must not run' } } },
    { jsonrpc: '2.0', id: 77, method: 'ping' },
  ]);
  assert.equal(requestCount, before, 'an id-less tools/call must not reach the bridge');
  assert.deepEqual([...got.keys()], [77], 'only the identified request may get a response');
});

test('notifications/cancelled aborts the matching in-flight bridge request', async () => {
  const got = await drive([
    { jsonrpc: '2.0', id: 81, method: 'tools/call', params: { name: 'ask_codex', arguments: { prompt: 'SLOW', cwd: '/tmp' } } },
    { jsonrpc: '2.0', method: 'notifications/cancelled', params: { requestId: 81, reason: 'client timeout' } },
    { jsonrpc: '2.0', id: 82, method: 'ping' },
  ]);
  assert.equal(got.get(81).result.isError, true);
  assert.match(got.get(81).result.content[0].text, /cancelled by MCP client/);
  assert.deepEqual(got.get(82).result, {});
});

test('workspace write is rejected by the MCP adapter without reaching the service', async () => {
  const before = requestCount;
  const got = await drive([{jsonrpc: '2.0', id: 191, method: 'tools/call',
    params: {name: 'ask_codex', arguments: {prompt: 'edit', sandbox: 'workspace-write'}}}]);
  assert.equal(got.get(191).result.isError, true);
  assert.equal(requestCount, before);
});

test('deprecated client launcher settings preserve provider routing during migration', async () => {
  const got = await drive([{jsonrpc: '2.0', id: 192, method: 'tools/call',
    params: {name: 'ask_claude', arguments: {prompt: 'legacy launcher'}}}],
    {extra: {AI_MCP_URL: undefined, AI_MCP_TOKEN: undefined, AI_MCP_PROVIDER: undefined,
      BRIDGE_URL: baseUrl, BRIDGE_TOKEN: TOKEN, BRIDGE_PROVIDER: 'claude'}});
  assert.equal(got.get(192).result.isError, undefined);
  assert.equal(lastRequest.auth, `Bearer ${TOKEN}`);
  assert.equal(lastRequest.body.provider, 'claude');
});


test('tools/call preserves native usage and emits distinct deduplicatable invocation receipts', async () => {
  const frames = [61, 62].map(id => ({ jsonrpc: '2.0', id, method: 'tools/call',
    params: { name: 'ask_codex', arguments: { prompt: 'USAGE' } } }));
  const got = await drive(frames);
  const first = got.get(61).result;
  const second = got.get(62).result;
  const receipt = first.structuredContent.receipt;
  assert.equal(first.content[0].text, 'echo: USAGE');
  assert.equal(receipt.usage.models[0].inputTokens, 100);
  assert.equal(receipt.usage.subscriptionCredits, null);
  assert.equal(receipt.usage.scope, 'per_call');
  assert.notEqual(receipt.invocationId, second.structuredContent.receipt.invocationId);
  assert.deepEqual(JSON.parse(first.content[1].text.replace('AI-MCP usage receipt: ', '')), receipt);
});
