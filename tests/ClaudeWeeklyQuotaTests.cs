using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiMcpContainers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiMcpContainers.Tests;

public class ClaudeWeeklyQuotaTests
{
    [Fact]
    public void Live_weekly_limit_shape_is_recognized_without_becoming_auth_failure()
    {
        const string raw = """{"is_error":true,"api_error_status":429,"result":"You've hit your weekly limit · resets Nov 4, 10am (UTC)"}""";
        Assert.True(ClaudeRunner.LooksLikeClaudeSessionLimit(raw));
        Assert.False(ClaudeRunner.LooksLikeClaudeAuthFailure(raw));
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero),
            ClaudeRunner.TryParseSessionLimitResetUtc(raw, new DateTimeOffset(2026, 11, 2, 14, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData("resets Jan 2, 12am (UTC)", 2026, 12, 30, 2027, 1, 2, 0)]
    [InlineData("resets Nov 4, 12pm (UTC)", 2026, 11, 2, 2026, 11, 4, 12)]
    [InlineData("resets Nov 4, 2026, 10am (UTC)", 2026, 11, 2, 2026, 11, 4, 10)]
    public void Dated_reset_keeps_calendar_date_and_utc(string text, int year, int month, int day,
        int resetYear, int resetMonth, int resetDay, int hour)
    {
        Assert.Equal(new DateTimeOffset(resetYear, resetMonth, resetDay, hour, 0, 0, TimeSpan.Zero),
            ClaudeRunner.TryParseSessionLimitResetUtc(text, new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData("resets Feb 30, 10am (UTC)")]
    [InlineData("resets Nov 4, 13pm (UTC)")]
    [InlineData("resets Nov 4, 0am (UTC)")]
    [InlineData("resets Nov 4, 10:99am (UTC)")]
    [InlineData("resets Nov 4, 10am (PST)")]
    [InlineData("resets Nov 1, 10am (UTC)")]
    [InlineData("resets Nov 30, 10am (UTC)")]
    [InlineData("resets Novel 4, 10am (UTC)")]
    public void Invalid_stale_or_unreasonably_distant_dated_reset_is_not_invented(string text)
        => Assert.Null(ClaudeRunner.TryParseSessionLimitResetUtc(text,
            new DateTimeOffset(2026, 11, 2, 14, 0, 0, TimeSpan.Zero)));

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task Weekly_quota_crosses_real_cli_and_http_boundaries_and_cached_repeat_does_not_spawn(bool reuse, int exit)
    {
        if (OperatingSystem.IsWindows()) return; // POSIX executable fixture; the production bridge is Linux.
        var dir = Path.Combine(Path.GetTempPath(), "bridge-quota-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var reset = DateTimeOffset.UtcNow.AddDays(2).Date.AddHours(10);
            var resetUtc = new DateTimeOffset(reset, TimeSpan.Zero);
            var message = "You've hit your weekly limit · resets " + resetUtc.ToString("MMM d, htt", CultureInfo.InvariantCulture).ToLowerInvariant() + " (UTC)";
            var script = Path.Combine(dir, "claude.sh");
            var payload = JsonSerializer.Serialize(new { type = "result", is_error = true, api_error_status = 429, result = message });
            // No external provider/credentials: real runner launches this executable exactly once.
            File.WriteAllText(script, "#!/bin/sh\nprintf x >> '" + dir + "/calls'\ncat >/dev/null\ncat <<'JSON'\n" + payload + "\nJSON\nexit " + exit + "\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var runner = new ClaudeRunner(new ClaudeRunnerOptions { ClaudeBinary = script, DefaultCwd = dir,
                Timeout = TimeSpan.FromSeconds(10), HomeDir = dir, ReuseSession = reuse }, NullLogger<ClaudeRunner>.Instance);
            using var app = new QuotaApp(runner);
            using var client = app.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", TestApp.DefaultToken);
            for (var i = 0; i < 2; i++)
            {
                var response = await client.PostAsJsonAsync("/chat", new { provider = "claude", prompt = "independent review", conversationId = "quota-" + i });
                Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal("rate_limited", body.RootElement.GetProperty("code").GetString());
                Assert.Equal("weekly", body.RootElement.GetProperty("limitKind").GetString());
                Assert.Equal(resetUtc, body.RootElement.GetProperty("retryAtUtc").GetDateTimeOffset());
                Assert.Contains("weekly", body.RootElement.GetProperty("error").GetString());
                Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
                Assert.True(response.Headers.RetryAfter?.Delta <= resetUtc - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2));
            }
            var compat = await client.PostAsJsonAsync("/v1/responses", new { model = "default", input = "independent review" });
            Assert.Equal(HttpStatusCode.TooManyRequests, compat.StatusCode);
            using var compatBody = JsonDocument.Parse(await compat.Content.ReadAsStringAsync());
            Assert.Equal("weekly", compatBody.RootElement.GetProperty("limitKind").GetString());
            Assert.True(compat.Headers.RetryAfter?.Delta > TimeSpan.Zero);
            Assert.Equal("x", File.ReadAllText(Path.Combine(dir, "calls")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private sealed class QuotaApp(ClaudeRunner runner) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("AiMcp:Binary", "/bin/sh");
            builder.UseSetting("AiMcp:ClaudeBinary", "/bin/sh");
            builder.UseSetting("AiMcp:DefaultCwd", "/tmp");
            builder.UseSetting("AiMcp:Roots", "/tmp");
            builder.UseSetting("AiMcp:Provider", "claude");
            builder.UseSetting("AiMcp:Providers", "claude");
            builder.UseSetting("AiMcp:Token", TestApp.DefaultToken);
            builder.UseSetting("AI_MCP_CODEX_INNER_SANDBOX", "available");
            builder.ConfigureServices(services => {
                services.RemoveAll<IAgentRunnerRegistry>();
                services.AddSingleton<IAgentRunnerRegistry>(new AgentRunnerRegistry(new Dictionary<string, IAgentRunner> { ["claude"] = runner }));
            });
        }
    }
}
