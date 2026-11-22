# AI-MCP-Containers

![Human-figure agents discuss the goal, critically revise a plan, compare UI options with user approval, propose changes, reuse checks, and shake hands over a joint delivery. Intended benefits: less unnecessary change, earlier risk discovery, consistent UI and less repeated testing.](docs/assets/pair-workflow.gif)

[View the static result and six-step overview](docs/assets/pair-workflow.png).

**Example collaborative workflow:** The personified agents discuss and refine a shared
result. The host client coordinates an owner-controlled
policy. Providers critique read-only project context; the client applies in-scope
changes and runs justified checks. Claude and Codex roles can swap with the leading
client. UI choices and visual checks apply to UI work. Instructions do not provide
immutable enforcement or a built-in orchestration engine.
The handshake means a joint recommendation after user acceptance and evidence, rather
than an automatic release. Scenes pause for two extra seconds and the final delivery
for eight, so the results remain visible. The linked static preview shows all six
steps and the shared delivery as an alternative to the animation.

Two global Docker services use the host owner's local Codex/ChatGPT and Claude Code
subscriptions. Each service exposes `/chat` and `/v1/responses`; a local Node adapter
makes the service available to MCP clients over stdio.
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
| `AI_MCP_LAN_IP` | Empty, as in the example, keeps access to this host and the Docker network only. `auto` discovers the host's physical private IPv4 addresses at startup; one assigned private host IPv4 adds a listener on that address only. |

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

The example leaves `AI_MCP_LAN_IP` empty: the services are reachable from this host
and from containers on `ai-mcp-shared` only. Set `AI_MCP_LAN_IP=auto` to add LAN
access. On a Linux host using a local Docker Unix socket, startup then discovers
RFC1918 IPv4 addresses on active physical network interfaces,
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

## Connect Claude and Codex as MCP clients

Both Codex and Claude Code can use **both** services: `ask_codex` calls the Codex
container and `ask_claude` calls the Claude container. The client and provider do not
need to be the same product. All calls use the Docker host owner's subscriptions.

The MCP client starts `node mcp/bridge-mcp.mjs` locally over **stdio**. The adapter then
calls the selected service's HTTP API. Do not register ports `8788` or `8789` as direct
MCP HTTP URLs; these services do not expose the MCP Streamable HTTP transport.

### Shared prerequisites

Start the containers first; registering an MCP server does not start Docker. On each
client machine, keep a checkout of this repository and install Node.js. Replace every
absolute-path placeholder below with a path on the **client's** machine:

| Placeholder | What to select |
| --- | --- |
| `/absolute/path/to/node` | Node executable, from `command -v node`. An absolute path also works when an IDE has a different `PATH`. |
| `/absolute/path/to/AI-MCP-Containers/mcp/bridge-mcp.mjs` | This checkout's adapter script, accessible to the MCP client. |
| `/absolute/path/to/private/ai-mcp.env` | Private file containing `AI_MCP_TOKEN=<service-key>`. On the Docker host, the repository's ignored `.env` can be used directly. |

Create or edit the private token file outside version control and restrict it with
`chmod 600 /absolute/path/to/private/ai-mcp.env`. Use the existing **service Bearer key**,
not a provider API key or OAuth token. A remote client needs only this key; do not copy
the host's Codex/Claude credential directories. The adapter reads the file itself;
do not `source` it. An exported `AI_MCP_TOKEN` takes precedence over the file.

The examples below run on the Docker host. For another LAN machine or a client
container, change `AI_MCP_URL` using the [network access table](#network-access).
`127.0.0.1` always means the machine/container where the adapter runs. A Docker client
must join `ai-mcp-shared` and use service port `8080`.

### Codex CLI and VS Code

Codex CLI and the Codex VS Code extension share `~/.codex/config.toml`. Add these
sections while preserving existing settings. The names `ai_mcp_codex` and
`ai_mcp_claude` are reserved for these **stdio** registrations: provider-side Codex
runs disable them to prevent recursive service calls. Do not use these IDs for HTTP
MCP registrations; conflicting transports fail configuration loading. Other custom
MCP entries are not disabled by this protection. See the [official OpenAI MCP documentation](https://developers.openai.com/codex/mcp).

```toml
[mcp_servers.ai_mcp_codex]
command = "/absolute/path/to/node"
args = ["/absolute/path/to/AI-MCP-Containers/mcp/bridge-mcp.mjs"]
startup_timeout_sec = 20
tool_timeout_sec = 1300

[mcp_servers.ai_mcp_codex.env]
AI_MCP_PROVIDER = "codex"
AI_MCP_URL = "http://127.0.0.1:8788"
AI_MCP_ENV_FILE = "/absolute/path/to/private/ai-mcp.env"
AI_MCP_TIMEOUT_MS = "1250000"

[mcp_servers.ai_mcp_claude]
command = "/absolute/path/to/node"
args = ["/absolute/path/to/AI-MCP-Containers/mcp/bridge-mcp.mjs"]
startup_timeout_sec = 20
tool_timeout_sec = 1300

[mcp_servers.ai_mcp_claude.env]
AI_MCP_PROVIDER = "claude"
AI_MCP_URL = "http://127.0.0.1:8789"
AI_MCP_ENV_FILE = "/absolute/path/to/private/ai-mcp.env"
AI_MCP_TIMEOUT_MS = "1250000"
```

Start a new Codex session after saving. Inspect the registrations with:

```sh
codex mcp list
codex mcp get ai_mcp_codex
codex mcp get ai_mcp_claude
```

In the interactive CLI, use `/mcp` to inspect active servers and tools. In VS Code,
reload the Codex session after editing the shared configuration.

### Claude Code CLI and VS Code

Install the Claude Code CLI if `claude` is not on your `PATH`; the VS Code extension
alone does not install a terminal command. User-scope registrations are shared by the
CLI and VS Code and apply across projects. Use a current CLI supporting JSON input
from stdin (version `2.1.327` or newer). See [Claude's JSON registration guide](https://code.claude.com/docs/en/mcp#add-mcp-servers-from-json-configuration)
and [VS Code MCP setup](https://code.claude.com/docs/en/vs-code#connect-to-external-tools-with-mcp).

Create a private directory with `mkdir -p ~/.config/ai-mcp-clients`. Save the following
as `~/.config/ai-mcp-clients/codex-service.json`, replacing the absolute paths:

```json
{
  "type": "stdio",
  "command": "/absolute/path/to/node",
  "args": ["/absolute/path/to/AI-MCP-Containers/mcp/bridge-mcp.mjs"],
  "timeout": 1300000,
  "env": {
    "AI_MCP_PROVIDER": "codex",
    "AI_MCP_URL": "http://127.0.0.1:8788",
    "AI_MCP_ENV_FILE": "/absolute/path/to/private/ai-mcp.env",
    "AI_MCP_TIMEOUT_MS": "1250000"
  }
}
```

Save the second registration as `~/.config/ai-mcp-clients/claude-service.json`:

```json
{
  "type": "stdio",
  "command": "/absolute/path/to/node",
  "args": ["/absolute/path/to/AI-MCP-Containers/mcp/bridge-mcp.mjs"],
  "timeout": 1300000,
  "env": {
    "AI_MCP_PROVIDER": "claude",
    "AI_MCP_URL": "http://127.0.0.1:8789",
    "AI_MCP_ENV_FILE": "/absolute/path/to/private/ai-mcp.env",
    "AI_MCP_TIMEOUT_MS": "1250000"
  }
}
```

Run these commands from a terminal **outside** a running Claude Code session:

```sh
claude mcp add-json ai_mcp_codex - --scope user < ~/.config/ai-mcp-clients/codex-service.json
claude mcp add-json ai_mcp_claude - --scope user < ~/.config/ai-mcp-clients/claude-service.json
claude mcp list
claude mcp get ai_mcp_codex
claude mcp get ai_mcp_claude
```

These commands update the user configuration in `~/.claude.json`; no key is placed in
the command arguments. Start a new CLI/VS Code conversation and open `/mcp` to inspect
the connections. Avoid project-scope `.mcp.json` files containing private tokens.

### Verify a request and coordinate the agents

Registration and tool discovery verify the local stdio connection, not the Bearer key
or provider quota. Please use this small README-review prompt when you would like to
check a real provider request before starting a larger task. It helps distinguish
connection, authentication and quota issues while returning a focused review. This
request consumes provider quota; reuse an existing successful check when its relevant
setup is unchanged. Ask the client to call `ask_codex` or `ask_claude` with input such as:

```json
{
  "prompt": "Critique the project README. Return only supported improvements, up to three; say if none are needed. Do not read secrets.",
  "cwd": "/absolute/path/to/mounted/project"
}
```

`cwd` must be an existing path under the Docker host's selected `AI_MCP_PROJECT_ROOT`,
using the path visible inside the service container. It is not a path on a remote
client machine. Omit `sandbox` to use the service default; see [adapter options](mcp/README.md)
for explicit read-only modes and their limitations.

Please use the following prompt when you would like both agents to contribute to
planning, implementation and verification. The complementary review helps surface
missing cases, and sharing existing test evidence keeps the work grounded without
repeating completed checks unnecessarily. Tell your client: **“Ask Claude for a plan,
then send it to Codex with: 'Strictly critique this plan.' Include the authorized goal,
constraints and acceptance criteria. Assess supported findings and revise within scope.
Propose changes, reuse valid passing test evidence, run only justified affected tests,
then ask the other agent: 'Critique this implementation and test evidence.'”** Each
provider call is independent, so the client must pass the relevant prior output in the
next prompt. The containers return
analysis and proposals; the client's own tools apply edits and execute tests.

The examples allow a `1250000` ms adapter HTTP timeout and a larger outer tool timeout:
`1300` seconds in Codex, `1300000` milliseconds in Claude. This exceeds Claude's
1200-second provider deadline. Adjust these together; see [Claude's tool timeout reference](https://code.claude.com/docs/en/mcp#time-limits-on-mcp-tool-calls).

| Symptom | What to check |
| --- | --- |
| Node/script cannot start | Absolute executable and script paths on the client machine; IDE process permissions. |
| `401` | Correct service key and readable private ENV file; remove a stale exported `AI_MCP_TOKEN`. Restart the adapter after changing the file. |
| Connection refused | Containers are running and `AI_MCP_URL` matches the adapter's location. |
| `cwd_forbidden` / `403` | Use a project path inside the service's mounted root. |
| Outer tool timeout | Keep the client deadline longer than the adapter and provider deadlines. |
| `429` | Inspect the returned error: provider quota/rate limit or service concurrency limit. Re-registering MCP does not reset quotas. |

### Set up from a console prompt and keep the workflow

You can ask either coding client to configure both MCP connections and save the pair
workflow as a project instruction. Use the [ready-to-paste setup prompt](docs/console-setup.md)
and [shared collaboration rule template](docs/templates/ai-mcp-collaboration.md).
The recipe preserves existing settings, uses the private token file, and connects both
clients to one canonical project rule through `AGENTS.md` and `CLAUDE.md`.

For covered development tasks, the rule requires a complementary-provider plan review
and a final change/test-evidence review. Claude-led work iterates with Codex, and
Codex-led work with Claude, up to 20 review requests across the entire task. It prefers
the smallest sufficient durable change and evaluates architectural fit, security and
performance. It finishes early when ready and reports unresolved work rather than
claiming completion after quota failure, stalled review or exhausted budget.

Documentation, code and configuration audits also require complementary-provider
review of the final findings, even without edits or when no findings are identified.
The host sends the scope, inspected artifacts, evidence and limitations with
"Critique these audit findings and evidence." Audits do not qualify for the factual
lookup or spelling/formatting exemptions. This is an installed client instruction;
the services do not enforce the review workflow at runtime.

For UI design or redesign, it requires three visual alternatives and explicit user
approval before implementation. Visible UI changes require AI inspection of rendered
views and evidence that affected interactions work, with simple UX and existing fonts
unless a change is requested. The adapter is text-only: an image-capable host performs
visual inspection while the peer reviews code and interaction evidence.

The rule is active in this repository through [AGENTS.md](AGENTS.md),
[CLAUDE.md](CLAUDE.md) and [one canonical policy](.ai-mcp/collaboration.md).
Agents are prohibited from independently changing, weakening or bypassing that policy
or its references. Amendments require an explicit user instruction. This is an agent
policy; technical immutability requires controls outside the agent's write authority.

MCP registration is user-scoped; the rule is project-scoped by default. The guide also
explains an explicit all-projects user rule. Start a new client session after setup.
The guide verifies rule loading, tool discovery and actual peer reviews separately.
These are persistent model instructions, not guaranteed orchestration or a permission
bypass; the client still owns edits/tests and reports unavailable reviews honestly.

### Keep iterations focused

Please use these gates when applying the collaboration rule. They help keep rigorous
critique tied to the requested outcome instead of turning suggestions into new work.
The [canonical policy](.ai-mcp/collaboration.md) makes them mandatory for covered tasks.

| Gate | Required behavior | Benefit |
| --- | --- | --- |
| Task contract | Record the outcome, acceptance criteria, non-goals and anticipated change surfaces. Only explicit user steering changes the outcome. | Both agents evaluate the same requested result. |
| Evidence before corrections | Trace findings to requirements, current artifacts, observed failures or supported risks. Mark assumptions and hypotheses. | Plausible claims do not silently become features or defects. |
| Triage | Apply justified in-scope fixes; keep optional ideas and unresolved hypotheses in the task record/report. | Useful ideas are preserved without expanding the implementation. |
| Scope approval | Explain impact and a bounded alternative before material expansion; keep routine authorized choices autonomous. | The user retains control of project direction without repeated approvals for normal work. |
| Critical, finite review | Ask for strict plan critique and implementation/evidence critique. A supported READY is valid. Stop early, respect the 20-request cap and reserve the final review. | Review can improve the work without inventing defects or running indefinitely. |
| Reuse evidence | Reuse valid unchanged test/review results; run only justified affected checks. | Time and quota are spent on changed behavior. |

These gates guide the clients; the services do not enforce a task contract, review
counter or scope-approval workflow. Agreement between models is not proof. Missing
evidence or review is reported rather than replaced with an unsupported success claim.

### UI criteria and user benefits

For new UI design or redesign, please compare three distinct rendered alternatives
and explicitly approve a choice before production implementation. For visible changes,
the following criteria guide the AI review and affected functional checks:

| Criterion | What is checked | Benefit for the user |
| --- | --- | --- |
| Simple flows | Clear primary actions, familiar navigation and only necessary controls. | Tasks take fewer confusing steps. |
| Familiar typography | Existing project fonts unless a change is requested, readable text and consistent spacing. | Screens remain easy to scan and feel consistent. |
| Accessible interaction | Meaningful labels, readable contrast, keyboard access and visible focus; information is not conveyed by color alone. | More users can understand and operate the interface. |
| Responsive layouts | Relevant mobile/desktop sizes, wrapping, overflow and usable controls. | Content and actions remain reachable on different screens. |
| Feedback and recovery | Loading, empty, error and validation states, action feedback and a clear next step. | Users can tell what happened and how to continue. |
| Working controls | Affected links, buttons, forms, navigation and error handling, supported by actual checks. | The interface performs the actions it promises. |
| Privacy and performance | Sensitive content in views/review context and relevant interaction or resource costs. | Reviews avoid unnecessary exposure and keep interactions practical. |

An image-capable host inspects actual rendered views. The text-only peer reviews UI
code, interaction evidence and reported findings; a path or description is not image
access. Screenshots establish appearance, while functional evidence establishes the
tested behavior. Reuse unchanged valid evidence and report missing verification.

## Runtime output and readiness

Provider stdout and stderr share an 8 MiB byte budget per process. Codex's final
answer file has a separate budget of the same size. Oversized output fails with
`output_limit_exceeded`; it is not silently truncated. Set `AI_MCP_MAX_OUTPUT_BYTES`
to an integer between 65536 and 67108864 to change the budgets. Invalid values stop
startup. This bounds captured output, not all provider memory or temporary disk use;
the Compose memory and tmpfs limits still apply.

`GET /health` remains public HTTP liveness and is the Compose healthcheck target.
Authenticated `GET /ready` returns 200 or 503 based on local executables, the working
directory, the configured sandbox, and recent authentication/quota observations.
It makes no model calls and reads no credentials. `unknown` means remote validity
has not been established recently; a ready response does not prove remote provider
reachability or token validity. Typed authentication failures are currently observed
for Claude; Codex login failures remain generic provider errors and are not classified
as authentication failures. Authentication observations expire after one minute;
quota observations expire at their supplied reset time, capped at 15 minutes, or
one minute when no future reset is known. Successful calls restore readiness;
readiness never prevents a recovery call.

MCP tool errors retain their human-readable `error` and add `errorDetails` with
`code`, `httpStatus`, and valid optional `retryAtUtc`, `limitKind`, and
`retryAfterSeconds` fields. Header and body retry values remain distinct when both
are supplied. Responses API replies now carry a unique `resp_` identifier per reply.

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

The per-process `AI_MCP_TIMEOUT_SECONDS` deadline covers prompt writes and flushes,
process exit and stdout/stderr reads. Timeout or caller cancellation triggers
process-tree termination while the provider is still running; output-reader cleanup
has a separate one-second bound. This is a process deadline, not a total budget
across retries. A child orphaned after its parent exits can outlive the call.

Failed-provider warnings record the exit code, not raw stdout/stderr. Output remains
in memory for quota, authentication and capacity classification. This does not
sanitize every application/provider log or establish a confidentiality boundary.

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

## Responses text API

`POST /v1/responses` implements a text-only subset, not the full OpenAI API.
It accepts `input` as a nonempty string or a message array with text content.
Message content may be a string or an array of text parts; multimodal inputs and
tool execution are outside this contract. Top-level `instructions` are placed first,
then system/developer messages in their original relative order, then other messages.
Instructions alone do not replace the required input. The requested `model` remains
informational; the response reports the provider's actual model when available.

The accepted fields are `input`, `instructions`, `model`, `stream`, `tools` and
`previous_response_id`. Unknown fields and duplicate keys receive `400` before a
provider runs. `stream: true`, nonempty `tools` and nonempty `previous_response_id`
receive `unsupported_parameter`: streaming, client tools and response chaining are
not implemented. Omitted/null optional fields, `stream: false`, `tools: []` and an
empty previous response ID are accepted. Empty/whitespace instructions are ignored;
an empty model is accepted as informational. Wrong field types receive `bad_request`,
non-object JSON roots receive `bad_json_type`, and missing/empty input receives
`missing_input`. Errors use the bridge's `{ "error": "...", "code": "..." }` envelope.
Combined instructions and input remain subject to the 100,000-character limit.

## Migration and verification

Application names are `AiMcpContainers` and `AiMcp`; active environment names use
`AI_MCP_*`. Provider-native `CODEX_HOME`, `HOME` and `CLAUDE_CODE_TMPDIR` retain their
native meanings. The MCP adapter temporarily accepts deprecated client launcher aliases.

The shared network is `ai-mcp-shared`.

Reuse passing test evidence when sources and execution inputs are unchanged. Run new
and directly affected checks; image builds do not automatically rerun the test suite.
Completed passing checks must not be repeated merely because the task moves to another
stage or unrelated code/documentation changes. Report their existing evidence instead;
rerun a failed check only when a relevant fix or concrete finding justifies it. This is
a [mandatory project rule](.ai-mcp/collaboration.md#mandatory-test-evidence-reuse--owner-2026-11-19).
Tests of model instructions verify their attachment, not that a model can never ignore them.

The changed-file selector in `scripts/affected-dotnet.mjs` maps production and test
changes to affected classes, including security, validation, usage and sandbox
checks. Unmapped C# sources fail CI until a mapping is added. Shared build inputs
and selector changes select all mapped consumers; unrelated changes select none.
Push diffs include both names of renames. A new or unavailable base falls back to
mapping tracked service inputs, never to an empty successful selection.

The focused [bridge CI](.github/workflows/dotnet-checks.yml) and
[adapter CI](.github/workflows/mcp-checks.yml) run on pushes changing their relevant
source, fixtures, dependencies or execution configuration. Unrelated documentation
does not trigger these checks, and a separate pull-request trigger is deliberately
omitted to avoid duplicate runs. These are focused checks, not full-suite coverage;
they are not configured as required branch-protection checks.
Reuse an existing successful run for unchanged inputs instead of manually rerunning it.
Each run uploads its source hashes, commit, actual tool versions, test selection and
TRX/JUnit results, including available evidence on failure. Local reports belong in
the ignored `test-results/` directory. Preparing these workflows locally does not
prove they have run on GitHub.

Public `main` and `develop` use a clean root history. Older clones should be replaced
to avoid reintroducing removed commits. Rewriting branch history cannot erase other
people's clones, forks or GitHub caches. If a credential is published, revoke or rotate
it; rewriting history does not invalidate it. Never commit private `.env` files,
credential stores, private keys or machine-specific overrides.


### Install and reuse the model

Follow the [step-by-step installation guide](docs/getting-started.md) to start the
services, register MCP once per native client and install the permanent rules.
There is no manual Markdown copying:

```bash
node scripts/install-rules.mjs --project /absolute/path/to/your/project
# Or use one policy across your native clients' projects:
node scripts/install-rules.mjs --user
```

Existing setup always asks **Keep / Update / Override**, defaulting to Keep. Update
adds missing files and references while preserving existing policy text; conflicting
managed references stop before writes. It does not merge new rule clauses into a
customized policy. Changed existing instruction files are backed up privately. Override backs
up changed existing files and preserves unrelated instructions. In a non-interactive
AI console, the AI must receive your choice before passing `--existing override`.
User-scope rules do not grant container access to additional projects.

Fresh installations include the mandatory audit-review rule and an audit-aware Codex
reference. To adopt them in an existing installation, choose **Override** after
reviewing its effect on your managed policy. **Keep** and **Update** preserve existing
canonical policy bytes; Update also preserves existing managed reference text.

### Understand the added review usage

Each provider spends its own allowance. An illustrative workload of **10 main calls
and 2 reviews adds 20% more requests**; it implies 20% more cost only under the
assumption of equal average cost per request. Actual credits and cash charges depend
on the providers, models and plans. This is not a measured overhead forecast.

MCP now returns a per-call usage receipt for the initiating client to retain and
summarize by provider/model, with coverage and unknown values shown. Native token
counts and API-equivalent estimates are not subscription credits. The initiating
client's work outside the bridge is unmeasured unless it supplies its own evidence.
See [usage and cost reporting](docs/usage-and-cost.md) for fields and limitations.

The practical reasons to evaluate this model are critical planning before a large
patch, earlier supported security/performance findings, smaller changes, explicit
UI approval and avoiding unnecessary repeat tests. These are intended benefits;
there are no guaranteed savings or measured defect-reduction percentages.
