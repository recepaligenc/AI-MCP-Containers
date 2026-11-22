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

Please use this prompt when you would like to connect the current coding client to
both providers and save the collaboration workflow for this project. It helps keep
setup consistent while preserving existing settings and keeping private keys out of
prompts and tracked files. Open the target project in Codex or Claude Code before
pasting it. The prompt contains no private key or machine-specific path; the agent
should discover existing paths/settings and ask only for information that cannot be
found safely.

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
3. Run the deterministic rule installer from the checkout:
   node scripts/install-rules.mjs --project /absolute/path/to/target/project
   Use the target root discovered above. Do not manually copy Markdown files.
   If AI-MCP setup already exists, show its warning and ask me Keep, Update or Override;
   Keep is the default and must make no changes. In a non-interactive console,
   rerun with --existing keep, --existing update or --existing override only after I answer. Never
   select Override on my behalf. Update adds missing files/references and preserves
   existing policy bytes; conflicting references stop before writes. Update does
   not merge new clauses into customized policy. Changed existing instructions are
   backed up privately. Override backs up changed existing files and
   preserves unrelated instructions. Do not weaken the policy during installation.
4. For rules across my native clients' projects, use --user only if I choose that
   scope. It installs one canonical policy in the Claude config root and references
   it from both clients. Respect CODEX_HOME and CLAUDE_CONFIG_DIR. Both clients need
   access to that policy; the installer does not grant mounts or filesystem access.
5. Report the actual canonical path and active Codex/Claude reference paths. Preserve
   existing AGENTS.override.md precedence and Claude instruction layout. Start a
   new session to load newly installed instructions; an installer exit code alone
   does not prove client discovery or adherence.
6. Verify saved rule contents, active instruction paths, imports and MCP registration.
   Reuse unchanged passing evidence; do not rerun project tests for this setup change.
   Verify automatic guidance discovery in a fresh session using the client's loaded-
   file view or approved, path-only startup diagnostics. Check that its guidance leads
   to the canonical rule. A manually forced file read or self-report is not proof of
   persistence. Do not enable plaintext session logging automatically or expose full
   logs. If you cannot inspect fresh-session loading, leave that verification pending.
7. In the fresh session, verify the mandatory workflow with a small synthetic plan
   and patch proposal, without changing application files. Ask the complementary
   provider: "Strictly critique this plan." Revise from supported findings, then ask:
   "Critique this implementation and test evidence." Include the synthetic proposal
   and its test strategy, with concrete constraints and questions. Label synthetic examples and unexecuted
   tests honestly. This explicitly requested functional check may consume provider
   quota and counts toward the shared 20-request task budget. Reuse valid existing
   evidence and stop on quota/availability failure rather than retrying unchanged calls.
8. Keep setup local and reviewable, without automatically committing or publishing
   the target project's changes. Report changed files, registration/rule scopes,
   request count, verification actually performed and anything still needed. Report
   persistence loading, MCP discovery and real peer-review execution as separate
   results. A listed tool or attempted call is not a successful peer review. Do not
   declare full verification complete while a required response or fresh load is missing.
```

The agent must still follow the client's real permission and approval policy. If it
cannot edit user configuration, it should prepare the exact change and report the
blocked step; a prompt cannot grant filesystem permissions or bypass an approval.

The token file is loaded by the adapter at startup. An exported `AI_MCP_TOKEN` takes
precedence, so remove an obsolete exported value if it points at another installation.
Registration/tool discovery does not prove HTTP authentication, provider login or
remaining subscription quota. The requested functional workflow check uses real
provider requests and consumes quota; an unavailable provider leaves that check pending.

## What becomes persistent

The [shared template](templates/ai-mcp-collaboration.md) directs the coordinating
client to obtain a complementary agent's plan review before implementation and a
review of the final changes and test evidence before completion. This is mandatory
for technical implementation plans, application code, runtime configuration, security
behavior, UI and tests. Documentation, code and configuration audits require review
of the final findings, even without edits or with no findings. The host sends the
scope, inspected artifacts, methods, evidence and limitations with
"Critique these audit findings and evidence." Audit-only work uses this review in
place of the plan and implementation stages; substantive fix proposals or edits use the
existing plan and implementation gates. Direct factual lookups without evaluation
and spelling/formatting-only edits are otherwise exempt; audits are not. Changes to
rendered UI retain applicable UI checks.

Claude-led work uses Codex as the reviewer; Codex-led work uses Claude. The client
revises and resubmits concrete changes until acceptance criteria and review evidence
support completion, with at most 20 complementary-provider requests for the entire
task. Failed dispatched requests count, and restarting or splitting work does not reset
the count. Successful unchanged review/test evidence can be reused. The workflow stops
early when ready, or stops as incomplete on exhausted budget, unresolved material
disagreement, missing information or provider unavailability.

Each review assesses correctness, architectural fit, security, performance and
maintenance cost. The preferred solution is the smallest sufficient durable change,
not a fragile workaround or speculative redesign. Providers receive bounded, stateless
review tasks; the host client applies edits and executes only justified checks.
Missing reviews and quota failures block a completion claim unless the user explicitly
waives the review for that task. A waiver is reported as a waiver, never as a review.

Every peer-review request explicitly asks for critique; plan reviews use
"Strictly critique this plan." The reviewer challenges assumptions and relevant missing cases
with evidence, rather than merely endorsing the proposal. No fixed number of defects
is required. The host keeps the task's outcome, acceptance criteria and non-goals
stable unless the user changes them. Optional ideas and unverified findings are
recorded separately from justified in-scope corrections; material expansion needs
user approval. This helps preserve project direction while still allowing necessary
fixes. See the [scope and evidence gates](../README.md#keep-iterations-focused).

For UI design or redesign, present three distinct visual mockup alternatives and
obtain the user's explicit approval before applying the design. Preserve existing
fonts unless requested otherwise, and favor simple interactions. Every visible UI
change needs AI inspection of rendered views and evidence for affected interactions.
The text-only MCP adapter does not itself send images; an image-capable host client
can inspect the views while the complementary provider reviews code and interaction
evidence. Do not claim image review from a text description or file path alone.

The saved policy prohibits agents from changing, weakening or bypassing it or its
references on their own initiative. Amendments require an explicit user instruction
identifying the intended change. A reported task-specific waiver is not a permanent
exception. Keep unrelated project rules and review authorized changes to the policy.

This repository activates the rule for its own future sessions through root
`AGENTS.md`, root `CLAUDE.md` and [the canonical policy](../.ai-mcp/collaboration.md).
The [template](templates/ai-mcp-collaboration.md) matches that policy. Other projects
receive it through the automatic installer, or use one user-level policy across projects.
See the [step-by-step installation guide](getting-started.md) for setup and Keep/Update/Override choices.

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
Read and follow ".ai-mcp/collaboration.md" before any technical work, including documentation, code or configuration audits, planning, or implementation. This owner-controlled policy also governs delegated agents.
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

## Verify persistence and actual use separately

If the installation agent could not perform fresh-session verification, please open
a new session in the target project and use this follow-up prompt. It helps you check
three separate things: whether the rule loads, whether the tools are available, and
whether the complementary provider actually responds. This makes missing setup steps
easier to identify before you rely on the workflow. Reuse valid existing evidence;
new functional requests consume provider quota.

```text
Follow the project's persistent instructions. First identify automatically loaded
guidance from the client's loaded-file view or approved startup evidence, and verify
that its pointer/import leads to the installed canonical collaboration policy
(project scope or user scope). Do not count a manually
forced file read or an unsupported self-report as persistence evidence. If automatic
loading cannot be inspected, report it as pending. State the mandatory review stages
and the current task's request count out of 20. Verify MCP registration/tool discovery.

Then exercise the rule with a small synthetic implementation plan. Ask the
complementary provider: "Strictly critique this plan." Revise a synthetic patch
proposal using supported findings, then ask:
"Critique this implementation and test evidence."
Include the proposal and test strategy, constraints and specific questions.
Keep application files
unchanged and label all synthetic or unexecuted test evidence. Reuse prior valid
evidence; do not repeat project tests merely for setup. Report rule loading, tool
discovery and actual peer-review responses separately, with pending/failed steps.
Stop on quota failure or missing prerequisites; do not claim full success without
the required provider responses.
```

| Result | Required evidence |
| --- | --- |
| Persistent rule loaded | Fresh-session discovery evidence for the active guidance and its pointer/import to the canonical rule, followed under that guidance. File existence, a manually forced read or self-report alone is insufficient. |
| MCP connection initialized | Client reports the server and its advertised tool; this alone does not verify the provider request. |
| Peer workflow exercised | Actual complementary-provider responses for the synthetic plan and resulting proposal, with review decisions and request count. |
| Application tests | Reported as not run for this synthetic setup check; never inferred from peer-review text. |

These are observable verification results, not proof that a model can never ignore
an instruction. The 20-request counter and completion gate are client instructions in
this recipe; this project does not enforce them through a server-side loop or hook.

The owner-controlled policy is also an instruction, not filesystem immutability.
An agent with write authority can technically alter its files; changing file mode,
adding a Git hook or calling a file protected does not remove that authority.
Hard enforcement requires agent-external permissions or managed policy that the agent
cannot change. No such administrative control is installed by this recipe.

Claude's `/memory` and `/context all` views expose loaded instruction paths. Codex's
[official verification guide](https://developers.openai.com/codex/guides/agents-md#verify-your-setup)
describes auditing instruction discovery through startup/session evidence. Inspect
only the needed path records locally; keep complete logs and transcripts private.
Plaintext diagnostic logging must be an explicit choice, outside tracked project files.
See [Claude's loading checks](https://code.claude.com/docs/en/memory#my-agentsmd-isnt-loading).

## Apply the rule to a development task

After installation, the saved rule is intended to apply without repeating a prompt.
Please use this task prompt when you would like to restate the completion conditions
for a particular development task. It gives both agents shared acceptance criteria
and helps keep changes focused, reviews critical and evidence-based, and completed
tests from being repeated unnecessarily.

```text
Apply the persistent Claude-Codex collaboration rule as a completion requirement
for this development task. If you are Codex, use ask_claude as the peer; if you are
Claude Code, use ask_codex. Record the authorized outcome, acceptance criteria,
non-goals and anticipated change surfaces. Before implementation, ask the peer:
"Strictly critique this plan." Include the concrete proposal, constraints and questions.

Choose the smallest sufficient durable change that fits this service's architecture.
Have the peer assess correctness, security, performance and maintenance cost. Evaluate
findings against evidence, implement justified corrections and resubmit the relevant
changes and test evidence. Do not apply suggestions blindly or expand unrelated scope.
Require critique of each substantive solution and its rationale before adoption;
batch related proposals. Treat hypotheses as unverified, park optional ideas, and
apply only justified in-scope corrections. Ask me before a material scope expansion,
with its impact and a bounded alternative; routine authorized choices need no new
approval. For final review ask: "Critique this implementation and test evidence."
An endorsement without substantive assessment does not satisfy review. A supported
READY with no defects is valid; do not manufacture problems to continue iterating.

For UI design or redesign, first present three distinct visual mockup alternatives
and wait for my explicit approval before implementing the design. Preserve existing
project fonts unless I ask otherwise. For visible UI changes, inspect real rendered
views with an image-capable AI and check affected interactions with actual evidence.
Keep the UI simple and report missing visual capability rather than claiming review.

Use at most 20 dispatched peer-review requests for the entire task, including errors,
plan review and final review. Preserve the count across subtasks and resumed sessions.
Reuse unchanged passing test/review evidence. Finish early when acceptance criteria
are met and READY covers the final artifact with no unresolved material findings.

Stop and report incomplete work on exhausted budget, unavailable peer or repeated
material disagreement without progress. Do not claim completion without the required
actual review responses unless I explicitly waive the missing stage for this task.
Report the request count, findings addressed/rejected, evidence and remaining risks.
Do not weaken, rewrite or bypass the persistent policy or its references on your own
initiative. Any policy amendment needs my explicit instruction identifying the change.
```

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


## Usage reporting after a task

The task policy asks the host to retain MCP usage receipts and report measured
per-agent/model counts with coverage. This helps you distinguish a small review
workload from the initiating client's separate work. Missing subscription credits
remain unavailable, and duplicate text/structured receipts count once. See
[usage and cost reporting](usage-and-cost.md) for the illustrative overhead example
and measurement limits. This is host task-record behavior, not a server billing ledger.
