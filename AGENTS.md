# Agent rules

The main session coordinates the work and writes code.
Delegate planning, architecture and costly-to-reverse decisions to planner.
Delegate tests, lint and build commands to test_runner.
Delegate file and code search to reader.
Delegate web and documentation research to researcher.
When the relevant custom agent is available, do not perform that task in the main session.

## Mandatory test evidence reuse — owner, 2026-11-19

This rule applies globally to every project and to delegated agents. Do not rerun a previously passing test when its relevant source, dependencies, fixtures, test selection and execution configuration are unchanged and its passing evidence remains available. Reuse that evidence. Do not repeat tests merely because a session resumes, a ticket changes stage, a progress note is written or a review requests already available proof. Do not run a whole suite to compensate for an unavailable targeted command or a blocked permission profile.

Rerun only the affected tests when relevant code, dependencies, fixtures, the tested artifact or execution configuration changes, a related failure or regression is discovered, or a concrete finding invalidates the previous evidence. Fix a discovered problem and rerun its affected tests. For new behavior, run only new tests and directly affected existing consumers. A failed test may be rerun after a relevant fix; unrelated passing tests retain their evidence.

Before any test, lint, build or replay validation command, identify the affected behavior, the prior evidence being reused and the concrete reason for each required run. Select tests precisely, record the source or artifact and result, and stop after the necessary checks pass. If authorization prevents the targeted command, report the block instead of broadening the test selection. Do not describe unexecuted checks as passing. This is a mandatory instruction, not a suggestion, and must be included when delegating verification work.

<!-- BEGIN AI-MCP COLLABORATION -->
Read and follow `.ai-mcp/collaboration.md` for this project's collaboration workflow.
<!-- END AI-MCP COLLABORATION -->
