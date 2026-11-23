# Claude runtime

This document describes the **Linux host extension-based deployment**.
For Windows/macOS Docker Desktop, use the [Desktop guide](docs/docker-desktop.md):
its image installs the Linux CLI and its account home is a named Docker volume.
The host extension mounting and credential-seed steps below do not apply there.

The service uses the latest compatible stable Linux Claude Code executable installed
by the host's VS Code extension. It does not install an independent npm Claude CLI.
The host extensions directory, selected by `AI_MCP_CLAUDE_EXTENSIONS`, is mounted
read-only at `/vscode-extensions`.

The launcher resolves the highest installed stable numeric version on every call.
It checks installation identity, registry state, architecture, canonical paths and
executable permissions. An incomplete newest installation fails rather than silently
falling back to an older version. VS Code can be closed, and an extension update does
not require rebuilding or restarting the service. Existing calls finish with their
original executable; subsequent calls pick up the update.

`AI_MCP_CLAUDE_HOME` points to private runtime storage. The host credentials are seeded
from the read-only `/seed` directory. On a recognized authentication failure, updated
host credentials can be copied atomically and retried once. Credential contents must
never be logged. The CLI still needs readable credentials; this is not a secret-isolated
execution environment for untrusted prompts.

Project access is read-only in both Compose and the request policy. Claude safe mode
suppresses project hooks and MCP servers. Read-only calls expose only `Read,Grep`;
`read-only-web` additionally exposes `WebFetch,WebSearch`. Provider login checks and
`claude --version` do not prove entitlement to every requested model.

Build the complete current application with `docker compose build`. Preserve previous
local image IDs and private configuration for rollback, then update one provider at a
time. Validate new artifact identity, authentication and mount policy before proceeding
to the second provider. Do not publish local images or logs containing credentials.
