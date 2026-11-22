# AI-MCP-Containers MCP adapter

`bridge-mcp.mjs` is a zero-dependency stdio MCP adapter for an already-running
AI-MCP-Containers service. It never starts containers. There are exactly two global
provider endpoints: Codex on `127.0.0.1:8788`, Claude on `127.0.0.1:8789`.

Configure the MCP launcher with these names:

| Variable | Meaning |
| --- | --- |
| `AI_MCP_PROVIDER` | `codex` (default) or `claude` |
| `AI_MCP_URL` | Service URL; defaults to the provider's localhost port |
| `AI_MCP_ENV_FILE` | Path to the private service `.env`; defaults beside this repository |
| `AI_MCP_TOKEN` | Optional explicit service key; otherwise read from that `.env` |
| `AI_MCP_TOOL_NAME` | Default `ask_codex` or `ask_claude` |
| `AI_MCP_TOOL_DESC` | Optional tool description |
| `AI_MCP_DEFAULT_CWD` | Optional client-side working-directory override |
| `AI_MCP_REQUIRE_CWD` | Require callers to supply a working directory |
| `AI_MCP_TIMEOUT_MS` | Positive request timeout; default 300000 milliseconds |

The server-side Compose configuration has one project path: `AI_MCP_PROJECT_ROOT`.
It is mounted read-only in both containers. The service defaults to this directory;
`cwd` can select an existing descendant. A parent mount exposes all readable files
beneath it. Neither a prompt nor `cwd` establishes a per-project confidentiality
boundary. Keep credentials and unrelated projects outside selected roots where possible.

Project writes are disabled. The adapter advertises `read-only`, plus `read-only-web`
for Claude. Omit `sandbox` for the service default. If Codex's native sandbox cannot
start, explicitly requesting `read-only` returns `sandbox_unavailable`; the operator
may use its container-enforced default instead. Claude's read-only CLI uses safe mode
and a `Read,Grep` allowlist; web mode adds `WebFetch,WebSearch`. Neither mode grants
file-edit or shell tools. Do not treat this as complete credential isolation.

Each MCP call receives an independent conversation ID and supports cancellation.
The supplied deployment disables provider conversation reuse; send the context needed
for the current request in its prompt.

The canonical Docker network is `ai-mcp-shared`. A client on that network can set
`AI_MCP_URL` to `http://codex-mcp:8080` or `http://claude-mcp:8080`. Host clients can
keep the localhost defaults. With `AI_MCP_LAN_IP=auto`, service startup discovers the
local Linux Docker host's physical private IPv4 addresses. Remote clients can use any
selected host address with port `8788` or `8789` and their explicit `AI_MCP_TOKEN`.
Set one private IPv4 to restrict LAN access or leave the setting empty to disable it.
Run `./run-docker.sh` again after host address changes; discovery is not continuous.
The key is required on every network; HTTP requires a TLS proxy or private tunnel
when the network is untrusted.
The HTTP service additionally limits total simultaneous requests across all IDs.
For Claude's 1200-second provider timeout, use a larger adapter/host timeout so the
provider can return its typed failure before the outer transport cancels it.

Old MCP launcher aliases beginning with `BRIDGE_` are accepted only as a migration
fallback when the matching `AI_MCP_*` value is absent. New launcher configurations
and the service `.env` must use `AI_MCP_*`.

Run affected adapter/Compose-inventory tests with:

```sh
node --test mcp/test/mcp.test.mjs
```

The test fixture contains a fake key and talks to a local mock HTTP service; it does
not use subscriptions. Reuse unchanged passing cases instead of rerunning them.
