using System.Text.Json;

namespace AiMcpContainers;

// Native counters retain their own semantics; missing data is never a measured zero.
public sealed record ModelUsage(
    string? Model,
    long? InputTokens,
    long? OutputTokens,
    long? CachedInputTokens = null,
    long? CacheReadInputTokens = null,
    long? CacheCreationInputTokens = null,
    long? ReasoningOutputTokens = null,
    bool? InputIncludesCache = null,
    bool? OutputIncludesReasoning = null);

public sealed record ProviderUsage(
    string Source,
    string Scope,
    IReadOnlyList<ModelUsage> Models,
    decimal? EstimatedApiCostUsd = null,
    decimal? SubscriptionCredits = null);

public static class NativeUsage
{
    // The last turn also feeds existing session context rotation. Multiple turns are
    // deliberately not represented as a complete, additive command measurement.
    public static ProviderUsage? FromCodexEvents(string stdout, bool resumed = false) =>
        ParseCodexEvents(stdout, resumed).Usage;

    public static (ProviderUsage? Usage, long? LastInputTokens) ParseCodexEvents(string stdout, bool resumed = false)
    {
        ModelUsage? last = null;
        long? lastInputTokens = null;
        var turns = 0;
        foreach (var line in stdout.Split('\n'))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("type", out var type)
                    || type.ValueKind != JsonValueKind.String || type.GetString() != "turn.completed") continue;
                turns++;
                last = root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                    ? new ModelUsage(null, Count(usage, "input_tokens"), Count(usage, "output_tokens"),
                        CachedInputTokens: Count(usage, "cached_input_tokens"),
                        ReasoningOutputTokens: Count(usage, "reasoning_output_tokens"),
                        InputIncludesCache: true, OutputIncludesReasoning: true)
                    : null;
                if (last?.InputTokens is long input) lastInputTokens = input;
            }
            catch (JsonException) { /* Diagnostics can surround native JSONL events. */ }
        }
        return (last is null ? null : new ProviderUsage("codex.turn.completed",
            resumed || turns != 1 ? "unknown" : "per_call", new[] { last }), lastInputTokens);
    }

    public static ProviderUsage? FromClaudeResult(string raw, bool resumed = false)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var models = new List<ModelUsage>();
            var source = "claude.result.modelUsage";
            if (root.TryGetProperty("modelUsage", out var all) && all.ValueKind == JsonValueKind.Object)
                foreach (var entry in all.EnumerateObject())
                    if (entry.Value.ValueKind == JsonValueKind.Object)
                        models.Add(new ModelUsage(entry.Name, Count(entry.Value, "inputTokens"),
                            Count(entry.Value, "outputTokens"),
                            CacheReadInputTokens: Count(entry.Value, "cacheReadInputTokens"),
                            CacheCreationInputTokens: Count(entry.Value, "cacheCreationInputTokens"),
                            OutputIncludesReasoning: true));
            if (models.Count == 0)
            {
                source = "claude.result.usage";
                if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
                models.Add(new ModelUsage(null, Count(usage, "input_tokens"), Count(usage, "output_tokens"),
                    CacheReadInputTokens: Count(usage, "cache_read_input_tokens"),
                    CacheCreationInputTokens: Count(usage, "cache_creation_input_tokens"),
                    InputIncludesCache: false, OutputIncludesReasoning: true));
            }
            decimal? cost = root.TryGetProperty("total_cost_usd", out var value)
                && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && number >= 0
                ? number : null;
            return new ProviderUsage(source, resumed ? "cumulative_session" : "per_call", models, cost);
        }
        catch (JsonException) { return null; }
    }

    private static long? Count(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number
        && field.TryGetInt64(out var number) && number >= 0 ? number : null;

    public static object? ToResponsesUsage(ProviderUsage? usage)
    {
        if (usage is not { Source: "codex.turn.completed", Scope: "per_call" }
            || usage.Models.Count != 1 || usage.Models[0] is not { InputTokens: long input, OutputTokens: long output } model
            || input > long.MaxValue - output) return null;
        return new
        {
            input_tokens = input,
            output_tokens = output,
            total_tokens = input + output,
            input_tokens_details = model.CachedInputTokens is long cached ? new { cached_tokens = cached } : null,
            output_tokens_details = model.ReasoningOutputTokens is long reasoning ? new { reasoning_tokens = reasoning } : null
        };
    }
}
