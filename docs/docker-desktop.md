# Local services on Windows and macOS

Use Docker Desktop **Linux containers** and Node.js 22 or later on the host.
The Desktop deployment builds Linux CLIs for the Docker engine's amd64 or arm64
architecture. It does not execute Windows/macOS VS Code extension binaries.
Both services run locally; a separate Linux server is unnecessary.

Allow Docker access to your selected project directory when prompted. The host
Node process runs the setup wrapper and MCP adapter.

This deployment is an alternative to the Linux extension-based deployment. Both use
the same project name, service names and ports; run one deployment on a machine.
Do not combine the two Compose files as overrides. The existing Linux installation
and its ignored overrides remain separate from these commands.

## Windows

Install [Docker Desktop for Windows](https://docs.docker.com/desktop/setup/install/windows-install/),
select the WSL2 backend and Linux-container mode, and install Git and Node.js 22 or
later on the Windows host. In PowerShell, clone the repository and copy the template:

```powershell
git clone https://github.com/recepaligenc/AI-MCP-Containers.git
cd AI-MCP-Containers
Copy-Item .env.desktop.example .env.desktop
```

Continue with [shared configuration](#shared-configuration), then
[build, authenticate and start](#build-authenticate-and-start).

## macOS

Install [Docker Desktop for Mac](https://docs.docker.com/desktop/setup/install/mac-install/)
for your Intel or Apple Silicon Mac, plus Git and Node.js 22 or later on the host.
In macOS Terminal, clone the repository and copy the template:

```sh
git clone https://github.com/recepaligenc/AI-MCP-Containers.git
cd AI-MCP-Containers
cp .env.desktop.example .env.desktop
```

Continue with [shared configuration](#shared-configuration), then
[build, authenticate and start](#build-authenticate-and-start).

## Shared configuration

Generate a private service key, then put it in `.env.desktop` as `AI_MCP_TOKEN`:

```sh
node -e "console.log(require('crypto').randomBytes(32).toString('hex'))"
```

Set `AI_MCP_PROJECT_ROOT` to an existing project directory, using forward slashes:

```dotenv
# Windows example
AI_MCP_PROJECT_ROOT='C:/Users/Alice/Projects/My App'
# macOS example: use this instead of the Windows setting
# AI_MCP_PROJECT_ROOT='/Users/alice/Projects/My App'
```

Keep `.env.desktop` private; Git ignores it. Use a narrow project directory.
The preflight rejects the filesystem root, your home directory and its ancestors.
The project mount is read-only, but readable secrets inside that project remain
readable by the provider. Provider accounts belong to persistent, separate Docker
volumes; no host `.codex`, `.claude` or macOS Keychain is mounted or copied.

## Build, authenticate and start

These commands work in PowerShell and macOS Terminal:

```sh
node scripts/desktop.mjs check
node scripts/desktop.mjs build
node scripts/desktop.mjs login codex
node scripts/desktop.mjs login claude
node scripts/desktop.mjs up
```

The image pins Codex `0.171.0` and Claude Code `2.1.330`; both are installed from
their official npm packages. Rebuild to upgrade the versions in `Dockerfile`.
CLI executables live outside the persistent homes so an old volume cannot shadow
a newly built CLI. See [OpenAI's CLI installation guide](https://developers.openai.com/codex/cli)
and [Claude's npm installation guide](https://code.claude.com/docs/en/setup#install-with-npm).

Codex uses [device-code authentication](https://developers.openai.com/codex/auth#login-on-headless-devices).
Open the displayed URL on the host and enter the code. Enable device-code login
in your account/workspace settings if required. It stores credentials only in the
container's Codex volume using file storage.

Claude runs `claude auth login`. Open its displayed URL on the host and paste the
authorization code into the terminal if prompted, following the
[official authentication flow](https://code.claude.com/docs/en/authentication#log-in-to-claude-code).
Choose your Claude subscription login. Interactive authentication is performed by
you; the setup script does not read or print stored credentials.

Log in before starting the services. To renew a login, run `down`, repeat the
relevant login command, then run `up`. The wrapper refuses login to a running
provider to avoid concurrent credential changes. Check saved logins with:

```sh
node scripts/desktop.mjs status codex
node scripts/desktop.mjs status claude
```

`down` stops services and preserves account volumes. Deleting the Desktop home
volumes in Docker Desktop removes their saved logins. Credentials in those volumes
are separate from native host CLI logins; no rotating host credential is duplicated.

## Connect local MCP clients

The endpoints remain `http://127.0.0.1:8788` (Codex) and
`http://127.0.0.1:8789` (Claude). Use the existing
[client registration examples](../README.md#connect-claude-and-codex-as-mcp-clients),
but point `AI_MCP_ENV_FILE` to the absolute local path of **`.env.desktop`**.
Use your native Node executable and the local `mcp/bridge-mcp.mjs` path. Find Node
with `(Get-Command node).Source` in PowerShell or `command -v node` on macOS.
Native Codex/Claude clients are required only when connecting those clients; service
authentication uses the CLIs inside Docker.

### Codex CLI / VS Code

Add both blocks to the native client's `~/.codex/config.toml`
(`$env:USERPROFILE\.codex\config.toml` on Windows), preserving existing settings.
This complete Windows example uses literal TOML strings so backslashes are preserved;
replace the Node and checkout paths with your own:

```toml
[mcp_servers.ai_mcp_codex]
command = 'C:\Program Files\nodejs\node.exe'
args = ['C:\Projects\AI-MCP-Containers\mcp\bridge-mcp.mjs']
startup_timeout_sec = 20
tool_timeout_sec = 1300

[mcp_servers.ai_mcp_codex.env]
AI_MCP_PROVIDER = 'codex'
AI_MCP_URL = 'http://127.0.0.1:8788'
AI_MCP_ENV_FILE = 'C:\Projects\AI-MCP-Containers\.env.desktop'
AI_MCP_TIMEOUT_MS = '1250000'
AI_MCP_DEFAULT_CWD = '/workspace'

[mcp_servers.ai_mcp_claude]
command = 'C:\Program Files\nodejs\node.exe'
args = ['C:\Projects\AI-MCP-Containers\mcp\bridge-mcp.mjs']
startup_timeout_sec = 20
tool_timeout_sec = 1300

[mcp_servers.ai_mcp_claude.env]
AI_MCP_PROVIDER = 'claude'
AI_MCP_URL = 'http://127.0.0.1:8789'
AI_MCP_ENV_FILE = 'C:\Projects\AI-MCP-Containers\.env.desktop'
AI_MCP_TIMEOUT_MS = '1250000'
AI_MCP_DEFAULT_CWD = '/workspace'
```

On **macOS**, use these same two blocks with `command` set to the absolute result
of `command -v node`. Replace both `args` entries with your adapter's Unix path,
such as `['/Users/alice/Projects/AI-MCP-Containers/mcp/bridge-mcp.mjs']`, and both
`AI_MCP_ENV_FILE` entries with `/Users/alice/Projects/AI-MCP-Containers/.env.desktop`.
Provider names, URLs, timeouts and `/workspace` remain identical.

### Claude Code CLI / VS Code

Use a native Claude Code CLI supporting `mcp add-json` input from stdin (the
[shared instructions](../README.md#claude-code-cli-and-vs-code) specify the minimum
version). Save this as a local `codex-service.json`, replacing the paths:

```json
{
  "type": "stdio",
  "command": "C:/Program Files/nodejs/node.exe",
  "args": ["C:/Projects/AI-MCP-Containers/mcp/bridge-mcp.mjs"],
  "timeout": 1300000,
  "env": {
    "AI_MCP_PROVIDER": "codex",
    "AI_MCP_URL": "http://127.0.0.1:8788",
    "AI_MCP_ENV_FILE": "C:/Projects/AI-MCP-Containers/.env.desktop",
    "AI_MCP_TIMEOUT_MS": "1250000",
    "AI_MCP_DEFAULT_CWD": "/workspace"
  }
}
```

Create `claude-service.json` from the same structure with `AI_MCP_PROVIDER` set to
`claude` and `AI_MCP_URL` set to `http://127.0.0.1:8789`. For macOS, replace all
client paths with their absolute Unix paths. The files contain paths, not secret keys.

Run registration **outside** an active Claude Code session:

```powershell
# Windows PowerShell: pipe JSON to stdin; do not use Unix '< file' redirection.
Get-Content -Raw -LiteralPath ./codex-service.json | claude mcp add-json ai_mcp_codex - --scope user
Get-Content -Raw -LiteralPath ./claude-service.json | claude mcp add-json ai_mcp_claude - --scope user
```

```sh
# macOS Terminal
claude mcp add-json ai_mcp_codex - --scope user < ./codex-service.json
claude mcp add-json ai_mcp_claude - --scope user < ./claude-service.json
```

For either native client, start a new session and inspect `/mcp`. Tool discovery
confirms registration; it does not prove an authenticated model call. Continue with
[steps 4–5 of the installation guide](getting-started.md#4-connect-each-native-client-once)
for the shared connection verification and rule installer.

The service sees the selected project at **`/workspace`**. Omit request `cwd` to
use that directory, or send `/workspace/subproject`. Host paths such as
`C:/Users/Alice/Projects/My App` or `/Users/alice/Projects/My App` are bind sources,
not valid server working directories. Instruct native clients to use the server
path for `ask_codex`/`ask_claude`; paths are not translated automatically.

Timeout is 1200 seconds and the default prompt limit is 1,000,000 UTF-16 units,
configurable with `AI_MCP_MAX_PROMPT_CHARS`. Keep the documented 1250-second adapter
timeout and restart clients after changing their settings. Desktop listens on
localhost only; Linux automatic LAN discovery is not used.

## Verification coverage

Windows-drive and macOS-path unit fixtures and resolved Compose hardening checks
cover configuration. The Linux amd64 image has also been checked for CLI startup,
non-root home ownership/persistence, read-only mounts and authenticated local
readiness for both provider configurations, without making model calls.
The CI matrix is configured to run setup unit checks on Linux, Windows and macOS; that is not
an end-to-end Docker Desktop or browser-login test.

Actual Windows/macOS Docker Desktop execution, ARM64 execution and browser account
login remain unverified here. Linux-only evidence does not establish that a host's
file-sharing permissions or account login works.

After installation on each target machine:

1. Run `node scripts/desktop.mjs status codex` and `status claude` and confirm both
   accounts are signed in.
2. Open `http://127.0.0.1:8788/health` and `http://127.0.0.1:8789/health` in the host
   browser; each should return `{"status":"ok"}`.
3. Start a new native client session and confirm both MCP tools are connected.
4. Choose a non-secret file in the selected project. Ask each provider, with
   `cwd=/workspace`, to report a short fact from that file without editing anything.
   This uses provider quota and checks account access, client wiring and the project
   mount together. A quota, login or file-sharing failure leaves that check pending.

Only a successful run on the target machine establishes its complete installation.
