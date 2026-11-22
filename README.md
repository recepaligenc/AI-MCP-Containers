# AI-MCP-Containers

Two global Docker services use the host owner's local Codex/ChatGPT and Claude Code
subscriptions. Each service exposes `/chat`, `/v1/responses` and an MCP stdio adapter.
No provider API key is required. The service's Bearer key is separate from provider
authentication; all callers share this installation's accounts and subscription quotas.

## Configure and start

Use a Linux host with Docker Compose, Node.js, rsync and compatible Linux provider
binaries from VS Code. Sign into the native Codex and Claude Code CLIs on the host.
Codex's updater supports Linux x64; Claude's launcher supports Linux x64 and arm64.
macOS and Windows extension executables cannot run in these Linux containers.

Copy `.env.example` to the ignored private `.env` and fill in these settings:

| Setting | Purpose |
| --- | --- |
| `AI_MCP_TOKEN` | Private service key, at least 32 characters. Generate with `openssl rand -hex 32`. |
| `AI_MCP_PROJECT_ROOT` | One existing absolute host directory for project and review access. |
| `AI_MCP_HOST_UID`, `AI_MCP_HOST_GID` | Host credential owner's IDs, from `id -u` and `id -g`. Use a non-root owner. |
| `AI_MCP_CODEX_EXT_BIN` | Dedicated stable Codex binary directory, normally `${HOME}/.codex-ext-bin`. |
| `AI_MCP_CLAUDE_EXTENSIONS` | Host VS Code extensions directory, normally `${HOME}/.vscode/extensions`. |
| `AI_MCP_MAX_CONCURRENT_REQUESTS` | Maximum active requests per provider; default `6`. |
| `AI_MCP_LAN_IP` | `auto` discovers the host's physical private IPv4 addresses at startup. Set one host IPv4 to restrict access, or leave empty to disable LAN access. |

Create the shared network once, prepare Codex's stable binary copy, then start:

```sh
docker network create ai-mcp-shared
./refresh-codex-bin.sh
./run-docker.sh --build
```

Skip network creation if `ai-mcp-shared` already exists. The startup wrapper validates
the resolved configuration before applying it. It preserves an existing ignored
`docker-compose.override.yml` and generates the LAN mappings from the selected host
addresses. The same resolved address list is checked and applied in that invocation.
Missing bind directories are never created automatically.

## Network access

| Client location | Codex | Claude |
| --- | --- | --- |
| This host | `http://127.0.0.1:8788` | `http://127.0.0.1:8789` |
| Docker container on `ai-mcp-shared` | `http://codex-mcp:8080` | `http://claude-mcp:8080` |
| LAN, for each selected host address | `http://<host-LAN-IP>:8788` | `http://<host-LAN-IP>:8789` |

Attach a client container to the external `ai-mcp-shared` network in its own Compose
configuration. LAN access supplements localhost access; existing local MCP clients
keep working. The preflight rejects wildcard/public bindings, Docker bridge addresses,
addresses not assigned to this host, and mappings that remove localhost access.

The example enables `AI_MCP_LAN_IP=auto`. On a Linux host using a local Docker Unix
socket, startup discovers RFC1918 IPv4 addresses on active physical network interfaces,
including Ethernet and Wi-Fi. Docker bridges, VPN/tunnel and other virtual interfaces
are excluded. Each discovered address receives its own port mappings; no wildcard
listener is added. TCP/SSH Docker endpoints and Docker Desktop are rejected in automatic
mode because local interface discovery cannot determine their daemon host's addresses.
Do not forward a remote daemon through a local Unix socket: automatic discovery assumes
the daemon runs on the same Linux machine as this checkout.
If no suitable address is found, only localhost and Docker-network access remain.

Automatic mode can expose the service on multiple connected private networks. To
choose one, set a single assigned private IPv4 instead; an empty value disables LAN
access. Addresses are discovered at startup, not continuously: run `./run-docker.sh`
again after a DHCP or network change to update the container port mappings.

Send `Authorization: Bearer <AI_MCP_TOKEN>` on every API request, including requests
from Docker peers and LAN clients. `/health` and the static UI are public. The UI
uses the service's default policy unless the user explicitly selects a supported
read-only mode. HTTP does not encrypt the key or traffic; use a TLS reverse proxy or
private tunnel across untrusted networks. A shared key does not identify individual users.

## Security layers and limits

| Layer | What it enforces |
| --- | --- |
| Authentication | Startup refuses missing or short keys. API routes require a valid Bearer key by default, including case and trailing-slash variants. |
| Project mounts | Only the selected project tree is mounted for project access, read-only in both containers. The API rejects `workspace-write`. |
| Working directory | A request's `cwd` must exist under the allowed root. Symbolic-link components are resolved before containment checks. |
| Runtime | Read-only root filesystem, non-root application/provider processes, dropped capabilities, `no-new-privileges` and Docker's default seccomp profile. |
| Resource limits | Each provider has a global concurrency limit, 2 CPUs, 2 GiB memory and 256 PIDs. Different conversation IDs cannot bypass concurrency control. |
| Request state | The supplied Compose deployment does not resume or persist provider conversations. Clients supply context in each request. |
| Child environment | Service configuration and the Bearer key are removed from provider-process environments; the launcher retains its required extension path. |
| Model instructions | Every provider call receives fixed English instructions to avoid reading or exposing `.env`, private keys and credentials, including through Git history or alternate paths. This is behavioral guidance, not an enforced file-access restriction. |

The resource preflight rejects privileged/host-namespace overrides, extra capabilities,
unexpected bind sources and writable project mounts. It catches configuration mistakes;
it does not protect against a host administrator deliberately changing the deployment.

`AI_MCP_PROJECT_ROOT` grants access to the entire selected tree. A parent such as
`/mnt/projects` exposes other descendant projects and readable `.env` files. `cwd`
chooses a starting directory; it does not isolate files that are already mounted.
Choose a narrow root and keep real secrets outside it when confidentiality is required.
There is no AppArmor setup, filtered project copy or per-request directory scan.

Read-only mounts protect files from modification, not from being read. Native provider
credentials remain accessible to the provider runtime. Codex can use `danger-full-access`
internally when its native sandbox cannot run; Docker's read-only project mount remains
the project-write boundary. These containers are for trusted callers, not an execution
sandbox for untrusted prompts or mutually untrusted users. Model instructions and child
environment cleanup do not establish an operating-system secrecy boundary.

## Subscriptions and automatic updates

Codex shares the host `~/.codex` directory so atomic OAuth refreshes remain coordinated.
The service never prunes host-owned session files. Claude reads the host `~/.claude`
credential seed and uses a private writable runtime volume. Credential refresh and
runtime state require writes; project directories remain read-only. Provider quota
limits are returned as typed errors, including `429` and a retry time when available.

Claude selects the newest compatible stable extension on every invocation. Codex uses
a stable copy updated with rename-safe synchronization so active calls can finish.
The Codex binary copy and Claude extension source are mounted read-only in the containers.
Install the optional user updater and credential-directory recovery units with:

```sh
node scripts/install-user-services.mjs --install
```

The updater refuses unsafe destinations before synchronizing. See
[systemd/README.md](systemd/README.md), [CLAUDE-RUNTIME.md](CLAUDE-RUNTIME.md) and
[mcp/README.md](mcp/README.md) for provider, updater and client details.

## Migration and verification

Application names are `AiMcpContainers` and `AiMcp`; active environment names use
`AI_MCP_*`. Provider-native `CODEX_HOME`, `HOME` and `CLAUDE_CODE_TMPDIR` retain their
native meanings. The MCP adapter temporarily accepts deprecated client launcher aliases.

The canonical shared network is `ai-mcp-shared`. An existing installation can retain
`codex-shared` temporarily through an ignored local override while other projects
migrate their own client configuration. Do not remove a network with active consumers.

Reuse passing test evidence when sources and execution inputs are unchanged. Run new
and directly affected checks; image builds do not automatically rerun the test suite.
Tests of model instructions verify their attachment, not that a model can never ignore them.

Public `main` and `develop` use a clean root history. Older clones should be replaced
to avoid reintroducing removed commits. Rewriting branch history cannot erase other
people's clones, forks or GitHub caches. If a credential is published, revoke or rotate
it; rewriting history does not invalidate it. Never commit private `.env` files,
credential stores, private keys or machine-specific overrides.
