using System.Diagnostics;
using AiMcpContainers;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AiMcpContainers.Tests;

public class RunnerProcessTests
{
    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    public async Task Deadline_covers_blocked_stdin_and_releases_capacity(string provider)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fake = new FakeProvider("echo $$ > parent.pid\nsleep 30 &\necho $! > child.pid\nwait");
        var runner = new BoundedAgentRunner(fake.Runner(provider, TimeSpan.FromMilliseconds(350)), 1);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var result = await runner.RunAsync(new string('p', 100_000), null, null, "read-only", null, watchdog.Token);
        Assert.Equal("timeout", result.ErrorCode);
        await AssertStopped(fake.Root, "parent.pid");
        await AssertStopped(fake.Root, "child.pid");
        // A second call must reach the provider, rather than seeing bridge_busy.
        var again = await runner.RunAsync("next", null, null, "read-only", null, watchdog.Token);
        Assert.Equal("timeout", again.ErrorCode);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    public async Task Output_is_drained_before_large_prompt_is_written(string provider)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fake = new FakeProvider("head -c 131072 /dev/zero | tr '\\000' '\\n'\ncat > /dev/null\nprintf '{\"result\":\"ok\"}\\n'");
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var result = await fake.Runner(provider, TimeSpan.FromSeconds(4))
            .RunAsync(new string('p', 100_000), null, null, "read-only", null, watchdog.Token);
        Assert.True(result.Ok, result.ErrorCode);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    public async Task Caller_cancellation_stops_blocked_input_and_child_tree(string provider)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fake = new FakeProvider("echo $$ > parent.pid\nsleep 30 &\necho $! > child.pid\nwait");
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
        var result = await fake.Runner(provider, TimeSpan.FromSeconds(4))
            .RunAsync(new string('p', 100_000), null, null, "read-only", null, caller.Token);
        Assert.Equal("cancelled", result.ErrorCode);
        await AssertStopped(fake.Root, "parent.pid");
        await AssertStopped(fake.Root, "child.pid");
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    public async Task Already_cancelled_caller_wins_over_expired_deadline(string provider)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fake = new FakeProvider("sleep 30");
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var result = await fake.Runner(provider, TimeSpan.Zero)
            .RunAsync("p", null, null, "read-only", null, caller.Token);
        Assert.Equal("cancelled", result.ErrorCode);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    public async Task Failure_logs_and_client_error_omit_provider_content(string provider)
    {
        if (!OperatingSystem.IsLinux()) return;
        const string marker = "private-task-content-42";
        using var fake = new FakeProvider($"cat > /dev/null\nprintf '{marker}\\n'\nprintf '{marker}\\n' >&2\nexit 1");
        var result = await fake.Runner(provider, TimeSpan.FromSeconds(4))
            .RunAsync("p", null, null, "read-only", null, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(provider + "_failed", result.ErrorCode);
        Assert.NotEmpty(fake.Messages);
        Assert.DoesNotContain(marker, string.Join("\n", fake.Messages));
        Assert.DoesNotContain(marker, result.ErrorMessage ?? "");
        Assert.DoesNotContain(marker, result.Output);
    }

    private static async Task AssertStopped(string root, string name)
    {
        var pid = File.ReadAllText(Path.Combine(root, name)).Trim();
        var status = $"/proc/{pid}/stat";
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            try
            {
                // An orphan awaiting reaping no longer executes or holds pipe handles.
                if (!File.Exists(status) || File.ReadAllText(status).Split(") ")[1].StartsWith("Z")) return;
            }
            catch (IOException) { return; }
            await Task.Delay(20);
        }
        Assert.Fail($"Fake provider process {pid} is still running.");
    }

    private sealed class FakeProvider : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "runner-process-" + Guid.NewGuid().ToString("N"));
        public List<string> Messages { get; } = new();
        private string Binary => Path.Combine(Root, "provider");

        public FakeProvider(string body)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Binary, "#!/bin/sh\n" + body + "\n");
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(Binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public IAgentRunner Runner(string provider, TimeSpan timeout) => provider == "codex"
            ? new CodexRunner(new CodexRunnerOptions { CodexBinary = Binary, CodexHome = Root,
                DefaultCwd = Root, Timeout = timeout }, new CaptureLogger<CodexRunner>(Messages))
            : new ClaudeRunner(new ClaudeRunnerOptions { ClaudeBinary = Binary, HomeDir = Root,
                DefaultCwd = Root, Timeout = timeout }, new CaptureLogger<ClaudeRunner>(Messages));

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class CaptureLogger<T>(List<string> messages) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => messages.Add(formatter(state, exception) + exception);
    }
}
