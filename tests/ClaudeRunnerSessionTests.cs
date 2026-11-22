using AiMcpContainers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiMcpContainers.Tests;

public class ClaudeRunnerSessionTests
{
    private const string SessionId = "11111111-1111-4111-8111-111111111111";

    [Fact]
    public async Task ReadOnly_uses_dontAsk_and_exact_read_tool_allowlist()
    {
        using var temp = new TempTree();
        var script = CreateArgCaptureClaude(temp.Root);
        var runner = CreateRunner(script, temp.Root, maxSessionBytes: 1024 * 1024);

        var result = await runner.RunAsync("inspect", cwd: null, model: null, sandbox: "read-only", conversationId: null, CancellationToken.None);

        Assert.True(result.Ok);
        var args = File.ReadAllLines(Path.Combine(temp.Root, "arg-tokens.log"));
        Assert.Contains("--safe-mode", args);
        Assert.Contains("dontAsk", args);
        var toolsIndex = Array.IndexOf(args, "--tools");
        Assert.True(toolsIndex >= 0);
        Assert.Equal("Read,Grep", args[toolsIndex + 1]);
        Assert.DoesNotContain("plan", args);
        Assert.DoesNotContain("acceptEdits", args);
        Assert.DoesNotContain("Bash", args);
        Assert.DoesNotContain("Edit", args);
        Assert.DoesNotContain("Write", args);
    }

    [Fact]
    public async Task ReadOnlyWeb_adds_only_the_two_web_tools_preapproved_and_never_edit_or_shell()
    {
        using var temp = new TempTree();
        var script = CreateArgCaptureClaude(temp.Root);
        var runner = CreateRunner(script, temp.Root, maxSessionBytes: 1024 * 1024);

        var result = await runner.RunAsync("verify", cwd: null, model: null, sandbox: "read-only-web", conversationId: null, CancellationToken.None);

        Assert.True(result.Ok);
        var args = File.ReadAllLines(Path.Combine(temp.Root, "arg-tokens.log"));
        Assert.Contains("--safe-mode", args);
        Assert.Contains("dontAsk", args);
        Assert.DoesNotContain("acceptEdits", args);
        Assert.Equal("Read,Grep,WebFetch,WebSearch", args[Array.IndexOf(args, "--tools") + 1]);
        Assert.Equal("WebFetch,WebSearch", args[Array.IndexOf(args, "--allowedTools") + 1]);
        Assert.DoesNotContain(args, a => a.Contains("Bash") || a.Contains("Edit") || a.Contains("Write"));
    }

    [Fact]
    public async Task WorkspaceWrite_keeps_acceptEdits_without_read_only_allowlist()
    {
        using var temp = new TempTree();
        var script = CreateArgCaptureClaude(temp.Root);
        var runner = CreateRunner(script, temp.Root, maxSessionBytes: 1024 * 1024);

        var result = await runner.RunAsync("edit", cwd: null, model: null, sandbox: "workspace-write", conversationId: null, CancellationToken.None);

        Assert.True(result.Ok);
        var args = File.ReadAllLines(Path.Combine(temp.Root, "arg-tokens.log"));
        Assert.Contains("--safe-mode", args);
        Assert.Contains("acceptEdits", args);
        Assert.DoesNotContain("dontAsk", args);
        Assert.DoesNotContain("--tools", args);
        Assert.DoesNotContain("Read,Grep", args);
    }

    [Fact]
    public async Task ReadOnly_resume_reapplies_dontAsk_and_read_tool_allowlist()
    {
        using var temp = new TempTree();
        var script = CreateFakeClaude(temp.Root, sessionBytes: 128);
        var runner = CreateRunner(script, temp.Root, maxSessionBytes: 1024 * 1024);

        var first = await runner.RunAsync("first", cwd: null, model: null, sandbox: "read-only", conversationId: null, CancellationToken.None);
        var second = await runner.RunAsync("second", cwd: null, model: null, sandbox: "read-only", conversationId: null, CancellationToken.None);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        var lines = File.ReadAllLines(Path.Combine(temp.Root, "args.log"));
        Assert.Equal(2, lines.Length);
        Assert.Contains("--resume", lines[1]);
        Assert.Contains("--permission-mode dontAsk", lines[1]);
        Assert.Contains("--tools Read,Grep", lines[1]);
        Assert.DoesNotContain("acceptEdits", lines[1]);
    }

    [Fact]
    public async Task ReuseSession_resumes_previous_session_for_matching_request_key()
    {
        using var temp = new TempTree();
        var script = CreateFakeClaude(temp.Root, sessionBytes: 128);
        var runner = CreateRunner(script, temp.Root, maxSessionBytes: 1024 * 1024);

        var first = await runner.RunAsync("first", cwd: null, model: null, sandbox: "workspace-write", conversationId: null, CancellationToken.None);
        var second = await runner.RunAsync("second", cwd: null, model: null, sandbox: "workspace-write", conversationId: null, CancellationToken.None);

        Assert.True(first.Ok);
        Assert.True(second.Ok);

        var lines = File.ReadAllLines(Path.Combine(temp.Root, "args.log"));
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.Contains("--safe-mode", line));
        Assert.DoesNotContain("--resume", lines[0]);
        Assert.Contains("--resume", lines[1]);
        Assert.Contains(SessionId, lines[1]);
    }

    [Fact]
    public async Task ReuseSession_rotates_when_session_file_exceeds_configured_size()
    {
        using var temp = new TempTree();
        var script = CreateFakeClaude(temp.Root, sessionBytes: 128);
        var runner = CreateRunner(script, temp.Root, maxSessionBytes: 16);

        await runner.RunAsync("first", cwd: null, model: null, sandbox: "workspace-write", conversationId: null, CancellationToken.None);
        await runner.RunAsync("second", cwd: null, model: null, sandbox: "workspace-write", conversationId: null, CancellationToken.None);

        var lines = File.ReadAllLines(Path.Combine(temp.Root, "args.log"));
        Assert.Equal(2, lines.Length);
        Assert.DoesNotContain("--resume", lines[1]);
    }

    [Fact]
    public async Task ReuseSession_rotates_when_previous_input_tokens_exceed_configured_limit()
    {
        using var temp = new TempTree();
        var script = CreateFakeClaude(temp.Root, sessionBytes: 8, inputTokens: 200);
        var runner = CreateRunner(script, temp.Root, maxSessionBytes: 1024 * 1024, maxSessionInputTokens: 100);

        await runner.RunAsync("first", cwd: null, model: null, sandbox: "workspace-write", conversationId: null, CancellationToken.None);
        await runner.RunAsync("second", cwd: null, model: null, sandbox: "workspace-write", conversationId: null, CancellationToken.None);

        var lines = File.ReadAllLines(Path.Combine(temp.Root, "args.log"));
        Assert.Equal(2, lines.Length);
        Assert.DoesNotContain("--resume", lines[1]);
    }

    [Fact]
    public async Task ReuseSession_persists_session_state_across_runner_instances()
    {
        using var temp = new TempTree();
        var script = CreateFakeClaude(temp.Root, sessionBytes: 8);
        var firstRunner = CreateRunner(script, temp.Root, maxSessionBytes: 1024 * 1024);

        await firstRunner.RunAsync("first", cwd: null, model: null, sandbox: "workspace-write", conversationId: "same", CancellationToken.None);
        var secondRunner = CreateRunner(script, temp.Root, maxSessionBytes: 1024 * 1024);
        await secondRunner.RunAsync("second", cwd: null, model: null, sandbox: "workspace-write", conversationId: "same", CancellationToken.None);

        var lines = File.ReadAllLines(Path.Combine(temp.Root, "args.log"));
        Assert.Equal(2, lines.Length);
        Assert.Contains("--resume", lines[1]);
        Assert.Contains(SessionId, lines[1]);
    }

    [Fact]
    public async Task ReuseSession_keeps_distinct_conversation_ids_in_separate_sessions()
    {
        using var temp = new TempTree();
        var script = CreateFakeClaude(temp.Root, sessionBytes: 8);
        var runner = CreateRunner(script, temp.Root, maxSessionBytes: 1024 * 1024);

        await runner.RunAsync("first", cwd: null, model: null, sandbox: "workspace-write", conversationId: "a", CancellationToken.None);
        await runner.RunAsync("second", cwd: null, model: null, sandbox: "workspace-write", conversationId: "b", CancellationToken.None);

        var lines = File.ReadAllLines(Path.Combine(temp.Root, "args.log"));
        Assert.Equal(2, lines.Length);
        Assert.DoesNotContain("--resume", lines[1]);
    }

    [Fact]
    public async Task SessionPool_runs_calls_concurrently_and_caps_live_sessions_at_pool_size()
    {
        using var temp = new TempTree();
        const int poolSize = 3;
        var inflightDir = Path.Combine(temp.Root, "inflight");
        var releaseFile = Path.Combine(temp.Root, "release");
        Directory.CreateDirectory(inflightDir);
        var script = CreateBarrierFakeClaude(temp.Root, inflightDir, releaseFile);
        var runner = CreateRunner(script, temp.Root, maxSessionBytes: 1024 * 1024, sessionPoolSize: poolSize);

        // Fire more calls than the pool can serve at once. With a pool of N exactly N should run in
        // parallel (held at the barrier); the rest wait on a slot. If the pool serialized (size 1)
        // only one would ever be in-flight and the wait below would time out.
        const int total = poolSize + 2;
        var tasks = Enumerable.Range(0, total)
            .Select(_ => runner.RunAsync("p", cwd: null, model: null, sandbox: "workspace-write", conversationId: null, CancellationToken.None))
            .ToArray();

        var peakInFlight = await WaitForInFlight(inflightDir, expected: poolSize, ceiling: poolSize);
        File.WriteAllText(releaseFile, "go");
        var results = await Task.WhenAll(tasks);
        // Exactly N ran together: == N proves concurrency (a serialized pool of 1 would peak at 1 and
        // the wait above would time out); WaitForInFlight already asserted it never exceeded N.
        Assert.Equal(poolSize, peakInFlight);
        Assert.All(results, r => Assert.True(r.Ok));

        // Live session count is bounded by the pool: the first wave created N fresh sessions and the
        // trailing calls resumed (reused) them, so the persisted state holds exactly N — not `total`.
        var persisted = File.ReadAllText(Path.Combine(temp.Root, ".claude", "claude-bridge-sessions.json"));
        using var doc = System.Text.Json.JsonDocument.Parse(persisted);
        var sessionCount = doc.RootElement.GetProperty("Sessions").GetArrayLength();
        Assert.Equal(poolSize, sessionCount);

        var resumeCount = File.ReadAllLines(Path.Combine(temp.Root, "args.log")).Count(l => l.Contains("--resume"));
        Assert.Equal(total - poolSize, resumeCount); // the 2 trailing calls reused a pooled session
    }

    // Polls the in-flight marker dir until `expected` processes are simultaneously parked at the
    // barrier, returning the peak seen. Fails loudly (via the caller's assert) if the peak never
    // reaches `expected` (serialized) — and asserts it never exceeds `ceiling` (slot cap breached).
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

    private static async Task WaitForArgsLogLines(string root, int expected)
    {
        var path = Path.Combine(root, "args.log");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) && File.ReadAllLines(path).Length >= expected)
                return;
            await Task.Delay(25);
        }
        Assert.Fail($"args.log did not reach {expected} lines");
    }

    [Fact]
    public void Constructor_prunes_inactive_session_files_older_than_configured_days()
    {
        using var temp = new TempTree();
        var script = CreateFakeClaude(temp.Root, sessionBytes: 8);
        var projectsDir = Path.Combine(temp.Root, ".claude", "projects", "-workspace");
        Directory.CreateDirectory(projectsDir);
        var oldFile = Path.Combine(projectsDir, "old.jsonl");
        File.WriteAllText(oldFile, "old");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-3));

        _ = CreateRunner(script, temp.Root, maxSessionBytes: 1024 * 1024, pruneSessionDays: 1);

        Assert.False(File.Exists(oldFile));
    }

    [Fact]
    public async Task NoSessionPersistence_adds_flag_to_fresh_non_reused_runs()
    {
        using var temp = new TempTree();
        var script = CreateFakeClaude(temp.Root, sessionBytes: 8);
        var runner = new ClaudeRunner(
            new ClaudeRunnerOptions
            {
                ClaudeBinary = script,
                HomeDir = temp.Root,
                DefaultCwd = temp.Root,
                Timeout = TimeSpan.FromSeconds(10),
                ReuseSession = false,
                NoSessionPersistence = true,
            },
            NullLogger<ClaudeRunner>.Instance);

        await runner.RunAsync("first", cwd: null, model: null, sandbox: "workspace-write", conversationId: null, CancellationToken.None);

        var line = Assert.Single(File.ReadAllLines(Path.Combine(temp.Root, "args.log")));
        Assert.Contains("--no-session-persistence", line);
        Assert.DoesNotContain("--resume", line);
    }

    [Fact]
    public async Task Session_limit_failure_opens_cooldown_without_spawning_again()
    {
        using var temp = new TempTree();
        var script = CreateSessionLimitFakeClaude(temp.Root);
        var runner = CreateRunner(script, temp.Root, maxSessionBytes: 1024 * 1024);

        var first = await runner.RunAsync("first", cwd: null, model: null, sandbox: "workspace-write", conversationId: null, CancellationToken.None);
        var second = await runner.RunAsync("second", cwd: null, model: null, sandbox: "workspace-write", conversationId: null, CancellationToken.None);

        Assert.False(first.Ok);
        Assert.False(second.Ok);
        Assert.Equal("rate_limited", first.ErrorCode);
        Assert.Equal("rate_limited", second.ErrorCode);

        var line = Assert.Single(File.ReadAllLines(Path.Combine(temp.Root, "args.log")));
        Assert.DoesNotContain("--resume", line);
    }

    [Fact]
    public async Task Queued_call_observes_session_limit_cooldown_after_slot_release()
    {
        using var temp = new TempTree();
        var releaseFile = Path.Combine(temp.Root, "release");
        var script = CreateBarrierSessionLimitFakeClaude(temp.Root, releaseFile);
        var runner = CreateRunner(script, temp.Root, maxSessionBytes: 1024 * 1024, sessionPoolSize: 1);

        var first = runner.RunAsync("first", cwd: null, model: null, sandbox: "workspace-write", conversationId: null, CancellationToken.None);
        await WaitForArgsLogLines(temp.Root, expected: 1);
        var second = runner.RunAsync("second", cwd: null, model: null, sandbox: "workspace-write", conversationId: null, CancellationToken.None);

        File.WriteAllText(releaseFile, "go");
        var results = await Task.WhenAll(first, second);

        Assert.All(results, r => Assert.Equal("rate_limited", r.ErrorCode));
        Assert.Single(File.ReadAllLines(Path.Combine(temp.Root, "args.log")));
    }

    [Fact]
    public void Session_context_guard_wraps_prompt()
    {
        var wrapped = ClaudeRunner.WithSessionContextGuard("do only this");

        Assert.Contains("Treat the current request as independent", wrapped);
        Assert.Contains("do only this", wrapped);
    }

    [Fact]
    public async Task Anonymous_calls_do_not_resume_other_requests_when_secure_reuse_is_enabled()
    {
        using var temp = new TempTree();
        var script = CreateFakeClaude(temp.Root, sessionBytes: 8);
        var runner = CreateRunner(script, temp.Root, maxSessionBytes: 1024 * 1024, requireConversationId: true);
        Assert.True((await runner.RunAsync("one", null, null, "read-only", null, CancellationToken.None)).Ok);
        Assert.True((await runner.RunAsync("two", null, null, "read-only", null, CancellationToken.None)).Ok);
        Assert.DoesNotContain("--resume", File.ReadAllLines(Path.Combine(temp.Root, "args.log"))[1]);
    }

    private static ClaudeRunner CreateRunner(
        string script,
        string home,
        long maxSessionBytes,
        long maxSessionInputTokens = 0,
        int pruneSessionDays = 0,
        long maxTotalSessionBytes = 0,
        int sessionPoolSize = 1,
        bool requireConversationId = false) =>
        new(
            new ClaudeRunnerOptions
            {
                ClaudeBinary = script,
                HomeDir = home,
                DefaultCwd = home,
                Timeout = TimeSpan.FromSeconds(10),
                ReuseSession = true,
                RequireConversationIdForReuse = requireConversationId,
                SessionPoolSize = sessionPoolSize,
                MaxSessionBytes = maxSessionBytes,
                MaxSessionInputTokens = maxSessionInputTokens,
                PruneSessionDays = pruneSessionDays,
                MaxTotalSessionBytes = maxTotalSessionBytes,
            },
            NullLogger<ClaudeRunner>.Instance);

    // A fake claude that parks at a barrier so the test can observe how many run at once. Each fresh
    // spawn mints a unique session id (sess-<pid>); a --resume call echoes the id back (true reuse),
    // so distinct ids == distinct live sessions.
    private static string CreateBarrierFakeClaude(string root, string inflightDir, string releaseFile)
    {
        var script = Path.Combine(root, "barrier-claude.sh");
        // Plain raw string + placeholder replacement: the embedded JSON has runs of `}}}` that would
        // clash with `$$"""` interpolation's `}}` delimiters, so we avoid interpolation entirely.
        var body = """
            #!/usr/bin/env sh
            args="$*"
            printf '%s\n' "$args" >> "__ROOT__/args.log"
            cat >/dev/null
            marker="__INFLIGHT__/$$"
            : > "$marker"
            while [ ! -f "__RELEASE__" ]; do sleep 0.02; done
            rm -f "$marker"
            case "$args" in
              *--resume*) sid="${args##*--resume }"; sid="${sid%% *}" ;;
              *) sid="sess-$$" ;;
            esac
            mkdir -p "$HOME/.claude/projects/-workspace"
            printf 'x\n' > "$HOME/.claude/projects/-workspace/$sid.jsonl"
            printf '{"result":"ok","session_id":"%s","usage":{"input_tokens":1,"cache_creation_input_tokens":0,"cache_read_input_tokens":0},"modelUsage":{"claude-test":{"inputTokens":1}}}\n' "$sid"
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

    private static string CreateFakeClaude(string root, int sessionBytes, long inputTokens = 10)
    {
        var script = Path.Combine(root, "fake-claude.sh");
        var sessionPayload = new string('x', sessionBytes);
        var result = "{\"result\":\"fake-answer\",\"session_id\":\"" + SessionId +
                     "\",\"usage\":{\"input_tokens\":" + inputTokens +
                     ",\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0},\"modelUsage\":{\"claude-test\":{\"inputTokens\":1}}}";
        File.WriteAllText(script, $$"""
            #!/usr/bin/env sh
            printf '%s\n' "$*" >> "{{root}}/args.log"
            cat >/dev/null
            mkdir -p "$HOME/.claude/projects/-workspace"
            printf '{{sessionPayload}}\n' > "$HOME/.claude/projects/-workspace/{{SessionId}}.jsonl"
            printf '{{result}}\n'
            """);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return script;
    }

    private static string CreateArgCaptureClaude(string root)
    {
        var script = Path.Combine(root, "arg-capture-claude.sh");
        var body = """
            #!/usr/bin/env sh
            printf '%s\n' "$@" > "__ROOT__/arg-tokens.log"
            cat >/dev/null
            mkdir -p "$HOME/.claude/projects/-workspace"
            printf 'x\n' > "$HOME/.claude/projects/-workspace/__SESSION_ID__.jsonl"
            printf '{"result":"fake-answer","session_id":"__SESSION_ID__","usage":{"input_tokens":1,"cache_creation_input_tokens":0,"cache_read_input_tokens":0},"modelUsage":{"claude-test":{"inputTokens":1}}}\n'
            """;
        File.WriteAllText(script, body
            .Replace("__ROOT__", root)
            .Replace("__SESSION_ID__", SessionId));

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return script;
    }

    private static string CreateSessionLimitFakeClaude(string root)
    {
        var script = Path.Combine(root, "limited-claude.sh");
        var body = """
            #!/usr/bin/env sh
            printf '%s\n' "$*" >> "__ROOT__/args.log"
            cat >/dev/null
            cat <<'JSON'
            {"type":"result","subtype":"success","is_error":true,"api_error_status":429,"result":"You've hit your session limit · resets 11:59pm (UTC)","session_id":"limited-session","usage":{"input_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0},"modelUsage":{}}
            JSON
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

    private static string CreateBarrierSessionLimitFakeClaude(string root, string releaseFile)
    {
        var script = Path.Combine(root, "barrier-limited-claude.sh");
        var body = """
            #!/usr/bin/env sh
            printf '%s\n' "$*" >> "__ROOT__/args.log"
            cat >/dev/null
            while [ ! -f "__RELEASE__" ]; do sleep 0.02; done
            cat <<'JSON'
            {"type":"result","subtype":"success","is_error":true,"api_error_status": 429,"result":"You've hit your session limit · resets 11:59pm (UTC)","session_id":"limited-session","usage":{"input_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0},"modelUsage":{}}
            JSON
            exit 1
            """;
        File.WriteAllText(script, body
            .Replace("__ROOT__", root)
            .Replace("__RELEASE__", releaseFile));

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
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"claude-bridge-tests-{Guid.NewGuid():N}");

        public TempTree() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch { }
        }
    }
}
