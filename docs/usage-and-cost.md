# Understand usage before estimating cost

Codex and Claude use their own accounts and subscriptions. Asking the complementary
agent to review work consumes that provider's allowance; it does not spend the same
credit from both accounts. A reviewer often receives fewer requests, but a long
context or a different model can still make an individual review expensive.

## A transparent illustration

| Illustrative workload | Main agent | Reviewing agent | Combined requests |
| --- | ---: | ---: | ---: |
| Implementation alone | 10 | 0 | 10 |
| Implementation with two reviews | 10 | 2 | 12 |

This example adds **20% more requests**. It would add 20% cost only if the average
cost per request were equal. It is an illustration, not a measured overhead
forecast: model, context, output length, cache behavior, subscription allowances and
iteration count all matter. A request count is not a credit count. Stop once the
evidence supports completion; the policy permits up to 20 peer requests, not 20
mandatory rounds.

The intended value is earlier discovery of supported mistakes, clearer scope and
less avoidable rework. Critical planning, small patches and reused test evidence
can help avoid repeated corrections from incomplete prompts. No measured bug
reduction, guaranteed savings or percentage improvement is claimed. For an
evaluation, compare similar tasks and record actual usage, accepted defects, elapsed
time and necessary rework rather than treating agreement as proof of correctness.

## What the initiating client receives

Each dispatched MCP provider call returns the answer and an invocation receipt. The
answer is the first text block and the receipt is the second. The structured output
carries the same two things: `structuredContent.answer` (or `structuredContent.error`
for a failed call) and `structuredContent.receipt`. This is because some clients give
their model only the structured output when it is present. Both receipt representations
have the **same invocation ID** and must be counted once.

| Receipt field | Meaning |
| --- | --- |
| `invocationId`, `provider`, `durationMs` | Unique bridge invocation, account/provider side and elapsed time |
| `usage.source`, `usage.scope` | Native measurement source and whether counts are additive |
| `usage.models[]` | Reported model or unknown model, native optional token counters and inclusion semantics |
| `estimatedApiCostUsd` | Native API-equivalent estimate when reported; not a subscription charge |
| `subscriptionCredits` | `null`: current CLIs do not provide reliable task-specific subscription credits |

Missing usage is `null`, never a fabricated zero. Error, timeout and canceled calls
return unavailable usage. A successful call can also lack counters. Reported zero
is retained only when the native CLI actually reports it. Input/cache/reasoning
fields retain native semantics: cached input and reasoning output may already be
included in their parent counts, so adding them again would overstate consumption.

The host maintains the collaboration task record in the background, deduplicates
receipts by invocation ID, and reports per-provider/model sums only for compatible
`per_call` measurements. It shows measured-call coverage against attempted bridge
calls and lists non-additive or missing measurements separately. Individual metric
coverage also matters: an omitted output counter is not zero output. There is no
server-global billing ledger or automatic cross-account balance query.

Default fresh invocations can report per-call usage. Resumed Claude results are
`cumulative_session` and excluded from additive totals. Resumed Codex results, or
JSONL streams with several completed turns, are `unknown` scope until their native
semantics are verified. Claude `modelUsage` is preferred over the main-loop `usage`
field; the two are never added together. API-style fallback input excludes cache;
the model-usage input/cache relationship remains unspecified, so no invented common
input total is produced. `/v1/responses` maps verified fresh Codex counts to standard
usage and returns `usage: null` when a reliable mapping is unavailable; native
`provider_usage` remains available separately.

The bridge cannot observe the initiating client's own work outside its calls.
Unless that client supplies reliable native evidence, its local usage remains
**unmeasured**. Bridge receipts alone are not a complete task or account bill, and
failed native recovery attempts may have unreported consumption. Do not infer
task credits from account-balance changes while other sessions may be running.

An honest final report can therefore say “Claude: 2 of 3 bridge calls measured,
per-model token details available; Codex initiating session: unmeasured; subscription
credits: unavailable.” This is a reporting example, not a record of a real run.

Codex JSON events increase captured stdout relative to plain text; capture was
already unbounded. The feature retains that existing behavior. This is a known
memory consideration for unusually large native output, not a claim that JSON has
no overhead.

The distinction between subscription allowance and API pricing follows
[OpenAI pricing guidance](https://learn.chatgpt.com/docs/pricing) and
[Claude cost guidance](https://code.claude.com/docs/en/costs). Native usage behavior is
documented in [Codex non-interactive mode](https://learn.chatgpt.com/docs/non-interactive-mode),
[Claude headless results](https://code.claude.com/docs/en/headless),
[Claude SDK usage](https://code.claude.com/docs/en/agent-sdk/typescript#sdkresultmessage)
and [Claude cache accounting](https://platform.claude.com/docs/en/build-with-claude/prompt-caching#tracking-cache-performance).
