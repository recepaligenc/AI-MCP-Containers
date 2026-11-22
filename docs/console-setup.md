# One-prompt MCP setup and a persistent collaboration rule

You can ask either Codex or Claude Code to configure its MCP connections and save the
collaboration workflow for later sessions. The interactive client needs local file and
command permissions for that setup. This is an instruction-driven installation recipe,
not a built-in installer or an automatic orchestration engine.

The default recipe below makes **MCP registrations user-scoped** for the current
client, matching the [connection examples](../README.md#connect-claude-and-codex-as-mcp-clients),
and makes the **collaboration rule project-scoped**. Run it once in each client that
you use; both clients reuse the same two services and the same project rule.

## Paste this into either console

Open the target project in Codex or Claude Code and paste this prompt. It deliberately
contains no private key or machine-specific path. The agent should discover existing
paths/settings and ask only for information that cannot be found safely.

```text
Set up the current interactive host coding client to use both existing
AI-MCP-Containers providers and persist their collaboration workflow for this project.

1. Locate the AI-MCP-Containers checkout from the workspace or existing MCP settings.
   Read its README connection instructions and docs/templates/ai-mcp-collaboration.md.
   Identify the current client, target project root, Node executable, adapter script,
   correct backend URLs and existing private token-file path. Ask only for missing
   paths/settings that cannot be discovered safely. Treat credential files as opaque:
   do not print their contents, put keys in command arguments, or copy secrets into
   prompts, client configuration literals, project rules or version control.
2. Configure both reserved stdio registrations, ai_mcp_codex and ai_mcp_claude, for
   the current client at user scope using the documented configuration. The tools
   must be ask_codex and ask_claude. Use AI_MCP_ENV_FILE for the existing private key
   file and keep the documented timeout ordering. Preserve other client settings.
   Back up affected private configuration files privately and merge idempotently.
   Reuse the existing two provider services and their credentials; leave server mounts
   and provider permissions unchanged. Report unavailable prerequisites explicitly.
3. Copy the shared collaboration template, including its BEGIN/END AI-MCP
   COLLABORATION markers, into .ai-mcp/collaboration.md in the target project.
   Preserve existing content outside that block and update the block idempotently
   instead of duplicating it or replacing unrelated rules.
4. Add a managed instruction to the active project Codex guidance file to read and
   follow .ai-mcp/collaboration.md. Use AGENTS.md unless an existing non-empty
   AGENTS.override.md is the active guidance; preserve it instead of creating a new
   override that would shadow other instructions.
5. Add the shared rule as an import in the existing active project Claude guidance:
   use @.ai-mcp/collaboration.md in root CLAUDE.md, or
   @../.ai-mcp/collaboration.md in .claude/CLAUDE.md. Create root CLAUDE.md if neither
   exists. Preserve other instructions and add each pointer/import only once.
6. Verify registration and tool discovery without making model calls merely to test
   installation. Reuse unchanged passing evidence; do not run project tests for this
   configuration-only change. If a new client session is required, report that and
   leave live discovery pending rather than claiming it succeeded.
7. Keep setup local and reviewable, without automatically committing or publishing
   the target project's changes. Report changed files, registration/rule scopes,
   verification actually performed and anything still needed. Explain that a new
   client session is needed to load the persistent instructions and MCP settings.
```

The agent must still follow the client's real permission and approval policy. If it
cannot edit user configuration, it should prepare the exact change and report the
blocked step; a prompt cannot grant filesystem permissions or bypass an approval.

The token file is loaded by the adapter at startup. An exported `AI_MCP_TOKEN` takes
precedence, so remove an obsolete exported value if it points at another installation.
Registration/tool discovery does not prove HTTP authentication, provider login or
remaining subscription quota. A real provider request is a separate end-to-end check
and consumes quota.

## What becomes persistent

The [shared template](templates/ai-mcp-collaboration.md) directs the coordinating
client to obtain a complementary agent's critique for substantial plans and changes,
review code and test results, and preserve available passing test evidence. Providers
receive bounded, stateless review tasks; the host client performs edits and tests.
Small edits do not trigger a mandatory pair of model requests. Missing reviews and
quota failures must be reported rather than treated as successful reviews.

The template is a starting point. Keep existing project rules, adapt its managed
section deliberately, and review the resulting diff. This repository ships the
template as documentation; it does not activate it in your projects automatically.

Typical project layout:

```text
your-project/
  .ai-mcp/collaboration.md   # One canonical workflow, without secrets or host paths.
  AGENTS.md                # Codex instruction to read the shared workflow.
  CLAUDE.md                # Claude import of the same workflow.
```

The managed part of root `AGENTS.md` can be:

```markdown
<!-- BEGIN AI-MCP COLLABORATION -->
Read and follow `.ai-mcp/collaboration.md` for this project's collaboration workflow.
<!-- END AI-MCP COLLABORATION -->
```

The managed part of root `CLAUDE.md` can be:

```markdown
<!-- BEGIN AI-MCP COLLABORATION -->
@.ai-mcp/collaboration.md
<!-- END AI-MCP COLLABORATION -->
```

If Codex already uses a non-empty `AGENTS.override.md`, add the pointer there instead
of adding an ignored `AGENTS.md` rule. If the active Claude file is under `.claude/`,
its import must use `@../.ai-mcp/collaboration.md`. Relative imports are resolved from
the instruction file's location.

Start a new client session after installation. Codex builds its instruction chain at
session startup; Claude also loads project instructions into new conversations. See
the [official OpenAI AGENTS.md guide](https://developers.openai.com/codex/guides/agents-md)
and [Claude instruction/import documentation](https://code.claude.com/docs/en/memory).

## Use the rule across all projects

If you explicitly want the workflow in every project, change the setup prompt to
request a **user-scoped rule**, not just user-scoped MCP registration. Keep one copy
of the template in a user-owned configuration directory and reference it from:

| Client | User-level instruction file |
| --- | --- |
| Codex | `$CODEX_HOME/AGENTS.md`, normally `~/.codex/AGENTS.md`; an active `AGENTS.override.md` takes precedence. |
| Claude Code | `~/.claude/CLAUDE.md`, using an import with the shared file's absolute path. |

Preserve existing user instructions and keep personal paths and keys out of tracked
project files. A global rule affects all projects, but it does not grant the providers
access beyond their configured project mounts. Existing higher-priority instructions
and client permissions still apply.

Persistent rules are model context, not enforced security controls or a guarantee that
every step will execute. See [Claude's instruction guidance](https://code.claude.com/docs/en/memory#write-effective-instructions).
Report the actual reviews/tests performed; use application enforcement separately when
an operation must be guaranteed or forbidden.
