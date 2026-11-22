using AiMcpContainers;
using Xunit;

namespace AiMcpContainers.Tests;

public class ProviderHealthTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Observations_are_per_provider_expire_and_recover()
    {
        var clock = new Clock();
        var health = new ProviderHealth(clock);
        Assert.Equal("unknown", health.State("claude"));
        health.Observe("claude", new(false, "", "claude_auth_failed", "auth"));
        Assert.Equal("authentication_failed", health.State("claude"));
        Assert.Equal("unknown", health.State("codex"));
        clock.Now += TimeSpan.FromSeconds(61);
        Assert.Equal("unknown", health.State("claude"));
        health.Observe("claude", new(false, "", "rate_limited", "quota", RetryAtUtc: clock.Now.AddDays(1)));
        Assert.Equal("quota_limited", health.State("claude"));
        clock.Now += TimeSpan.FromSeconds(61);
        Assert.Equal("quota_limited", health.State("claude"));
        clock.Now += TimeSpan.FromMinutes(15);
        Assert.Equal("unknown", health.State("claude"));
        health.Observe("claude", new(false, "", "rate_limited", "quota", RetryAtUtc: clock.Now.AddSeconds(5)));
        clock.Now += TimeSpan.FromSeconds(6);
        Assert.Equal("unknown", health.State("claude"));
        health.Observe("claude", new(true, "ok", null, null));
        Assert.Equal("ready", health.State("claude"));
    }

    [Fact]
    public async Task Wrapper_reports_results_without_gating_recovery_calls()
    {
        var health = new ProviderHealth();
        var inner = new FakeCodexRunner { Next = new(false, "", "claude_auth_failed", "auth") };
        var runner = new BoundedAgentRunner(inner, 1, health, "claude");
        await runner.RunAsync("p", null, null, "read-only", null, CancellationToken.None);
        Assert.Equal("authentication_failed", health.State("claude"));
        inner.Next = new(true, "ok", null, null);
        await runner.RunAsync("p", null, null, "read-only", null, CancellationToken.None);
        Assert.Equal("ready", health.State("claude"));
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public void Missing_or_nonexecutable_binary_is_not_ready()
    {
        Assert.False(ProviderHealth.ExecutableExists("/missing/ai-mcp-binary"));
        if (!OperatingSystem.IsLinux()) return;
        Assert.True(ProviderHealth.ExecutableExists("sh"));
        var path = Path.GetTempFileName();
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.False(ProviderHealth.ExecutableExists(path));
        }
        finally { File.Delete(path); }
    }
}
