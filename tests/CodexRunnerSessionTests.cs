using AiMcpContainers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiMcpContainers.Tests;

public class CodexRunnerSessionTests
{
    private const string ThreadId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task Provider_calls_disable_reserved_mcp_servers_for_fresh_and_resumed_runs()
    {
        using var temp = new TempTree();
        var script = CreateFakeCodex(temp.Root, sessionBytes: 128);
        var runner = CreateRunner(script, temp.Root, maxSessionBytes: 1024 * 1024);

        Assert.True((await runner.RunAsync("first", null, null, "read-only", null, CancellationToken.None)).Ok);
        Assert.True((await runner.RunAsync("review", null, null, "read-only", null, CancellationToken.None)).Ok);

        var calls = File.ReadAllLines(Path.Combine(temp.Root, "args.log"));
        Assert.Equal(2, calls.Length);
        Assert.DoesNotContain("exec resume", calls[0]);
        Assert.Contains("exec resume", calls[1]);
        Assert.All(calls, args =>
        {
            Assert.Contains("--config mcp_servers.ai_mcp_codex={command=\"/bin/false\",enabled=false}", args);
            Assert.Contains("--config mcp_servers.ai_mcp_claude={command=\"/bin/false\",enabled=false}", args);
        });
    }

    [Fact]
    public async Task Config_default_model_is_passed_explicitly_to_the_cli()
    {
        using var temp = new TempTree();
        var script = CreateFakeCodex(temp.Root, sessionBytes: 128);
        File.WriteAllText(Path.Combine(temp.Root, "config.toml"), "model = \"gpt-6-sol\"\n");
        var runner = CreateRunner(script, temp.Root, maxSessionBytes: 1024 * 1024);
        var result = await runner.RunAsync("first", null, null, "read-only", null, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Contains("--model gpt-6-sol", Assert.Single(File.ReadAllLines(Path.Combine(temp.Root, "args.log"))));
    }

    [Fact]
    public async Task ReuseSession_resumes_previous_thread_for_matching_request_key()
    {
        using var temp = new TempTree();
        var script = CreateFakeCodex(temp.Root, sessionBytes: 128);
        var runner = CreateRunner(script, temp.Root, maxSessionBytes: 1024 * 1024);

        var first = await runner.RunAsync("first", cwd: null, model: null, sandbox: "read-only", conversationId: null, CancellationToken.None);
        var second = await runner.RunAsync("second", cwd: null, model: null, sandbox: "read-only", conversationId: null, CancellationToken.None);

        Assert.True(first.Ok);
        Assert.True(second.Ok);

        var lines = File.ReadAllLines(Path.Combine(temp.Root, "args.log"));
        Assert.Equal(2, lines.Length);
        Assert.DoesNotContain("exec resume", lines[0]);
        Assert.Contains("exec resume", lines[1]);
        Assert.Contains(ThreadId, lines[1]);
    }

    [Fact]
    public async Task ReuseSession_rotates_when_session_file_exceeds_configured_size()
    {
        using var temp = new TempTree();
        var script = CreateFakeCodex(temp.Root, sessionBytes: 128);
        var runner = CreateRunner(script, temp.Root, maxSessionBytes: 16);

        var first = await runner.RunAsync("first", cwd: null, model: null, sandbox: "read-only", conversationId: null, CancellationToken.None);
        var second = await runner.RunAsync("second", cwd: null, model: null, sandbox: "read-only", conversationId: null, CancellationToken.None);

        Assert.True(first.Ok);
        Assert.True(second.Ok);

        var lines = File.ReadAllLines(Path.Combine(temp.Root, "args.log"));
        Assert.Equal(2, lines.Length);
        Assert.DoesNotContain("exec resume", lines[0]);
        Assert.DoesNotContain("exec resume", lines[1]);
    }

    [Fact]
    public void Parses_thread_started_json_event()
    {
        var stdout = """
        warning: ignored
        {"type":"thread.started","thread_id":"abc"}
        {"type":"turn.completed"}
        """;

        Assert.Equal("abc", CodexRunner.TryParseThreadIdFromJsonEvents(stdout));
    }

    [Fact]
    public void Parses_last_input_tokens_from_turn_completed_event()
    {
        var stdout = """
        {"type":"turn.completed","usage":{"input_tokens":10}}
        {"type":"turn.completed","usage":{"input_tokens":42}}
        """;

        Assert.Equal(42, CodexRunner.TryParseLastInputTokensFromJsonEvents(stdout));
    }

    [Fact]
    public async Task ReuseSession_rotates_when_previous_input_tokens_exceed_configured_limit()
    {
        using var temp = new TempTree();
        var script = CreateFakeCodex(temp.Root, sessionBytes: 8, inputTokens: 200);
        var runner = CreateRunner(script, temp.Root, maxSessionBytes: 1024 * 1024, maxSessionInputTokens: 100);

        var first = await runner.RunAsync("first", cwd: null, model: null, sandbox: "read-only", conversationId: null, CancellationToken.None);
        var second = await runner.RunAsync("second", cwd: null, model: null, sandbox: "read-only", conversationId: null, CancellationToken.None);

        Assert.True(first.Ok);
        Assert.True(second.Ok);

        var lines = File.ReadAllLines(Path.Combine(temp.Root, "args.log"));
        Assert.Equal(2, lines.Length);
        Assert.DoesNotContain("exec resume", lines[1]);
    }

    [Fact]
    public async Task ReuseSession_persists_session_state_across_runner_instances()
    {
        using var temp = new TempTree();
        var script = CreateFakeCodex(temp.Root, sessionBytes: 8);
        var firstRunner = CreateRunner(script, temp.Root, maxSessionBytes: 1024 * 1024);

        var first = await firstRunner.RunAsync("first", cwd: null, model: null, sandbox: "read-only", conversationId: "same", CancellationToken.None);
        var secondRunner = CreateRunner(script, temp.Root, maxSessionBytes: 1024 * 1024);
        var second = await secondRunner.RunAsync("second", cwd: null, model: null, sandbox: "read-only", conversationId: "same", CancellationToken.None);

        Assert.True(first.Ok);
        Assert.True(second.Ok);

        var lines = File.ReadAllLines(Path.Combine(temp.Root, "args.log"));
        Assert.Equal(2, lines.Length);
        Assert.Contains("exec resume", lines[1]);
        Assert.Contains(ThreadId, lines[1]);
    }

    [Fact]
    public async Task ReuseSession_keeps_distinct_conversation_ids_in_separate_sessions()
    {
        using var temp = new TempTree();
        var script = CreateFakeCodex(temp.Root, sessionBytes: 8);
        var runner = CreateRunner(script, temp.Root, maxSessionBytes: 1024 * 1024);

        await runner.RunAsync("first", cwd: null, model: null, sandbox: "read-only", conversationId: "a", CancellationToken.None);
        await runner.RunAsync("second", cwd: null, model: null, sandbox: "read-only", conversationId: "b", CancellationToken.None);

        var lines = File.ReadAllLines(Path.Combine(temp.Root, "args.log"));
        Assert.Equal(2, lines.Length);
        Assert.DoesNotContain("exec resume", lines[1]);
    }

    [Fact]
    public void Constructor_prunes_inactive_session_files_older_than_configured_days()
    {
        using var temp = new TempTree();
        var script = CreateFakeCodex(temp.Root, sessionBytes: 8);
        var sessionsDir = Path.Combine(temp.Root, "sessions", "2026", "06", "05");
        Directory.CreateDirectory(sessionsDir);
        var oldFile = Path.Combine(sessionsDir, "rollout-old.jsonl");
        File.WriteAllText(oldFile, "old");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-3));

        _ = CreateRunner(script, temp.Root, maxSessionBytes: 1024 * 1024, pruneSessionDays: 1);

        Assert.False(File.Exists(oldFile));
    }

    [Fact]
    public async Task Ephemeral_adds_flag_to_fresh_non_reused_runs()
    {
        using var temp = new TempTree();
        var script = CreateFakeCodex(temp.Root, sessionBytes: 8);
        var runner = new CodexRunner(
            new CodexRunnerOptions
            {
                CodexBinary = script,
                CodexHome = temp.Root,
                DefaultCwd = temp.Root,
                Timeout = TimeSpan.FromSeconds(10),
                ReuseSession = false,
                Ephemeral = true,
            },
            NullLogger<CodexRunner>.Instance);

        var result = await runner.RunAsync("first", cwd: null, model: null, sandbox: "read-only", conversationId: null, CancellationToken.None);

        var line = Assert.Single(File.ReadAllLines(Path.Combine(temp.Root, "args.log")));
        Assert.Contains("--ephemeral", line);
        Assert.Contains("--json", line);
        Assert.Equal("per_call", result.Usage!.Scope);
        Assert.DoesNotContain("exec resume", line);
    }

    [Fact]
    public async Task Usage_limit_failure_opens_cooldown_without_spawning_again()
    {
        using var temp = new TempTree();
        var script = CreateUsageLimitFakeCodex(temp.Root);
        var runner = CreateRunner(script, temp.Root, maxSessionBytes: 1024 * 1024);

        var first = await runner.RunAsync("first", cwd: null, model: null, sandbox: "read-only", conversationId: null, CancellationToken.None);
        var second = await runner.RunAsync("second", cwd: null, model: null, sandbox: "read-only", conversationId: null, CancellationToken.None);

        Assert.False(first.Ok);
        Assert.False(second.Ok);
        Assert.Equal("rate_limited", first.ErrorCode);
        Assert.NotNull(first.RetryAtUtc);
        Assert.True(first.RetryAtUtc > DateTimeOffset.UtcNow);
        Assert.Equal(first.RetryAtUtc, second.RetryAtUtc);
        Assert.Null(first.Usage);
        Assert.Equal("rate_limited", second.ErrorCode);

        var line = Assert.Single(File.ReadAllLines(Path.Combine(temp.Root, "args.log")));
        Assert.DoesNotContain("exec resume", line);
    }

    [Fact]
    public void Session_context_guard_wraps_prompt()
    {
        var wrapped = CodexRunner.WithSessionContextGuard("do only this");

        Assert.Contains("Treat the current request as independent", wrapped);
        Assert.Contains("do only this", wrapped);
    }

    [Fact]
    public async Task SessionPool_runs_calls_concurrently_and_caps_live_threads_at_pool_size()
    {
        using var temp = new TempTree();
        const int poolSize = 3;
        var inflightDir = Path.Combine(temp.Root, "inflight");
        var releaseFile = Path.Combine(temp.Root, "release");
        Directory.CreateDirectory(inflightDir);
        var script = CreateBarrierFakeCodex(temp.Root, inflightDir, releaseFile);
        var runner = new CodexRunner(
            new CodexRunnerOptions
            {
                CodexBinary = script,
                CodexHome = temp.Root,
                DefaultCwd = temp.Root,
                Timeout = TimeSpan.FromSeconds(10),
                ReuseSession = true,
                SessionPoolSize = poolSize,
                MaxSessionBytes = 1024 * 1024,
            },
            NullLogger<CodexRunner>.Instance);

        const int total = poolSize + 2;
        var tasks = Enumerable.Range(0, total)
            .Select(_ => runner.RunAsync("p", cwd: null, model: null, sandbox: "read-only", conversationId: null, CancellationToken.None))
            .ToArray();

        var peak = await WaitForInFlight(inflightDir, expected: poolSize, ceiling: poolSize);
        File.WriteAllText(releaseFile, "go");
        var results = await Task.WhenAll(tasks);
        Assert.Equal(poolSize, peak); // N ran in parallel, never more than N (slot cap)
        Assert.All(results, r => Assert.True(r.Ok));

        var persisted = File.ReadAllText(Path.Combine(temp.Root, "codex-bridge-sessions.json"));
        using var doc = System.Text.Json.JsonDocument.Parse(persisted);
        Assert.Equal(poolSize, doc.RootElement.GetProperty("Sessions").GetArrayLength()); // live threads bounded by N
    }

    private static async Task<int> WaitForInFlight(string dir, int expected, int ceiling)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var peak = 0;
        while (DateTime.UtcNow < deadline)
        {
            var n = Directory.GetFiles(dir).Length;
            Assert.True(n <= ceiling, $"in-flight {n} exceeded pool size {ceiling}");
            if (n > peak) peak = n;
            if (peak >= expected) return peak;
            await Task.Delay(25);
        }
        return peak;
    }

    // A fake codex that parks at a barrier. Each fresh spawn mints a unique thread id (thread-<pid>);
    // an `exec resume <id>` call echoes the id back, so distinct ids == distinct live threads.
    private static string CreateBarrierFakeCodex(string root, string inflightDir, string releaseFile)
    {
        var script = Path.Combine(root, "barrier-codex.sh");
        var body = """
            #!/usr/bin/env sh
            args="$*"
            printf '%s\n' "$args" >> "__ROOT__/args.log"
            out=""
            while [ "$#" -gt 0 ]; do
              case "$1" in
                -o|--output-last-message) out="$2"; shift 2 ;;
                *) shift ;;
              esac
            done
            cat >/dev/null
            marker="__INFLIGHT__/$$"
            : > "$marker"
            while [ ! -f "__RELEASE__" ]; do sleep 0.02; done
            rm -f "$marker"
            case "$args" in
              *resume*) tid="${args##*resume }"; tid="${tid%% *}" ;;
              *) tid="thread-$$" ;;
            esac
            [ -n "$out" ] && printf 'fake-answer\n' > "$out"
            mkdir -p "$CODEX_HOME/sessions/2026/06/05"
            printf 'x\n' > "$CODEX_HOME/sessions/2026/06/05/rollout-2026-06-05T00-00-00-$tid.jsonl"
            printf '{"type":"thread.started","thread_id":"%s"}\n' "$tid"
            printf '{"type":"turn.completed","usage":{"input_tokens":1}}\n'
            """;
        File.WriteAllText(script, body
            .Replace("__ROOT__", root)
            .Replace("__INFLIGHT__", inflightDir)
            .Replace("__RELEASE__", releaseFile));

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return script;
    }

    [Theory]
    [InlineData("context length exceeded")]
    [InlineData("too many tokens in conversation")]
    [InlineData("session not found")]
    public void Detects_failures_that_should_roll_over_to_new_session(string detail)
        => Assert.True(CodexRunner.LooksLikeSessionRolloverFailure(detail));

    [Fact]
    public void Usage_limit_is_not_treated_as_rollover_failure()
    {
        const string detail = "You've hit your session limit · resets 3:10pm (UTC)";

        Assert.True(CodexRunner.LooksLikeCodexUsageLimit(detail));
        Assert.False(CodexRunner.LooksLikeSessionRolloverFailure(detail));
    }

    [Fact]
    public void Parses_usage_limit_reset_time_as_next_utc_occurrence()
    {
        const string detail = "You've hit your usage limit. Resets at 3:10pm UTC.";
        var now = new DateTimeOffset(2026, 6, 5, 14, 0, 0, TimeSpan.Zero);

        var reset = CodexRunner.TryParseUsageLimitResetUtc(detail, now);

        Assert.Equal(new DateTimeOffset(2026, 6, 5, 15, 10, 0, TimeSpan.Zero), reset);
    }

    [Fact]
    public async Task Anonymous_calls_do_not_resume_other_requests_when_secure_reuse_is_enabled()
    {
        using var temp = new TempTree();
        var script = CreateFakeCodex(temp.Root, sessionBytes: 8);
        var runner = CreateRunner(script, temp.Root, maxSessionBytes: 1024 * 1024, requireConversationId: true);
        Assert.True((await runner.RunAsync("one", null, null, "read-only", null, CancellationToken.None)).Ok);
        Assert.True((await runner.RunAsync("two", null, null, "read-only", null, CancellationToken.None)).Ok);
        Assert.DoesNotContain("exec resume", File.ReadAllLines(Path.Combine(temp.Root, "args.log"))[1]);
    }

    private static CodexRunner CreateRunner(
        string script,
        string home,
        long maxSessionBytes,
        long maxSessionInputTokens = 0,
        int pruneSessionDays = 0,
        long maxTotalSessionBytes = 0,
        bool requireConversationId = false) =>
        new(
            new CodexRunnerOptions
            {
                CodexBinary = script,
                CodexHome = home,
                DefaultCwd = home,
                Timeout = TimeSpan.FromSeconds(10),
                ReuseSession = true,
                RequireConversationIdForReuse = requireConversationId,
                MaxSessionBytes = maxSessionBytes,
                MaxSessionInputTokens = maxSessionInputTokens,
                PruneSessionDays = pruneSessionDays,
                MaxTotalSessionBytes = maxTotalSessionBytes,
            },
            NullLogger<CodexRunner>.Instance);

    private static string CreateFakeCodex(string root, int sessionBytes, long inputTokens = 10)
    {
        var script = Path.Combine(root, "fake-codex.sh");
        var sessionPayload = new string('x', sessionBytes);
        var threadStarted = "{\"type\":\"thread.started\",\"thread_id\":\"" + ThreadId + "\"}";
        var turnCompleted = "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":" + inputTokens + "}}";
        File.WriteAllText(script, $$"""
            #!/usr/bin/env sh
            printf '%s\n' "$*" >> "{{root}}/args.log"
            out=""
            while [ "$#" -gt 0 ]; do
              case "$1" in
                -o|--output-last-message)
                  out="$2"
                  shift 2
                  ;;
                *)
                  shift
                  ;;
              esac
            done
            cat >/dev/null
            if [ -n "$out" ]; then
              printf 'fake-answer\n' > "$out"
            fi
            mkdir -p "$CODEX_HOME/sessions/2026/06/05"
            printf '{{sessionPayload}}\n' > "$CODEX_HOME/sessions/2026/06/05/rollout-2026-06-05T00-00-00-{{ThreadId}}.jsonl"
            printf '{{threadStarted}}\n'
            printf '{{turnCompleted}}\n'
            """);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return script;
    }

    private static string CreateUsageLimitFakeCodex(string root)
    {
        var script = Path.Combine(root, "limited-codex.sh");
        var body = """
            #!/usr/bin/env sh
            printf '%s\n' "$*" >> "__ROOT__/args.log"
            out=""
            while [ "$#" -gt 0 ]; do
              case "$1" in
                -o|--output-last-message) out="$2"; shift 2 ;;
                *) shift ;;
              esac
            done
            cat >/dev/null
            printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
            printf '%s\n' '{"type":"error","message":"HTTP 429 usage limit · resets 11:59pm (UTC)"}'
            exit 1
            """;
        File.WriteAllText(script, body.Replace("__ROOT__", root));

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return script;
    }

    private sealed class TempTree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"codex-bridge-tests-{Guid.NewGuid():N}");

        public TempTree() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch { }
        }
    }
}
