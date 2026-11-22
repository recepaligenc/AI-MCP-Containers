# Install once, collaborate across projects

This guide sets up the two local provider services, connects your native Codex and
Claude clients, and installs the collaboration rules without moving Markdown files
by hand. The services use your own authenticated CLI installations. They do not
combine your subscriptions or transfer credits between providers.

## 1. Prepare your computer

Use a Linux host with Docker Engine, Docker Compose, Node.js and `rsync`. Sign in to
both native CLIs on the host and confirm that your installed Linux Codex and Claude
extension binaries work. Follow the providers' own sign-in instructions; do not put
account passwords or subscription credentials in this repository.

Clone this repository and enter it:

```bash
git clone https://github.com/recepaligenc/AI-MCP-Containers.git
cd AI-MCP-Containers
cp .env.example .env
```

## 2. Select access and configure your private environment

Edit `.env` using the English explanations in `.env.example`. Set:

- `AI_MCP_TOKEN`: generate a private bearer token with `openssl rand -hex 32`.
- `AI_MCP_PROJECT_ROOT`: an existing absolute directory containing only the projects
  you intend to expose to both services.
- The documented host CLI, extension and authentication paths for your machine.
- `AI_MCP_HOST_UID` and `AI_MCP_HOST_GID`: use the values from `id -u` and `id -g`.

The selected project tree is mounted read-only. This prevents direct writes; it
does **not** hide `.env`, credentials or other readable descendants. A broad root
such as `/mnt/projects` exposes more projects than a single selected repository.
The fixed safety prompt is behavioral guidance, not a filesystem access barrier.
Keep secrets outside the selected tree when they must not be readable.

Leave private `.env` and generated Compose overrides out of Git. Keep the default
localhost binding unless you need the documented host-IP mode. Host-IP access still
requires the bearer token; review the README network limitations before enabling it.

## 3. Start the services

Create the shared network once if it does not already exist:

```bash
docker network inspect ai-mcp-shared >/dev/null 2>&1 || docker network create ai-mcp-shared
./refresh-codex-bin.sh
./run-docker.sh --build
```

The launcher validates the resolved configuration before starting the services.
Use the README configuration and troubleshooting sections if a required host path
or compatible binary is missing. Do not solve a path error by mounting your entire
home directory or granting write access.

## 4. Connect each native client once

Open this repository in the native client you use and follow the
[console setup prompt](console-setup.md). Repeat that setup
from the other client if you use both. It registers the complementary provider MCP
connection in that client's user configuration. Registration is separate from
installing collaboration rules, and does not need repeating for every project.

Keep the reserved `ai_mcp_codex` and `ai_mcp_claude` registrations on the documented
stdio adapter. The guide includes the exact Codex and Claude connection commands,
verification prompt and their purpose. Never paste the bearer token into a prompt.

## 5. Install permanent collaboration rules automatically

Run either command **from this repository**, using the target project's absolute
path for a project installation:

```bash
node scripts/install-rules.mjs --project /absolute/path/to/your/project
```

Or apply the policy to your native clients across projects:

```bash
node scripts/install-rules.mjs --user
```

Project scope creates `.ai-mcp/collaboration.md` and managed references in the
existing client instruction files. It preserves unrelated instructions, including
an active `AGENTS.override.md` and an existing `.claude/CLAUDE.md` layout. No manual
Markdown copying is required.

Fresh project and user installations include mandatory complementary-provider review
of documentation, code and configuration audit findings, even when no edits are made
or no findings are identified. The host must obtain the actual review before reporting
the audit as complete. This is client guidance, not a server-side enforcement loop.

User scope keeps one canonical policy at
`<Claude config root>/.ai-mcp/collaboration.md`. Claude imports it relatively; Codex
receives an absolute file reference. Defaults are `~/.claude` and `~/.codex`, with
`CLAUDE_CONFIG_DIR` and `CODEX_HOME` respected. Both native clients must be able to
read the shared policy. The installer's readability check cannot prove access from
a later sandbox or another container. User rules do not add mounts or filesystem
permissions. Use project scope when the global reference is inaccessible.

Existing AI-MCP setup always produces a warning and a **Keep / Update / Override** choice,
even when its content is identical. Press Enter for **Keep**, which changes no
files or timestamps. **Update** adds missing files and references while preserving
existing canonical policy bytes and unrelated instruction text. It does not merge
new rule clauses into customized policy. Conflicting or unfamiliar managed
references stop the preflight before any writes. A preserved policy is not claimed
to match the newest template. Changed existing instruction files are backed up
privately before appending missing references. **Override** updates the managed policy/references, preserves
unrelated instruction text and privately backs up changed existing files under
`.ai-mcp/backups/`. Keep those backups private and out of version control. A backup
index identifies each original file. The installer refuses ambiguous markers and
symlinked managed destinations rather than guessing what to replace.

To adopt the audit-review rule in an existing installation, choose **Override** after
reviewing its effect on your managed policy. **Keep** and **Update** preserve existing
canonical policy bytes and do not add the new rule to that policy.
Update also preserves existing managed reference text; Override refreshes it from
the current installer, including the audit-aware Codex reference.

When a console cannot display an interactive prompt, the first attempt reports the
existing setup without writing. After **you** choose, rerun with one of:

```bash
node scripts/install-rules.mjs --project /absolute/path/to/your/project --existing keep
node scripts/install-rules.mjs --project /absolute/path/to/your/project --existing update
node scripts/install-rules.mjs --project /absolute/path/to/your/project --existing override
```

The same option works with `--user`. An AI must ask the owner and receive the answer
before selecting `--existing override`; an installation request alone does not
authorize replacing an existing setup. Multi-file writes are not one atomic
transaction: if installation fails partway, read the reported changed-file count
and backup location before retrying.

Start a new client session after installation. Use the existing
[verification prompt](console-setup.md) to confirm the loaded policy and actual MCP
connection. A writable instruction file is not technically immutable; these are
owner-controlled agent instructions, not an OS enforcement mechanism. More specific
instructions and client surfaces can affect loading. In particular, Claude Cowork
may skip imports outside its workspace.

## 6. Work in another project

If you chose user scope and the new project is inside the allowed project root,
open it in the same native client and use the
[collaboration task prompt](console-setup.md). No new rule-file copying or MCP
registration is needed. Pass that project's absolute `cwd` to peer reviews.

For project scope, run the installer once for the new project. If the project is
outside the selected Docker root, deliberately change `AI_MCP_PROJECT_ROOT` in
`.env` and rerun `./run-docker.sh`. A new instruction file cannot grant access to a
directory the containers do not mount. Review the exposure before broadening the
root to include several repositories.

## 7. Update without losing local decisions

Pull repository updates. Rerun the rule installer and choose Keep, Update or Override for
the installed policy. Update repairs missing installation structure; replacing or
merging newer policy text requires an explicit owner decision. Existing projects
are not silently rewritten. Refresh Codex
with `./refresh-codex-bin.sh --recreate` after a relevant extension update; Claude's
launcher selects its newest compatible installed extension at invocation time.
Rebuild changed services with `./run-docker.sh --build`. The optional documented
user services can automate binary refresh; they are separate from rule installation.

See [usage and cost reporting](usage-and-cost.md) for what the initiating client can
measure after reviews, and which credit figures remain unavailable.

Instruction path behavior follows the official
[Codex instruction discovery](https://learn.chatgpt.com/docs/agent-configuration/agents-md#how-codex-discovers-guidance),
[Claude memory/import rules](https://code.claude.com/docs/en/memory) and
[Claude configuration environment variables](https://code.claude.com/docs/en/env-vars).
