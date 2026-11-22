using System.Text.Json;
using AiMcpContainers;
using Xunit;

namespace AiMcpContainers.Tests;

public class NativeUsageTests
{
    private const string CodexEvent = """{"type":"turn.completed","usage":{"input_tokens":100,"cached_input_tokens":70,"output_tokens":20,"reasoning_output_tokens":8}}""";

    [Fact]
    public void Fresh_codex_usage_preserves_included_subsets_and_maps_standard_totals()
    {
        var usage = NativeUsage.FromCodexEvents("diagnostic\n" + CodexEvent)!;
        Assert.Equal("per_call", usage.Scope);
        var model = Assert.Single(usage.Models);
        Assert.Null(model.Model);
        Assert.Equal(100, model.InputTokens);
        Assert.Equal(70, model.CachedInputTokens);
        Assert.Equal(8, model.ReasoningOutputTokens);
        Assert.True(model.InputIncludesCache);
        Assert.Null(usage.SubscriptionCredits);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ResponsesApi.BuildResponse("ok", "m", "codex", usage)));
        var standard = doc.RootElement.GetProperty("usage");
        Assert.Equal(120, standard.GetProperty("total_tokens").GetInt64());
        Assert.Equal(70, standard.GetProperty("input_tokens_details").GetProperty("cached_tokens").GetInt64());
    }

    [Fact]
    public void Resumed_or_multiple_codex_turns_are_not_additive()
    {
        Assert.Equal("unknown", NativeUsage.FromCodexEvents(CodexEvent, resumed: true)!.Scope);
        var multiple = NativeUsage.FromCodexEvents(CodexEvent + "\n" + CodexEvent)!;
        Assert.Equal("unknown", multiple.Scope);
        Assert.Null(NativeUsage.ToResponsesUsage(multiple));
        Assert.Equal(100, CodexRunner.TryParseLastInputTokensFromJsonEvents(CodexEvent));
        Assert.Equal(100, CodexRunner.TryParseLastInputTokensFromJsonEvents(CodexEvent + "\n" + """{"type":"turn.completed","usage":{}}"""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"type\":\"error\",\"message\":\"failed\"}")]
    [InlineData("[]\nnull\ninvalid")]
    public void Missing_native_usage_stays_unavailable(string events) => Assert.Null(NativeUsage.FromCodexEvents(events));

    [Fact]
    public void Invalid_counters_are_unknown_and_reported_zero_is_preserved()
    {
        var usage = NativeUsage.FromCodexEvents("""{"type":"turn.completed","usage":{"input_tokens":-1,"output_tokens":0,"cached_input_tokens":"5"}}""")!;
        Assert.Null(usage.Models[0].InputTokens);
        Assert.Equal(0, usage.Models[0].OutputTokens);
        Assert.Null(usage.Models[0].CachedInputTokens);
        Assert.Null(NativeUsage.ToResponsesUsage(usage));
    }

    [Fact]
    public void Claude_prefers_model_usage_without_adding_main_loop_counts()
    {
        var usage = NativeUsage.FromClaudeResult("""{"usage":{"input_tokens":999},"modelUsage":{"main":{"inputTokens":10,"outputTokens":7,"cacheReadInputTokens":40},"helper":{"inputTokens":3,"outputTokens":2}},"total_cost_usd":0.012}""")!;
        Assert.Equal("claude.result.modelUsage", usage.Source);
        Assert.Equal(2, usage.Models.Count);
        Assert.Equal(10, usage.Models[0].InputTokens);
        Assert.Null(usage.Models[0].InputIncludesCache);
        Assert.Equal(0.012m, usage.EstimatedApiCostUsd);
        Assert.Null(usage.SubscriptionCredits);
        Assert.Null(NativeUsage.ToResponsesUsage(usage));
        Assert.Equal("cumulative_session", NativeUsage.FromClaudeResult("""{"modelUsage":{"main":{"inputTokens":1}}}""", true)!.Scope);
    }

    [Fact]
    public void Claude_fallback_preserves_separate_cache_counts_and_unknowns()
    {
        var usage = NativeUsage.FromClaudeResult("""{"usage":{"input_tokens":8,"cache_read_input_tokens":12,"cache_creation_input_tokens":4}}""")!;
        var model = Assert.Single(usage.Models);
        Assert.False(model.InputIncludesCache);
        Assert.Equal(8, model.InputTokens);
        Assert.Equal(12, model.CacheReadInputTokens);
        Assert.Null(model.OutputTokens);
        Assert.Null(NativeUsage.FromClaudeResult("not json"));
    }

    [Theory]
    [InlineData("{\"type\":\"error\",\"message\":\"HTTP 429 usage limit exceeded\"}")]
    [InlineData("{\"type\":\"turn.failed\",\"error\":{\"message\":\"quota exceeded\"}}")]
    public void Codex_jsonl_limit_errors_keep_existing_classification(string json) =>
        Assert.True(CodexRunner.LooksLikeCodexUsageLimit(json));

    [Fact]
    public void Jsonl_auth_failure_is_not_misclassified_as_a_usage_limit()
    {
        const string json = """{"type":"error","message":"HTTP 401 authentication required"}""";
        Assert.False(CodexRunner.LooksLikeCodexUsageLimit(json));
        Assert.False(CodexRunner.LooksLikeSessionRolloverFailure(json));
    }

    [Fact]
    public void Codex_jsonl_context_failure_keeps_existing_classification() =>
        Assert.True(CodexRunner.LooksLikeSessionRolloverFailure("""{"type":"turn.failed","error":{"message":"context window exceeded"}}"""));
}
