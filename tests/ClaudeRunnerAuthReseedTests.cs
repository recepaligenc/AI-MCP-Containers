using AiMcpContainers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiMcpContainers.Tests;

/// <summary>
/// The container copies the host's Claude credentials once at START; when the host
/// refreshes its OAuth token the copy is revoked and every call 401s until a manual
/// restart. These tests pin the fix: on an auth-failure the runner reseeds the
/// credentials from the configured seed file and retries ONCE — and never retries
/// when the seed is absent or identical (a same-token retry cannot succeed).
/// </summary>
public class ClaudeRunnerAuthReseedTests
{
    private const string AuthFailureJson =
        """{"type":"result","subtype":"success","is_error":true,"api_error_status":401,"result":"Failed to authenticate. API Error: 401 OAuth access token has been revoked.","session_id":"auth-fail","usage":{"input_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0},"modelUsage":{}}""";

    private const string ExpiredOAuthFailureJson =
        """{"type":"result","is_error":true,"api_error_status":null,"result":"Failed to authenticate: OAuth session expired and could not be refreshed","terminal_reason":"api_error"}""";

    private const string PrettyExpiredOAuthFailureJson = """
        {
          "type": "result",
          "is_error": true,
          "api_error_status": null,
          "result": "Failed to authenticate: OAuth session expired and could not be refreshed"
        }
        """ + "\n \n";

    [Theory]
    [InlineData(AuthFailureJson, true)]
    [InlineData(ExpiredOAuthFailureJson, true)]
    [InlineData("Failed to authenticate: OAuth session expired and could not be refreshed", false)]
    [InlineData("""{"type":"result","is_error":false,"api_error_status":null,"result":"Failed to authenticate: OAuth session expired and could not be refreshed"}""", false)]
    [InlineData("Failed to authenticate. API Error: 401 Invalid authentication credentials", true)]
    [InlineData("\"api_error_status\":401", true)]
    [InlineData("Invalid API key · Please run /login", true)]
    [InlineData("{\"is_error\":true,\"api_error_status\":null,\"result\":\"Not logged in · Please run /login\"}", true)]
    [InlineData("tool output: user is not logged in to grafana", false)]
    [InlineData("{\"api_error_status\":429,\"result\":\"You've hit your session limit\"}", false)]
    [InlineData("tool output: the server failed to authenticate the webhook", false)]
    [InlineData("error: invalid api key for openweather", false)]
    [InlineData("Claude exited with code 1.", false)]
    [InlineData("", false)]
    public void Auth_failure_detector_matches_precise_cli_error_shapes(string detail, bool expected) =>
        Assert.Equal(expected, ClaudeRunner.LooksLikeClaudeAuthFailure(detail));

    [Theory]
    [InlineData(AuthFailureJson)]
    [InlineData(ExpiredOAuthFailureJson)]
    public async Task Auth_failure_reseeds_from_seed_and_retries_once_successfully(string failureJson)
    {
        using var temp = new TempTree();
        var seed = WriteSeed(temp.Root, "fresh-token");
        WriteHomeCredentials(temp.Root, "stale-token");
        var runner = CreateRunner(CreateAuthFakeClaude(temp.Root, failureJson), temp.Root, seed);

        var result = await runner.RunAsync("hi", cwd: null, model: null,
            sandbox: "workspace-write", conversationId: null, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(temp.Root, "args.log")).Length);
        Assert.Equal("fresh-token", File.ReadAllText(HomeCredentialsPath(temp.Root)));
    }

    [Theory]
    [InlineData(ExpiredOAuthFailureJson, 0)]
    [InlineData(ExpiredOAuthFailureJson, 1)]
    [InlineData(PrettyExpiredOAuthFailureJson, 1)]
    public async Task Expired_oauth_stdout_recovers_despite_stderr_diagnostics(string failureJson, int exitCode)
    {
        using var temp = new TempTree();
        var seed = WriteSeed(temp.Root, "fresh-token");
        WriteHomeCredentials(temp.Root, "stale-token");
        var runner = CreateRunner(CreateAuthFakeClaude(temp.Root, failureJson,
            "CLI warning: update available", exitCode), temp.Root, seed, reuseSession: false);

        var result = await runner.RunAsync("hi", cwd: null, model: null,
            sandbox: "read-only", conversationId: null, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(temp.Root, "args.log")).Length);
        Assert.Equal("fresh-token", File.ReadAllText(HomeCredentialsPath(temp.Root)));
    }

    [Fact]
    public async Task Expired_oauth_json_in_stderr_is_not_a_structured_stdout_auth_failure()
    {
        using var temp = new TempTree();
        var seed = WriteSeed(temp.Root, "fresh-token");
        WriteHomeCredentials(temp.Root, "stale-token");
        var runner = CreateRunner(CreateAuthFakeClaude(temp.Root, "", ExpiredOAuthFailureJson),
            temp.Root, seed, reuseSession: false);

        var result = await runner.RunAsync("hi", cwd: null, model: null,
            sandbox: "read-only", conversationId: null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("claude_failed", result.ErrorCode);
        Assert.Single(File.ReadAllLines(Path.Combine(temp.Root, "args.log")));
        Assert.Equal("stale-token", File.ReadAllText(HomeCredentialsPath(temp.Root)));
    }

    [Theory]
    [InlineData(AuthFailureJson)]
    [InlineData(ExpiredOAuthFailureJson)]
    public async Task Auth_failure_with_identical_seed_fails_without_retry(string failureJson)
    {
        using var temp = new TempTree();
        var seed = WriteSeed(temp.Root, "stale-token");
        WriteHomeCredentials(temp.Root, "stale-token");
        var runner = CreateRunner(CreateAuthFakeClaude(temp.Root, failureJson), temp.Root, seed);

        var result = await runner.RunAsync("hi", cwd: null, model: null,
            sandbox: "workspace-write", conversationId: null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("claude_auth_failed", result.ErrorCode);
        Assert.Single(File.ReadAllLines(Path.Combine(temp.Root, "args.log")));
    }

    [Theory]
    [InlineData(AuthFailureJson)]
    [InlineData(ExpiredOAuthFailureJson)]
    public async Task Auth_failure_without_configured_seed_fails_without_retry(string failureJson)
    {
        using var temp = new TempTree();
        WriteHomeCredentials(temp.Root, "stale-token");
        var runner = CreateRunner(CreateAuthFakeClaude(temp.Root, failureJson), temp.Root, seedPath: null);

        var result = await runner.RunAsync("hi", cwd: null, model: null,
            sandbox: "workspace-write", conversationId: null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("claude_auth_failed", result.ErrorCode);
        Assert.Single(File.ReadAllLines(Path.Combine(temp.Root, "args.log")));
    }

    [Theory]
    [InlineData("{\"api_error_status\":403,\"result\":\"Your organization has disabled Claude subscription access for Claude Code\"}", true)]
    [InlineData("{\"api_error_status\":403,\"result\":\"cwd forbidden\"}", true)]
    [InlineData("{\"api_error_status\":401,\"result\":\"organization login failed\"}", false)]
    [InlineData("permission denied writing a file", false)]
    public void Access_forbidden_detector_requires_upstream_403_shape(string detail, bool expected) =>
        Assert.Equal(expected, ClaudeRunner.LooksLikeClaudeAccessForbidden(detail));

    [Fact]
    public async Task Organization_403_is_returned_as_sanitized_typed_failure()
    {
        using var temp = new TempTree();
        var script = Path.Combine(temp.Root, "forbidden-claude.sh");
        File.WriteAllText(script, """
            #!/usr/bin/env sh
            cat >/dev/null
            printf '%s\n' '{"api_error_status":403,"result":"Your organization has disabled Claude subscription access for Claude Code"}'
            exit 1
            """);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var runner = CreateRunner(script, temp.Root, seedPath: null, reuseSession: false);

        var result = await runner.RunAsync("hi", cwd: null, model: null,
            sandbox: "read-only", conversationId: null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("claude_access_forbidden", result.ErrorCode);
        Assert.DoesNotContain("disabled Claude subscription access", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Successful_call_never_touches_credentials()
    {
        using var temp = new TempTree();
        var seed = WriteSeed(temp.Root, "fresh-token");
        WriteHomeCredentials(temp.Root, "fresh-token");
        var runner = CreateRunner(CreateAuthFakeClaude(temp.Root), temp.Root, seed);

        var result = await runner.RunAsync("hi", cwd: null, model: null,
            sandbox: "workspace-write", conversationId: null, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Single(File.ReadAllLines(Path.Combine(temp.Root, "args.log")));
    }

    [Theory]
    [InlineData(AuthFailureJson)]
    [InlineData(ExpiredOAuthFailureJson)]
    public async Task Fresh_path_without_session_reuse_also_recovers(string failureJson)
    {
        using var temp = new TempTree();
        var seed = WriteSeed(temp.Root, "fresh-token");
        WriteHomeCredentials(temp.Root, "stale-token");
        var runner = CreateRunner(CreateAuthFakeClaude(temp.Root, failureJson), temp.Root, seed,
            reuseSession: false);

        var result = await runner.RunAsync("hi", cwd: null, model: null,
            sandbox: "workspace-write", conversationId: null, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(temp.Root, "args.log")).Length);
    }

    [Theory]
    [InlineData(AuthFailureJson)]
    [InlineData(ExpiredOAuthFailureJson)]
    public async Task Concurrent_auth_failures_all_recover_with_one_bounded_retry_each(string failureJson)
    {
        using var temp = new TempTree();
        var seed = WriteSeed(temp.Root, "fresh-token");
        WriteHomeCredentials(temp.Root, "stale-token");
        var runner = CreateRunner(CreateAuthFakeClaude(temp.Root, failureJson), temp.Root, seed,
            sessionPoolSize: 4);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            runner.RunAsync($"call-{i}", cwd: null, model: null,
                sandbox: "workspace-write", conversationId: null, CancellationToken.None)));

        // whoever published the reseed AND every concurrent loser (recent-reseed window)
        // gets exactly one retry — so all succeed and no caller spawns more than twice
        Assert.All(results, r => Assert.True(r.Ok));
        Assert.Equal("fresh-token", File.ReadAllText(HomeCredentialsPath(temp.Root)));
        var calls = File.ReadAllLines(Path.Combine(temp.Root, "args.log")).Length;
        Assert.InRange(calls, 5, 8);   // 4 first attempts + 1..4 retries
    }

    [Theory]
    [InlineData(null)]
    [InlineData("CLI warning: update available")]
    public async Task Expired_oauth_after_reseed_stops_after_one_retry_with_typed_error(string? stderr)
    {
        using var temp = new TempTree();
        var seed = WriteSeed(temp.Root, "new-but-invalid-token");
        WriteHomeCredentials(temp.Root, "stale-token");
        var runner = CreateRunner(CreateAuthFakeClaude(temp.Root, ExpiredOAuthFailureJson, stderr),
            temp.Root, seed, reuseSession: false);

        var result = await runner.RunAsync("hi", cwd: null, model: null,
            sandbox: "read-only", conversationId: null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("claude_auth_failed", result.ErrorCode);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(temp.Root, "args.log")).Length);
        Assert.Equal("new-but-invalid-token", File.ReadAllText(HomeCredentialsPath(temp.Root)));
        Assert.DoesNotContain("OAuth session expired", result.ErrorMessage!, StringComparison.Ordinal);
    }

    private static string HomeCredentialsPath(string home) =>
        Path.Combine(home, ".claude", ".credentials.json");

    private static void WriteHomeCredentials(string home, string content)
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(HomeCredentialsPath(home), content);
    }

    private static string WriteSeed(string root, string content)
    {
        var seedDir = Path.Combine(root, "seed");
        Directory.CreateDirectory(seedDir);
        var path = Path.Combine(seedDir, ".credentials.json");
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Succeeds only when the home credentials file contains "fresh-token";
    /// otherwise emits the real CLI's 401 shape and exits 1.</summary>
    private static string CreateAuthFakeClaude(string root, string failureJson = AuthFailureJson,
        string? stderr = null, int failureExitCode = 1)
    {
        var script = Path.Combine(root, "auth-claude.sh");
        var body = """
            #!/usr/bin/env sh
            printf '%s\n' "$*" >> "__ROOT__/args.log"
            cat >/dev/null
            if grep -q fresh-token "$HOME/.claude/.credentials.json" 2>/dev/null; then
              mkdir -p "$HOME/.claude/projects/-workspace"
              printf 'x\n' > "$HOME/.claude/projects/-workspace/22222222-2222-4222-8222-222222222222.jsonl"
              printf '{"result":"ok","session_id":"22222222-2222-4222-8222-222222222222","usage":{"input_tokens":1,"cache_creation_input_tokens":0,"cache_read_input_tokens":0},"modelUsage":{"claude-test":{"inputTokens":1}}}\n'
            else
            __AUTH_STDERR__
              cat <<'JSON'
            __AUTH_FAILURE__
            JSON
              exit __AUTH_EXIT__
            fi
            """;
        File.WriteAllText(script, body
            .Replace("__ROOT__", root)
            .Replace("__AUTH_FAILURE__", failureJson)
            .Replace("__AUTH_STDERR__", stderr is null ? "" : "cat >&2 <<'STDERR'\n" + stderr + "\nSTDERR")
            .Replace("__AUTH_EXIT__", failureExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private static ClaudeRunner CreateRunner(
        string script, string home, string? seedPath, bool reuseSession = true,
        int sessionPoolSize = 1) =>
        new(
            new ClaudeRunnerOptions
            {
                ClaudeBinary = script,
                HomeDir = home,
                DefaultCwd = home,
                Timeout = TimeSpan.FromSeconds(10),
                ReuseSession = reuseSession,
                SessionPoolSize = sessionPoolSize,
                CredentialSeedPath = seedPath,
            },
            NullLogger<ClaudeRunner>.Instance);

    private sealed class TempTree : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "claude-auth-tests-" + Guid.NewGuid().ToString("N"));
        public TempTree() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }
    }
}
