<!-- BEGIN AI-MCP COLLABORATION -->
# Claude and Codex collaboration

## Ownership and scope

The interactive host client coordinates the work, applies approved edits and executes
tests. The MCP provider services analyze read-only project context and return findings,
patch proposals or test recommendations. Follow the user's current request, existing
project instructions, permissions and approval requirements.

Apply this workflow to substantial planning, features, bug fixes and security changes
when an independent review can affect correctness or design. Keep trivial edits local.
The coordinating client can act as one partner: prefer `ask_claude` when the client is
Codex, and `ask_codex` when the client is Claude Code. Use the reserved registrations
`ai_mcp_claude` and `ai_mcp_codex`.

If you are answering a delegated provider/review request, perform that bounded task
and return your findings. Do not start another collaboration workflow or delegate back
to the coordinating client through these services.

## Plan, implement and review

1. Build a concrete plan from the requested outcome and project constraints. For a
   substantial change, send it to the complementary provider for critique before
   expensive or hard-to-reverse decisions. Ask for risks, missing cases and revisions.
2. Assess the critique against the source and available evidence. Resolve disagreements
   explicitly; do not treat agreement between agents as proof of correctness.
3. Apply accepted changes with the host client's tools. Send the relevant diff and
   necessary context to the complementary provider for an independent code review.
4. Address concrete findings and verify only the affected behavior. Ask a provider for
   test recommendations or analysis of results when that can resolve a real uncertainty.
5. Report the resulting behavior, reviews actually performed, validation evidence and
   any unresolved findings. Never describe an unperformed review or test as passing.

Each MCP call is independent. Include the relevant requirements, prior proposal or
diff, constraints and specific review question in every request. Identify delegated
requests as review-only assignments. Use a `cwd` inside the provider's authorized
mounted root; report missing access instead of widening service mounts automatically.

## Test evidence reuse

Before any test, lint, build or replay validation, identify the affected behavior,
available prior evidence and the concrete reason for the proposed run. Reuse passing
evidence when the relevant source, dependencies, fixtures, selection and execution
configuration are unchanged.

Run new or directly affected checks only when changed behavior, a related failure or
a concrete finding justifies them. Do not repeat a passing check merely because another
agent requests it, a session resumes or a review stage changes. Do not broaden to a full
suite because a targeted command is unavailable or blocked by permissions. A relevant
fix permits rerunning its affected failed checks; unrelated passing evidence remains valid.

## Context and failure handling

Share only the minimum project context needed for the review. Keep credentials,
private ENV files, provider homes, session logs and unrelated projects out of prompts,
tracked rule files and reports. Do not ask providers to retrieve secrets.

If a provider is unavailable, quota-limited or denied by policy, report which review
could not be performed. Honor a returned retry time; do not repeatedly retry the same
unchanged request. Continue independent authorized work where the task's risk permits,
and state the missing review before claiming completion. Never bypass permissions or
silently substitute a fabricated result.
<!-- END AI-MCP COLLABORATION -->
