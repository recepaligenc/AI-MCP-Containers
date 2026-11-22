# Codex extension refresh and shared-home recovery

Run `node scripts/install-user-services.mjs` to inspect the planned unit names.
Run it with `--install` to install and enable the user services for the current
checkout. The installer renders actual paths instead of shipping an owner's home
or installation directory. Only public binary-path settings are copied to units;
the service key is never written there.

- `ai-mcp-codex-refresh.path` observes the selected VS Code extensions directory.
- `ai-mcp-codex-refresh.timer` provides a 15-minute backstop.
- `ai-mcp-codex-refresh.service` updates the stable binary directory.
- `ai-mcp-codex-home.timer` checks the shared credential-directory identity each minute.

The updater reads `AI_MCP_CODEX_EXT_BIN` from `.env`; its extension directory defaults
to `~/.vscode/extensions` and can be selected with `AI_MCP_CODEX_EXTENSIONS`. It chooses
the highest numeric Linux x64 extension version. Content comparison detects changed
binaries/resources even if timestamps match. Rename-safe synchronization lets current
calls finish and new calls use the update without restarting the service.

The stable binary directory must be dedicated to this updater. Root, home, project
and extension-tree destinations are rejected before synchronization. A management
marker identifies existing copies; unknown nonempty directories are never adopted.

If the host `~/.codex` directory is renamed and recreated, the home guard recreates
only an already-running Codex service so its bind mount follows the current directory.
It never copies rotating OAuth credentials. Such recovery interrupts requests using
the obsolete directory; deliberately stopped services remain stopped.

The installer disables older updater/home-recovery units before enabling their new
project-prefixed names. After moving the checkout, rerun the installer.

```sh
systemctl --user list-timers ai-mcp-codex-refresh.timer ai-mcp-codex-home.timer
journalctl --user -u ai-mcp-codex-refresh.service -n 20
```
