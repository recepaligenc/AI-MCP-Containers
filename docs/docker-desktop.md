# Local services on Windows and macOS

Use Docker Desktop **Linux containers** and Node.js 22 or later on the host.
The Desktop deployment builds Linux CLIs for the Docker engine's amd64 or arm64
architecture. It does not execute Windows/macOS VS Code extension binaries.
Both services run locally; a separate Linux server is unnecessary.

Install [Docker Desktop for Windows](https://docs.docker.com/desktop/setup/install/windows-install/)
or [Docker Desktop for Mac](https://docs.docker.com/desktop/setup/install/mac-install/).
On Windows select the WSL2 backend and Linux-container mode. On macOS use the
installer for your Intel or Apple Silicon Mac. Allow Docker access to your selected
project directory when prompted. The host Node process runs the setup wrapper and MCP adapter.

This deployment is an alternative to the Linux extension-based deployment. Both use
the same project name, service names and ports; run one deployment on a machine.
Do not combine the two Compose files as overrides. The existing Linux installation
and its ignored overrides remain separate from these commands.

## Configure

Run commands from this repository. Copy the public template:

```powershell
# Windows PowerShell
Copy-Item .env.desktop.example .env.desktop
```

```sh
# macOS Terminal
cp .env.desktop.example .env.desktop
```

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
Use your native Node executable and the local `mcp/bridge-mcp.mjs` path.
Use literal TOML strings for Windows paths, for example:

```toml
command = 'C:\Program Files\nodejs\node.exe'
args = ['C:\Projects\AI-MCP-Containers\mcp\bridge-mcp.mjs']
# Inside the matching mcp_servers.<name>.env table:
# AI_MCP_ENV_FILE = 'C:\Projects\AI-MCP-Containers\.env.desktop'
```

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

The code includes Windows-drive and macOS-path fixtures, Compose hardening checks
and Linux-container CLI smoke checks. Actual Windows/macOS Docker Desktop runtime
and browser account login must be verified on those hosts. Linux-only evidence
does not establish that a host's file-sharing permissions or account login works.
