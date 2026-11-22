using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AiMcpContainers;

public sealed class ClaudeRunnerOptions
{
    /// <summary>Absolute path to the claude binary.</summary>
    public required string ClaudeBinary { get; init; }

    /// <summary>Working directory used when a request omits one.</summary>
    public required string DefaultCwd { get; init; }

    /// <summary>Per-request wall-clock timeout.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Combined stdout/stderr budget and separate final-answer budget, in bytes.</summary>
    public int MaxOutputBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>Optional HOME override so auth (~/.claude) resolves in containers. Null = inherit.</summary>
    public string? HomeDir { get; init; }

    /// <summary>When enabled, continue the previous Claude session for matching cwd/model/sandbox.</summary>
    public bool ReuseSession { get; init; }

    /// <summary>Require an explicit conversation ID before reusing any prior request state.</summary>
    public bool RequireConversationIdForReuse { get; init; }

    /// <summary>
    /// How many concurrent sessions to keep per request key when <see cref="ReuseSession"/> is on.
    /// A single Claude session cannot be resumed by two calls at once (concurrent --resume corrupts
    /// the session JSONL), so reuse with a pool of 1 serializes everything — fine for a trickle, but
    /// a burst of independent calls then queues until the client times out. A pool of N keeps up to
    /// N live sessions and runs up to N calls in parallel, each reusing one of the pooled sessions:
    /// it bounds the live-session count to N (so it stays under a provider session cap) while
    /// restoring N-way concurrency. Clamped to at least 1; 1 == legacy single-session behavior.
    /// </summary>
    public int SessionPoolSize { get; init; } = 1;

    /// <summary>When running fresh print turns, ask Claude not to persist a session file. Ignored for resumed sessions.</summary>
    public bool NoSessionPersistence { get; init; }

    /// <summary>Prepend a guard telling the model not to use earlier resumed-session content as task context.</summary>
    public bool IgnoreSessionContext { get; init; }

    /// <summary>Rotate to a fresh session when the persisted JSONL file reaches this size. 0 disables size-based rotation.</summary>
    public long MaxSessionBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>Rotate before resuming when the previous turn's input context reaches this size. 0 disables token-based rotation.</summary>
    public long MaxSessionInputTokens { get; init; }

    /// <summary>Delete inactive Claude session files older than this many days. 0 disables age-based pruning.</summary>
    public int PruneSessionDays { get; init; }

    /// <summary>Keep total inactive Claude session files under this byte budget. 0 disables size-based pruning.</summary>
    public long MaxTotalSessionBytes { get; init; }

    /// <summary>Optional path for persisted bridge session routing state. Null = $HOME/.claude/claude-bridge-sessions.json.</summary>
    public string? SessionStatePath { get; init; }

    /// <summary>
    /// Optional read-only seed for Claude credentials (the host's ~/.claude/.credentials.json,
    /// mounted e.g. at /seed/.credentials.json). The container copies it once at start; when the
    /// HOST later refreshes its OAuth token, the copy is revoked and every call 401s until a manual
    /// restart. With this set, an auth-failing call reseeds from here and retries ONCE — only when
    /// the seed actually differs (a same-token retry cannot succeed, so no retry storm). Null = off.
    /// </summary>
    public string? CredentialSeedPath { get; init; }
}

/// <summary>
/// Shells out to the Claude Code CLI in print mode (<c>claude -p</c>), feeding the prompt over
/// stdin and reading the reply from stdout. Authentication comes from the user's existing
/// Claude login (~/.claude/.credentials.json) — no API key.
///
/// The bridge's "sandbox" field maps onto Claude's permission model:
///   read-only        → --permission-mode dontAsk --tools Read,Grep
///   workspace-write  → --permission-mode acceptEdits  (may edit files / run tools)
///   read-only-web    → --permission-mode dontAsk --tools Read,Grep,WebFetch,WebSearch --allowedTools WebFetch,WebSearch
///                      (read files and web pages; never edit, never run a shell)
/// </summary>
public sealed class ClaudeRunner : IAgentRunner
{
    private static readonly TimeSpan SessionLimitFallbackCooldown = TimeSpan.FromMinutes(15);
    private readonly ClaudeRunnerOptions _opts;
    private readonly ILogger<ClaudeRunner> _log;
    // One pool per request key. Each pool caps concurrency (and therefore live session count) to
    // SessionPoolSize and hands out / takes back resumable sessions. ConcurrentDictionary so the
    // first call for a new key can create its pool without a global lock.
    private readonly ConcurrentDictionary<ClaudeSessionKey, SessionPool> _pools = new();
    // Serializes only the session-state FILE writes (not the claude processes), since check-ins now
    // happen concurrently across pool slots and must not interleave a half-written JSON file.
    private readonly object _persistGate = new();
    private readonly object _sessionLimitGate = new();
    private readonly int _poolSize;
    private DateTimeOffset? _sessionLimitUntilUtc;
    private string _sessionLimitKind = "session";

    public ClaudeRunner(ClaudeRunnerOptions opts, ILogger<ClaudeRunner> log)
    {
        _opts = opts;
        _log = log;
        _poolSize = Math.Max(1, _opts.SessionPoolSize);
        if (_opts.ReuseSession)
        {
            LoadSessionState();
            PruneInactiveSessionFiles();
            SaveSessionState();
        }
    }

    private static string PermissionModeFor(string sandbox) =>
        sandbox == "workspace-write" ? "acceptEdits" : "dontAsk";

    public async Task<CodexResult> RunAsync(
        string prompt, string? cwd, string? model, string sandbox, string? conversationId, CancellationToken ct)
    {
        if (TryGetActiveSessionLimit(out var limitedUntil, out var limitKind))
            return SessionLimitResult(limitedUntil, limitKind);

        var effectiveCwd = cwd ?? _opts.DefaultCwd;
        if (!_opts.ReuseSession || (_opts.RequireConversationIdForReuse && string.IsNullOrEmpty(conversationId)))
        {
            var freshRun = await RunProcessWithAuthRecoveryAsync(prompt, effectiveCwd, model, sandbox, resumeSessionId: null, ct);
            return HandleSessionLimitIfNeeded(freshRun) ?? freshRun.Result;
        }

        var effectivePrompt = _opts.IgnoreSessionContext ? WithSessionContextGuard(prompt) : prompt;
        var key = new ClaudeSessionKey(effectiveCwd, model ?? "", sandbox, conversationId ?? "");
        var pool = _pools.GetOrAdd(key, _ => new SessionPool(_poolSize));

        // One slot == permission to run one call AND to hold at most one live session. Acquiring a
        // slot bounds in-flight calls (and live sessions) to the pool size; we check a session out
        // of the idle stack, use it, and check it back in before releasing the slot.
        await pool.Slots.WaitAsync(ct);
        try
        {
            // A request may have queued before another call discovered the provider-side session
            // limit. Re-check after acquiring the slot so queued callers don't spawn new CLI
            // processes during the cooldown window.
            if (TryGetActiveSessionLimit(out limitedUntil, out limitKind))
                return SessionLimitResult(limitedUntil, limitKind);

            var session = pool.TryCheckout();

            // Drop a bloated session before resuming it; a fresh one will replace it in this slot.
            if (session is not null && ShouldRotateSession(session, out var rotateReason))
            {
                _log.LogInformation("rotating claude session {SessionId}: {Reason}", session.Id, rotateReason);
                session = null;
            }

            var run = await RunProcessWithAuthRecoveryAsync(effectivePrompt, effectiveCwd, model, sandbox, session?.Id, ct);
            var limitResult = HandleSessionLimitIfNeeded(run);
            if (limitResult is not null)
            {
                SaveSessionState();
                return limitResult;
            }

            if (!run.Result.Ok && session is not null && LooksLikeSessionRolloverFailure(run.FailureDetail ?? ""))
            {
                _log.LogInformation("claude resume failed for session {SessionId}; retrying with a fresh session", session.Id);
                session = null;
                run = await RunProcessWithAuthRecoveryAsync(effectivePrompt, effectiveCwd, model, sandbox, resumeSessionId: null, ct);
                limitResult = HandleSessionLimitIfNeeded(run);
                if (limitResult is not null)
                {
                    SaveSessionState();
                    return limitResult;
                }
            }

            if (run.Result.Ok)
            {
                var sessionId = run.SessionId ?? session?.Id;
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    // A fresh process reports a new id; a resumed one reports the same id. Either way
                    // it returns to the pool for the next call to reuse.
                    pool.CheckIn(new ClaudeSessionState(
                        sessionId,
                        FindSessionFile(sessionId, session?.FilePath),
                        run.InputTokens ?? session?.LastInputTokens,
                        DateTimeOffset.UtcNow));
                }
                else
                    _log.LogWarning("claude completed but did not report a session id; session reuse skipped for this turn");
            }
            // On failure we deliberately do NOT check the session back in: a killed/timed-out turn may
            // have left the JSONL half-written, so resuming it could corrupt the next call. The slot
            // frees, the session is dropped, and the next call recreates one — live count stays <= N.

            // Persist the settled pool every turn (idempotent for a plain reuse). Because the snapshot
            // is taken inside the write lock, the last concurrent writer records the full pool — so a
            // burst that mints N fresh sessions ends with all N persisted, not an early partial set.
            SaveSessionState();
            return run.Result;
        }
        finally
        {
            pool.Slots.Release();
        }
    }

    /// <summary>
    /// Runs the CLI once; on an AUTH failure (401 / revoked OAuth), reseeds the credentials from
    /// <see cref="ClaudeRunnerOptions.CredentialSeedPath"/> and retries exactly once. The retry
    /// happens ONLY when the seed content actually differs from the active credentials — retrying
    /// the same revoked token cannot succeed, so an equally-stale seed fails fast with no storm.
    /// </summary>
    private async Task<ClaudeProcessRun> RunProcessWithAuthRecoveryAsync(
        string prompt,
        string cwd,
        string? model,
        string sandbox,
        string? resumeSessionId,
        CancellationToken ct)
    {
        var run = await RunProcessAsync(prompt, cwd, model, sandbox, resumeSessionId, ct);
        if (run.Result.Ok)
            return run;
        if (LooksLikeClaudeAuthFailure(run.FailureDetail ?? "", run.FailureStdout ?? "")
            && TryReseedCredentials())
        {
            _log.LogWarning("claude auth failure; credentials reseeded from {Seed} — retrying once",
                _opts.CredentialSeedPath);
            run = await RunProcessAsync(prompt, cwd, model, sandbox, resumeSessionId, ct);
        }
        return ClassifyClaudeAccessFailure(run);
    }

    private static ClaudeProcessRun ClassifyClaudeAccessFailure(ClaudeProcessRun run)
    {
        var detail = run.FailureDetail ?? "";
        if (LooksLikeClaudeAuthFailure(detail, run.FailureStdout ?? ""))
            return run with
            {
                Result = new CodexResult(false, "", "claude_auth_failed",
                    "Claude authentication failed. Refresh the configured Claude credentials.")
            };
        if (LooksLikeClaudeAccessForbidden(detail))
            return run with
            {
                Result = new CodexResult(false, "", "claude_access_forbidden",
                    "Claude Code access is forbidden for the authenticated account or organization.")
            };
        return run;
    }

    /// <summary>True for the CLI's 401/authentication failure shapes — precise markers only, so an
    /// unrelated tool failure that merely mentions authentication can never trigger a credential
    /// overwrite. 429 session limits are NOT auth failures. Public for unit testing.</summary>
    public static bool LooksLikeClaudeAuthFailure(string detail) =>
        LooksLikeClaudeAuthFailure(detail, detail);

    private static bool LooksLikeClaudeAuthFailure(string detail, string resultJson)
    {
        if (string.IsNullOrWhiteSpace(detail)) return false;
        var d = detail.ToLowerInvariant();
        return IsExpiredOAuthResult(resultJson)
               || System.Text.RegularExpressions.Regex.IsMatch(d, "api_error_status\"\\s*:\\s*401")
               || d.Contains("oauth access token has been revoked", StringComparison.Ordinal)
               || d.Contains("invalid authentication credentials", StringComparison.Ordinal)
               || (d.Contains("failed to authenticate", StringComparison.Ordinal)
                   && d.Contains("401", StringComparison.Ordinal))
               || (d.Contains("invalid api key", StringComparison.Ordinal)
                   && d.Contains("/login", StringComparison.Ordinal))
               // live-verified CLI shape for unreadable/invalid credentials:
               // {"is_error":true,"api_error_status":null,"result":"Not logged in · Please run /login"}
               || (d.Contains("not logged in", StringComparison.Ordinal)
                   && d.Contains("/login", StringComparison.Ordinal));
    }

    // Observed with Claude Code 2.1.325 before an API request, with no HTTP status.
    // --output-format json supplies one stdout document. Keep it separate from stderr
    // diagnostics and accept only this error envelope, never quoted tool/model text.
    private static bool IsExpiredOAuthResult(string detail)
    {
        try
        {
            using var doc = JsonDocument.Parse(detail);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("type", out var type)
                   && type.ValueKind == JsonValueKind.String && type.GetString() == "result"
                   && root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True
                   && root.TryGetProperty("api_error_status", out var status) && status.ValueKind == JsonValueKind.Null
                   && root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String
                   && result.GetString() == "Failed to authenticate: OAuth session expired and could not be refreshed";
        }
        catch (JsonException) { return false; }
    }

    /// <summary>True for an upstream Claude authorization denial. This is distinct from bridge
    /// bearer authentication and from local cwd policy failures, both handled by the HTTP layer.</summary>
    public static bool LooksLikeClaudeAccessForbidden(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return false;
        var d = detail.ToLowerInvariant();
        return System.Text.RegularExpressions.Regex.IsMatch(d, "api_error_status\"\\s*:\\s*403")
               && (d.Contains("organization", StringComparison.Ordinal)
                   || d.Contains("subscription access", StringComparison.Ordinal)
                   || d.Contains("access for claude code", StringComparison.Ordinal)
                   || d.Contains("forbidden", StringComparison.Ordinal));
    }

    /// <summary>Concurrent 401s within this window after a reseed each get their single retry:
    /// their failing process started before the fresh credentials were published.</summary>
    private static readonly TimeSpan RecentReseedWindow = TimeSpan.FromSeconds(30);
    private readonly object _credentialGate = new();
    private DateTimeOffset? _lastReseedUtc;

    /// <summary>
    /// Publishes the seed credentials over $HOME/.claude/.credentials.json. Serialized by a lock;
    /// the write is a same-directory temp file (0600) atomically renamed over the target, so a
    /// concurrently spawning claude process can never observe a truncated file. Returns true when
    /// this caller published the change OR a change was published within <see cref="RecentReseedWindow"/>
    /// (its failing run predates the fresh credentials, so one retry is justified); false when no
    /// seed is configured, the seed is unreadable, or nothing newer is available.
    /// </summary>
    private bool TryReseedCredentials()
    {
        var seed = _opts.CredentialSeedPath;
        if (string.IsNullOrWhiteSpace(seed)) return false;
        var target = Path.Combine(GetClaudeHome(), ".claude", ".credentials.json");
        lock (_credentialGate)
        {
            try
            {
                if (!File.Exists(seed)) return false;
                var seedBytes = File.ReadAllBytes(seed);
                if (File.Exists(target) && seedBytes.AsSpan().SequenceEqual(File.ReadAllBytes(target)))
                    return _lastReseedUtc is { } at
                           && DateTimeOffset.UtcNow - at < RecentReseedWindow;
                var dir = Path.GetDirectoryName(target)!;
                Directory.CreateDirectory(dir);
                var temp = Path.Combine(dir, $".credentials.json.tmp-{Guid.NewGuid():N}");
                try
                {
                    // born 0600 — the secret must never exist world-readable, even between syscalls
                    var opts = new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        UnixCreateMode = OperatingSystem.IsWindows()
                            ? null : UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    };
                    using (var fs = new FileStream(temp, opts))
                        fs.Write(seedBytes);
                    File.Move(temp, target, overwrite: true);   // atomic rename on the same filesystem
                }
                catch
                {
                    try { File.Delete(temp); } catch { /* best effort */ }
                    throw;
                }
                _lastReseedUtc = DateTimeOffset.UtcNow;
                return true;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "could not reseed claude credentials from {Seed}", seed);
                return false;
            }
        }
    }

    private async Task<ClaudeProcessRun> RunProcessAsync(
        string prompt,
        string cwd,
        string? model,
        string sandbox,
        string? resumeSessionId,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _opts.ClaudeBinary,
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // argv only — user input is never interpolated into a shell string.
        psi.ArgumentList.Add("-p"); // print mode: run once, print result, exit
        // A shared headless reviewer must not inherit arbitrary project/user hooks, MCP servers or
        // CLAUDE files. They can block Stop forever or inject unrelated context. Safe mode keeps
        // auth, model selection, built-in tools and the explicit permission mode below.
        psi.ArgumentList.Add("--safe-mode");
        psi.ArgumentList.Add("--permission-mode");
        psi.ArgumentList.Add(PermissionModeFor(sandbox));
        if (sandbox == ChatRequestValidator.ReadOnlyWeb)
        {
            // The read-only allowlist plus the two web tools, pre-approved because dontAsk denies anything that would
            // otherwise ask. No Edit, Write or Bash: a web page read here cannot make the model change files or run
            // commands, whatever text it carries.
            psi.ArgumentList.Add("--tools");
            psi.ArgumentList.Add("Read,Grep,WebFetch,WebSearch");
            psi.ArgumentList.Add("--allowedTools");
            psi.ArgumentList.Add("WebFetch,WebSearch");
        }
        else if (sandbox != "workspace-write")
        {
            // dontAsk avoids Plan Mode's write-oriented plan workflow. The explicit allowlist is
            // the read-only boundary, including for the intentionally writable Claude overlay.
            psi.ArgumentList.Add("--tools");
            psi.ArgumentList.Add("Read,Grep");
        }
        // JSON (not text) so we learn the model the CLI ACTUALLY used: it appears only as the
        // key of the `modelUsage` object — there is no top-level `model` field. The answer text
        // is in `result`. We fall back to treating stdout as plain text if JSON parsing fails.
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
        if (!string.IsNullOrEmpty(resumeSessionId))
        {
            psi.ArgumentList.Add("--resume");
            psi.ArgumentList.Add(resumeSessionId);
        }
        else if (_opts.NoSessionPersistence)
        {
            psi.ArgumentList.Add("--no-session-persistence");
        }
        if (!string.IsNullOrEmpty(model))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(model);
        }
        // Prompt comes from stdin (avoids the per-arg length limit and any flag ambiguity).

        if (!string.IsNullOrEmpty(_opts.HomeDir))
            psi.Environment["HOME"] = _opts.HomeDir;

        ServiceSecurity.ConfigureProviderEnvironment(psi);
        using var proc = new Process { StartInfo = psi };

        try
        {
            if (!proc.Start())
                return new ClaudeProcessRun(new CodexResult(false, "", "spawn_failed", "Failed to start claude process."), null, null, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "claude spawn failed");
            return new ClaudeProcessRun(new CodexResult(false, "", "spawn_failed", "Failed to start claude process."), null, null, ex.Message);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_opts.Timeout);
        var capture = new ProviderOutput(_opts.MaxOutputBytes, timeoutCts);
        var stdoutTask = capture.ReadAsync(proc.StandardOutput.BaseStream);
        var stderrTask = capture.ReadAsync(proc.StandardError.BaseStream);
        string raw;
        string stderr;
        try
        {
            try
            {
                await proc.StandardInput.WriteAsync(prompt.AsMemory(), timeoutCts.Token);
                await proc.StandardInput.FlushAsync(timeoutCts.Token);
                proc.StandardInput.Close();
            }
            catch (IOException)
            {
                _log.LogWarning("claude stdin closed before the prompt was consumed");
            }
            await proc.WaitForExitAsync(timeoutCts.Token);
            raw = (await stdoutTask).Trim();
            stderr = await stderrTask;
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            if (!await ProviderProcess.ObserveOutputAsync(proc, stdoutTask, stderrTask))
                _log.LogWarning("claude output cleanup exceeded its deadline");
            return new ClaudeProcessRun(capture.CancelledResult(ct, "Claude", _opts.Timeout), null, null, null);
        }

        var sessionId = TryParseSessionId(raw);
        var inputTokens = TryParseInputTokens(raw);

        if (proc.ExitCode != 0 || IsErrorResult(raw))
        {
            var detail = (stderr + raw).Trim();
            _log.LogWarning("claude exited {Code}; provider output omitted", proc.ExitCode);
            // Keep raw output only for internal classification, never in logs or client errors.
            return new ClaudeProcessRun(
                new CodexResult(false, "", "claude_failed", $"Claude exited with code {proc.ExitCode}."),
                sessionId,
                inputTokens,
                detail,
                raw);
        }

        var (answer, servedModel) = ParseJsonResult(raw);
        return new ClaudeProcessRun(new CodexResult(true, answer, null, null, servedModel, Usage: NativeUsage.FromClaudeResult(raw, resumeSessionId is not null)), sessionId, inputTokens, null);
    }

    /// <summary>
    /// Parse `claude -p --output-format json` output. The answer is in <c>result</c>; the model is
    /// taken from <c>modelUsage</c> — there is no top-level model field. A single turn can list
    /// MULTIPLE models there: Claude Code runs a small background helper (haiku) alongside the main
    /// model that actually writes the answer, so the first key is NOT reliably the main one. We pick
    /// the main model = the entry with the largest total input (inputTokens + cacheReadInputTokens),
    /// since the main model alone receives the full system prompt + conversation context. Falls back
    /// to the raw text if the output isn't the expected JSON (e.g. a future CLI change).
    /// </summary>
    public static (string Answer, string? Model) ParseJsonResult(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw[0] != '{')
            return (raw.TrimEnd('\n', '\r'), null);

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            var answer = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() ?? ""
                : raw;

            var model = MainModelFromUsage(root);
            return (answer.TrimEnd('\n', '\r'), model);
        }
        catch (JsonException)
        {
            return (raw.TrimEnd('\n', '\r'), null);
        }
    }

    public static string? TryParseSessionId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart()[0] != '{') return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            return root.TryGetProperty("session_id", out var sessionId) && sessionId.ValueKind == JsonValueKind.String
                ? sessionId.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string WithSessionContextGuard(string prompt) =>
        """
        Bridge instruction:
        Treat the current request as independent. Do not use facts, entities, conclusions, code snippets, or details from earlier turns in this CLI session unless they are explicitly repeated in the current user prompt. Prior session history may only be used for runtime continuity; it is not task context.

        Current user prompt:
        """ + "\n" + prompt;

    public static long? TryParseInputTokens(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart()[0] != '{') return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                return null;

            long Read(string name) =>
                usage.TryGetProperty(name, out var e) &&
                e.ValueKind == JsonValueKind.Number &&
                e.TryGetInt64(out var n) ? n : 0;

            return Read("input_tokens") + Read("cache_creation_input_tokens") + Read("cache_read_input_tokens");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool LooksLikeSessionRolloverFailure(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return false;
        var d = detail.ToLowerInvariant();
        return d.Contains("context window", StringComparison.Ordinal)
               || d.Contains("context length", StringComparison.Ordinal)
               || d.Contains("maximum context", StringComparison.Ordinal)
               || d.Contains("too many tokens", StringComparison.Ordinal)
               || d.Contains("token limit", StringComparison.Ordinal)
               || d.Contains("conversation too large", StringComparison.Ordinal)
               || d.Contains("session too large", StringComparison.Ordinal)
               || d.Contains("no such session", StringComparison.Ordinal)
               || d.Contains("session not found", StringComparison.Ordinal)
               || d.Contains("could not find session", StringComparison.Ordinal)
               || d.Contains("failed to load session", StringComparison.Ordinal);
    }

    private static bool IsErrorResult(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    public static bool LooksLikeClaudeSessionLimit(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return false;
        var d = detail.ToLowerInvariant();
        return (System.Text.RegularExpressions.Regex.IsMatch(d, "api_error_status\"\\s*:\\s*429")
                || System.Text.RegularExpressions.Regex.IsMatch(d, "status\"\\s*:\\s*429")
                || d.Contains("http 429", StringComparison.Ordinal)
                || d.Contains("429 too many requests", StringComparison.Ordinal)
                || d.Contains("hit your", StringComparison.Ordinal))
               && (d.Contains("weekly limit", StringComparison.Ordinal)
                   || d.Contains("weekly usage limit", StringComparison.Ordinal)
                   || d.Contains("session limit", StringComparison.Ordinal)
                   || d.Contains("usage limit", StringComparison.Ordinal)
                   || d.Contains("rate limit", StringComparison.Ordinal));
    }

    public static DateTimeOffset? TryParseSessionLimitResetUtc(string detail, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(detail)) return null;

        var match = System.Text.RegularExpressions.Regex.Match(
            detail,
            @"resets(?:\s+at)?\s+(?:(?<month>[A-Za-z]{3,9})\s+(?<day>\d{1,2})(?:,?\s+(?<year>\d{4}))?,?\s+)?(?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<ampm>am|pm)?\s*(?:\(UTC\)|UTC)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups["hour"].Value, out var hour)) return null;
        var minute = match.Groups["minute"].Success ? int.Parse(match.Groups["minute"].Value) : 0;
        var ampm = match.Groups["ampm"].Value.ToLowerInvariant();
        if ((ampm != "" && hour is < 1 or > 12) || (ampm == "" && hour is < 0 or > 23) || minute > 59) return null;
        if (ampm == "pm" && hour < 12) hour += 12;
        if (ampm == "am" && hour == 12) hour = 0;
        nowUtc = nowUtc.ToUniversalTime();
        try
        {
            if (match.Groups["month"].Success)
            {
                var months = new[] { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };
                var fullMonths = new[] { "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december" };
                var monthText = match.Groups["month"].Value.ToLowerInvariant();
                var month = Array.IndexOf(months, monthText) + 1;
                if (month == 0) month = Array.IndexOf(fullMonths, monthText) + 1;
                if (month == 0) return null;
                var year = match.Groups["year"].Success ? int.Parse(match.Groups["year"].Value) : nowUtc.Year;
                var day = int.Parse(match.Groups["day"].Value);
                var reset = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);
                if (reset <= nowUtc && !match.Groups["year"].Success && nowUtc.Month == 12 && month == 1)
                    reset = reset.AddYears(1);
                // Never turn stale/invalid output into a year-long provider outage. Unknown dates
                // use the existing bounded probe cooldown instead of inventing a reset date.
                return reset > nowUtc && reset - nowUtc <= TimeSpan.FromDays(8) ? reset : null;
            }
            var daily = new DateTimeOffset(nowUtc.Year, nowUtc.Month, nowUtc.Day, hour, minute, 0, TimeSpan.Zero);
            return daily <= nowUtc ? daily.AddDays(1) : daily;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>
    /// Pick the main model from a <c>modelUsage</c> object. Claude Code may list a background helper
    /// (e.g. haiku) next to the model that wrote the answer; the main model is the one that received
    /// the full context, so it has the largest total input = inputTokens + cacheReadInputTokens.
    /// Returns null when modelUsage is absent/empty. Public for unit testing.
    /// </summary>
    public static string? MainModelFromUsage(JsonElement root)
    {
        if (!root.TryGetProperty("modelUsage", out var mu) || mu.ValueKind != JsonValueKind.Object)
            return null;

        string? best = null;
        long bestInput = -1;
        foreach (var entry in mu.EnumerateObject())
        {
            var v = entry.Value;
            long Read(string name) =>
                v.ValueKind == JsonValueKind.Object &&
                v.TryGetProperty(name, out var e) &&
                e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n) ? n : 0;

            var totalInput = Read("inputTokens") + Read("cacheReadInputTokens");
            if (best is null || totalInput > bestInput)
            {
                best = entry.Name;
                bestInput = totalInput;
            }
        }
        return best;
    }

    private void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (Exception ex) { _log.LogWarning(ex, "failed to kill claude process"); }
    }

    private CodexResult? HandleSessionLimitIfNeeded(ClaudeProcessRun run)
    {
        if (run.Result.Ok || !LooksLikeClaudeSessionLimit(run.FailureDetail ?? ""))
            return null;

        var now = DateTimeOffset.UtcNow;
        var until = TryParseSessionLimitResetUtc(run.FailureDetail ?? "", now)
                    ?? now.Add(SessionLimitFallbackCooldown);
        var kind = (run.FailureDetail ?? "").Contains("weekly", StringComparison.OrdinalIgnoreCase) ? "weekly" : "session";
        SetSessionLimitCooldown(until, kind);
        _log.LogWarning("claude session limit hit; suppressing new claude processes until {Until:u}", until);
        return SessionLimitResult(until, kind);
    }

    private bool TryGetActiveSessionLimit(out DateTimeOffset until, out string kind)
    {
        lock (_sessionLimitGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_sessionLimitUntilUtc is { } activeUntil && activeUntil > now)
            {
                until = activeUntil;
                kind = _sessionLimitKind;
                return true;
            }

            _sessionLimitUntilUtc = null;
            until = default;
            kind = "session";
            return false;
        }
    }

    private void SetSessionLimitCooldown(DateTimeOffset until, string kind)
    {
        lock (_sessionLimitGate)
        {
            if (_sessionLimitUntilUtc is null || until > _sessionLimitUntilUtc)
            {
                _sessionLimitUntilUtc = until;
                _sessionLimitKind = kind;
            }
        }
    }

    private static CodexResult SessionLimitResult(DateTimeOffset until, string kind) =>
        new(
            false,
            "",
            "rate_limited",
            $"Claude {kind} usage limit reached. Retry after {until:yyyy-MM-dd HH:mm:ss} UTC.",
            RetryAtUtc: until, LimitKind: kind);

    private bool ShouldRotateSession(ClaudeSessionState session, out string reason)
    {
        if (_opts.MaxSessionInputTokens > 0
            && session.LastInputTokens is { } inputTokens
            && inputTokens >= _opts.MaxSessionInputTokens)
        {
            reason = $"last input context reached {inputTokens} tokens (limit {_opts.MaxSessionInputTokens})";
            return true;
        }

        if (IsSessionFileTooLarge(session, out var bytes))
        {
            reason = $"session file reached {bytes} bytes (limit {_opts.MaxSessionBytes})";
            return true;
        }

        reason = "";
        return false;
    }

    private bool IsSessionFileTooLarge(ClaudeSessionState session, out long bytes)
    {
        bytes = 0;
        if (_opts.MaxSessionBytes <= 0) return false;
        var path = FindSessionFile(session.Id, session.FilePath);
        if (path is null) return false;
        try
        {
            bytes = new FileInfo(path).Length;
            return bytes >= _opts.MaxSessionBytes;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "could not stat claude session file {Path}", path);
            return false;
        }
    }

    private string? FindSessionFile(string sessionId, string? existingPath)
    {
        if (!string.IsNullOrEmpty(existingPath) && File.Exists(existingPath))
            return existingPath;

        var projectsDir = Path.Combine(GetClaudeHome(), ".claude", "projects");
        if (!Directory.Exists(projectsDir)) return null;

        try
        {
            return Directory
                .EnumerateFiles(projectsDir, "*.jsonl", SearchOption.AllDirectories)
                .Where(p => Path.GetFileName(p).Contains(sessionId, StringComparison.Ordinal))
                .OrderByDescending(p => File.GetLastWriteTimeUtc(p))
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "could not locate claude session file for {SessionId}", sessionId);
            return null;
        }
    }

    private string GetClaudeHome() =>
        string.IsNullOrEmpty(_opts.HomeDir)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : _opts.HomeDir;

    private string GetSessionStatePath() =>
        string.IsNullOrWhiteSpace(_opts.SessionStatePath)
            ? Path.Combine(GetClaudeHome(), ".claude", "claude-bridge-sessions.json")
            : _opts.SessionStatePath;

    private void LoadSessionState()
    {
        var path = GetSessionStatePath();
        if (!File.Exists(path)) return;

        try
        {
            var state = JsonSerializer.Deserialize<PersistedClaudeSessionStore>(File.ReadAllText(path));
            if (state?.Sessions is null) return;

            foreach (var item in state.Sessions)
            {
                if (string.IsNullOrWhiteSpace(item.Id)) continue;
                var key = new ClaudeSessionKey(item.Cwd ?? "", item.Model ?? "", item.Sandbox ?? "", item.ConversationId ?? "");
                if (string.IsNullOrWhiteSpace(key.Cwd) || string.IsNullOrWhiteSpace(key.Sandbox)) continue;
                var pool = _pools.GetOrAdd(key, _ => new SessionPool(_poolSize));
                // Don't load more idle sessions than the pool can hold (config may have shrunk K).
                if (pool.IdleCount >= _poolSize) continue;
                pool.CheckIn(new ClaudeSessionState(
                    item.Id,
                    File.Exists(item.FilePath) ? item.FilePath : FindSessionFile(item.Id, null),
                    item.LastInputTokens,
                    item.UpdatedAtUtc == default ? DateTimeOffset.UtcNow : item.UpdatedAtUtc));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "could not load claude bridge session state from {Path}", path);
        }
    }

    private void SaveSessionState()
    {
        var path = GetSessionStatePath();
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Concurrent check-ins call this. Snapshot the pools AND write inside the lock so the last
            // writer always captures the freshest idle set — snapshotting outside would let an early
            // (smaller) snapshot win the race and persist fewer sessions than the pool actually holds.
            // In-flight sessions aren't idle at this instant; they re-persist when they check back in.
            lock (_persistGate)
            {
                var state = new PersistedClaudeSessionStore(
                    Version: 1,
                    Sessions: _pools
                        .SelectMany(kvp => kvp.Value.SnapshotIdle()
                            .Select(s => new PersistedClaudeSession(
                                kvp.Key.Cwd,
                                kvp.Key.Model,
                                kvp.Key.Sandbox,
                                kvp.Key.ConversationId,
                                s.Id,
                                s.FilePath,
                                s.LastInputTokens,
                                s.UpdatedAtUtc)))
                        .OrderBy(s => s.Cwd, StringComparer.Ordinal)
                        .ThenBy(s => s.ConversationId, StringComparer.Ordinal)
                        .ThenBy(s => s.Id, StringComparer.Ordinal)
                        .ToList());

                File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "could not save claude bridge session state to {Path}", path);
        }
    }

    private void PruneInactiveSessionFiles()
    {
        if (_opts.PruneSessionDays <= 0 && _opts.MaxTotalSessionBytes <= 0) return;

        var projectsDir = Path.Combine(GetClaudeHome(), ".claude", "projects");
        if (!Directory.Exists(projectsDir)) return;

        var activeIds = _pools.Values.SelectMany(p => p.SnapshotIdle()).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

        List<FileInfo> files;
        try
        {
            files = Directory
                .EnumerateFiles(projectsDir, "*.jsonl", SearchOption.AllDirectories)
                .Select(p => new FileInfo(p))
                .Where(f => !activeIds.Any(id => f.Name.Contains(id, StringComparison.Ordinal)))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "could not enumerate claude session files for pruning");
            return;
        }

        if (_opts.PruneSessionDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-_opts.PruneSessionDays);
            foreach (var file in files.Where(f => f.LastWriteTimeUtc < cutoff).ToList())
            {
                TryDelete(file.FullName);
                files.Remove(file);
            }
        }

        if (_opts.MaxTotalSessionBytes > 0)
        {
            var total = files.Sum(f => SafeLength(f));
            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= _opts.MaxTotalSessionBytes) break;
                var len = SafeLength(file);
                TryDelete(file.FullName);
                total -= len;
            }
        }
    }

    private static long SafeLength(FileInfo file)
    {
        try { return file.Exists ? file.Length : 0; }
        catch { return 0; }
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _log.LogDebug(ex, "failed to delete claude session file {Path}", path); }
    }

    /// <summary>
    /// A bounded set of reusable sessions for one request key. <see cref="Slots"/> caps how many
    /// calls run at once (== how many sessions can be live at once); idle sessions wait in the stack
    /// between calls. Checkout/check-in are lock-free; a checked-out session is held by exactly one
    /// caller, so the live-session count never exceeds the slot count.
    /// </summary>
    private sealed class SessionPool
    {
        public SemaphoreSlim Slots { get; }
        private readonly ConcurrentStack<ClaudeSessionState> _idle = new();

        public SessionPool(int size) => Slots = new SemaphoreSlim(size, size);

        public ClaudeSessionState? TryCheckout() => _idle.TryPop(out var s) ? s : null;
        public void CheckIn(ClaudeSessionState session) => _idle.Push(session);
        public ClaudeSessionState[] SnapshotIdle() => _idle.ToArray();
        public int IdleCount => _idle.Count;
    }

    private readonly record struct ClaudeSessionKey(string Cwd, string Model, string Sandbox, string ConversationId);

    private sealed record ClaudeSessionState(string Id, string? FilePath, long? LastInputTokens, DateTimeOffset UpdatedAtUtc);

    private sealed record ClaudeProcessRun(CodexResult Result, string? SessionId, long? InputTokens,
        string? FailureDetail, string? FailureStdout = null);

    private sealed record PersistedClaudeSessionStore(int Version, List<PersistedClaudeSession> Sessions);

    private sealed record PersistedClaudeSession(
        string Cwd,
        string Model,
        string Sandbox,
        string ConversationId,
        string Id,
        string? FilePath,
        long? LastInputTokens,
        DateTimeOffset UpdatedAtUtc);
}
