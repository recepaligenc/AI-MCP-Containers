using AiMcpContainers;
using Xunit;

namespace AiMcpContainers.Tests;

// ClaudeRunner runs `claude -p --output-format json`. The answer is in `result`; the model that
// ACTUALLY served the request is the (typically only) key of `modelUsage` — there is no top-level
// `model` field. These cover that parse plus the degrade-to-text fallback so a future CLI change
// can never drop the answer.
public class ClaudeRunnerParseTests
{
    [Fact]
    public void Parses_result_text_and_model_from_modelUsage_key()
    {
        const string raw = """
        {
          "type": "result",
          "subtype": "success",
          "is_error": false,
          "result": "hello from claude",
          "usage": { "input_tokens": 10, "output_tokens": 3 },
          "modelUsage": {
            "claude-sonnet-4-6-20250930": { "inputTokens": 10, "outputTokens": 3, "cacheReadInputTokens": 0 }
          }
        }
        """;

        var (answer, model) = ClaudeRunner.ParseJsonResult(raw);

        Assert.Equal("hello from claude", answer);
        Assert.Equal("claude-sonnet-4-6-20250930", model);
    }

    [Fact]
    public void Picks_main_model_not_background_helper_when_multiple_present()
    {
        // Real-world shape: Claude Code runs a small haiku helper alongside the main model. The
        // helper is listed FIRST but only sees a tiny prompt; the main model (opus here) received
        // the full context (huge inputTokens + cacheRead). We must report the main model, not haiku.
        const string raw = """
        {
          "result": "answer",
          "modelUsage": {
            "claude-haiku-4-5-20251001": { "inputTokens": 443, "outputTokens": 11, "cacheReadInputTokens": 0 },
            "claude-opus-4-8":           { "inputTokens": 3587, "outputTokens": 4, "cacheReadInputTokens": 24832 }
          }
        }
        """;

        var (_, model) = ClaudeRunner.ParseJsonResult(raw);

        Assert.Equal("claude-opus-4-8", model);
    }

    [Fact]
    public void Main_model_detection_is_order_independent()
    {
        // Same as above but the main model is listed FIRST — selection must not depend on order.
        const string raw = """
        {
          "result": "answer",
          "modelUsage": {
            "claude-opus-4-8":           { "inputTokens": 3587, "cacheReadInputTokens": 24832 },
            "claude-haiku-4-5-20251001": { "inputTokens": 443, "cacheReadInputTokens": 0 }
          }
        }
        """;

        var (_, model) = ClaudeRunner.ParseJsonResult(raw);

        Assert.Equal("claude-opus-4-8", model);
    }

    [Fact]
    public void Json_without_modelUsage_yields_null_model()
    {
        const string raw = """{"type":"result","subtype":"success","result":"answer only"}""";

        var (answer, model) = ClaudeRunner.ParseJsonResult(raw);

        Assert.Equal("answer only", answer);
        Assert.Null(model);
    }

    [Fact]
    public void Non_json_output_degrades_to_plain_text()
    {
        // A future CLI change (or text mode) → treat stdout as the answer, model unknown.
        var (answer, model) = ClaudeRunner.ParseJsonResult("just plain text\n");

        Assert.Equal("just plain text", answer);
        Assert.Null(model);
    }

    [Fact]
    public void Malformed_json_degrades_to_raw_text()
    {
        // Starts with '{' but isn't valid JSON: keep the raw text rather than losing the answer.
        const string raw = "{not valid json";
        var (answer, model) = ClaudeRunner.ParseJsonResult(raw);

        Assert.Equal(raw, answer);
        Assert.Null(model);
    }

    [Fact]
    public void Empty_output_is_empty_answer()
    {
        var (answer, model) = ClaudeRunner.ParseJsonResult("");
        Assert.Equal("", answer);
        Assert.Null(model);
    }

    [Fact]
    public void Empty_modelUsage_object_yields_null_model()
    {
        const string raw = """{"result":"x","modelUsage":{}}""";
        var (answer, model) = ClaudeRunner.ParseJsonResult(raw);
        Assert.Equal("x", answer);
        Assert.Null(model);
    }

    [Fact]
    public void Parses_session_id()
    {
        const string raw = """{"session_id":"9cc9b96e-5c0e-420b-8b9d-1ab973397c44","result":"x"}""";

        Assert.Equal("9cc9b96e-5c0e-420b-8b9d-1ab973397c44", ClaudeRunner.TryParseSessionId(raw));
    }

    [Fact]
    public void Parses_total_input_tokens_from_usage()
    {
        const string raw = """
        {
          "usage": {
            "input_tokens": 10,
            "cache_creation_input_tokens": 20,
            "cache_read_input_tokens": 30
          }
        }
        """;

        Assert.Equal(60, ClaudeRunner.TryParseInputTokens(raw));
    }

    [Theory]
    [InlineData("context window exceeded")]
    [InlineData("too many tokens in conversation")]
    [InlineData("session not found")]
    public void Detects_failures_that_should_roll_over_to_new_session(string detail)
        => Assert.True(ClaudeRunner.LooksLikeSessionRolloverFailure(detail));

    [Fact]
    public void Session_limit_is_not_treated_as_rollover_failure()
    {
        const string detail = """{"api_error_status":429,"result":"You've hit your session limit · resets 3:10pm (UTC)"}""";

        Assert.True(ClaudeRunner.LooksLikeClaudeSessionLimit(detail));
        Assert.False(ClaudeRunner.LooksLikeSessionRolloverFailure(detail));
    }

    [Fact]
    public void Parses_session_limit_reset_time_as_next_utc_occurrence()
    {
        const string detail = """You've hit your session limit · resets 3:10pm (UTC)""";
        var now = new DateTimeOffset(2026, 6, 5, 14, 0, 0, TimeSpan.Zero);

        var reset = ClaudeRunner.TryParseSessionLimitResetUtc(detail, now);

        Assert.Equal(new DateTimeOffset(2026, 6, 5, 15, 10, 0, TimeSpan.Zero), reset);
    }

    [Fact]
    public void Parses_session_limit_reset_time_as_tomorrow_when_today_already_passed()
    {
        const string detail = """You've hit your session limit · resets 3:10pm (UTC)""";
        var now = new DateTimeOffset(2026, 6, 5, 16, 0, 0, TimeSpan.Zero);

        var reset = ClaudeRunner.TryParseSessionLimitResetUtc(detail, now);

        Assert.Equal(new DateTimeOffset(2026, 6, 6, 15, 10, 0, TimeSpan.Zero), reset);
    }
}
