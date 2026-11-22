using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AiMcpContainers;

/// <param name="Model">The model the backend actually used, when the CLI reports it (null = unknown).</param>
public sealed record CodexResult(bool Ok, string Output, string? ErrorCode, string? ErrorMessage, string? Model = null, DateTimeOffset? RetryAtUtc = null, string? LimitKind = null, ProviderUsage? Usage = null);

/// <summary>One agent backend (codex or claude). Same contract regardless of CLI.</summary>
public interface IAgentRunner
{
    Task<CodexResult> RunAsync(
        string prompt,
        string? cwd,
        string? model,
        string sandbox,
        string? conversationId,
        CancellationToken ct);
}

/// <summary>Resolves a provider name ("codex"/"claude") to its runner.</summary>
public interface IAgentRunnerRegistry
{
    /// <summary>The runner for <paramref name="provider"/>, or null if none is configured.</summary>
    IAgentRunner? Get(string provider);
}

public sealed class AgentRunnerRegistry : IAgentRunnerRegistry
{
    private readonly IReadOnlyDictionary<string, IAgentRunner> _runners;

    public AgentRunnerRegistry(IReadOnlyDictionary<string, IAgentRunner> runners) => _runners = runners;

    public IAgentRunner? Get(string provider) =>
        _runners.TryGetValue(provider, out var r) ? r : null;
}

public sealed class CodexRunnerOptions
{
    /// <summary>Absolute path to the codex binary.</summary>
    public required string CodexBinary { get; init; }

    /// <summary>CODEX_HOME (holds auth.json + config.toml). Inherited from the user if null.</summary>
    public string? CodexHome { get; init; }

    /// <summary>Per-request wall-clock timeout.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Working directory used when a request omits one.</summary>
    public required string DefaultCwd { get; init; }

    /// <summary>When enabled, continue the previous Codex thread for matching cwd/model/sandbox.</summary>
    public bool ReuseSession { get; init; }

    /// <summary>Require an explicit conversation ID before reusing any prior request state.</summary>
    public bool RequireConversationIdForReuse { get; init; }

    /// <summary>
    /// How many concurrent threads to keep per request key when <see cref="ReuseSession"/> is on.
    /// A single Codex thread can't take two turns at once (concurrent resume reorders the saved
    /// JSONL), so reuse with a pool of 1 serializes a burst until the client times out. A pool of N
    /// keeps up to N live threads and runs up to N calls in parallel, each reusing one pooled
    /// thread: it bounds the live-thread count to N (staying under a provider session cap) while
    /// restoring N-way concurrency. Clamped to at least 1; 1 == legacy single-thread behavior.
    /// </summary>
    public int SessionPoolSize { get; init; } = 1;

    /// <summary>When running fresh turns, ask Codex not to persist a session file. Ignored for resumed sessions.</summary>
    public bool Ephemeral { get; init; }

    /// <summary>Prepend a guard telling the model not to use earlier resumed-session content as task context.</summary>
    public bool IgnoreSessionContext { get; init; }

    /// <summary>Rotate to a fresh thread when the persisted session file reaches this size. 0 disables size-based rotation.</summary>
    public long MaxSessionBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>Rotate before resuming when the previous turn's input context reaches this size. 0 disables token-based rotation.</summary>
    public long MaxSessionInputTokens { get; init; }

    /// <summary>Delete inactive Codex session files older than this many days. 0 disables age-based pruning.</summary>
    public int PruneSessionDays { get; init; }

    /// <summary>Keep total inactive Codex session files under this byte budget. 0 disables size-based pruning.</summary>
    public long MaxTotalSessionBytes { get; init; }

    /// <summary>Optional path for persisted bridge session routing state. Null = $CODEX_HOME/codex-bridge-sessions.json.</summary>
    public string? SessionStatePath { get; init; }
}

/// <summary>
/// Shells out to `codex exec`, feeding the prompt over stdin and reading the agent's final
/// message from a temp file (`--output-last-message`) so the reply is clean of banner/log noise.
/// Authentication comes from the user's existing Codex login (CODEX_HOME/auth.json) — no API key.
/// </summary>
public sealed class CodexRunner : IAgentRunner
{
    private static readonly TimeSpan UsageLimitFallbackCooldown = TimeSpan.FromMinutes(15);
    private readonly CodexRunnerOptions _opts;
    private readonly ILogger<CodexRunner> _log;
    // One pool per request key, each capping concurrency and live-thread count to the pool size.
    private readonly ConcurrentDictionary<CodexSessionKey, SessionPool> _pools = new();
    // Serializes only the session-state FILE writes, since check-ins now run concurrently.
    private readonly object _persistGate = new();
    private readonly object _usageLimitGate = new();
    private readonly int _poolSize;
    private DateTimeOffset? _usageLimitUntilUtc;

    // The model codex will actually use, resolved once from config.toml. `codex exec --json` does
    // NOT report the model in its event stream (unlike claude's modelUsage), and this endpoint
    // never passes a --model override, so the top-level `model` in $CODEX_HOME/config.toml is the
    // authoritative source. Resolved at construction (config rarely changes without a restart).
    private readonly string? _configModel;

    public CodexRunner(CodexRunnerOptions opts, ILogger<CodexRunner> log)
    {
        _opts = opts;
        _log = log;
        _configModel = ReadConfigModel(opts.CodexHome, log);
        _poolSize = Math.Max(1, _opts.SessionPoolSize);
        if (_opts.ReuseSession)
        {
            LoadSessionState();
            PruneInactiveSessionFiles();
            SaveSessionState();
        }
    }

    public static bool HasModelCapacityError(string stdout)
    {
        foreach (var line in stdout.Split('\n'))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type)
                    || type.ValueKind != JsonValueKind.String) continue;
                string? message = null;
                if (type.GetString() == "error" && root.TryGetProperty("message", out var direct)
                    && direct.ValueKind == JsonValueKind.String) message = direct.GetString();
                if (type.GetString() == "turn.failed" && root.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var nested)
                    && nested.ValueKind == JsonValueKind.String) message = nested.GetString();
                if (message?.StartsWith("Selected model is at capacity.", StringComparison.OrdinalIgnoreCase) == true)
                    return true;
            }
            catch (JsonException) { }
        }
        return false;
    }

    private static string? ReadConfigModel(string? codexHome, ILogger log)
    {
        // Mirror codex's own resolution: $CODEX_HOME/config.toml, else ~/.codex/config.toml.
        var home = string.IsNullOrEmpty(codexHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : codexHome;
        var path = Path.Combine(home, "config.toml");
        try
        {
            return File.Exists(path) ? ParseConfigModel(File.ReadAllText(path)) : null;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "could not read codex config model from {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Extract the top-level <c>model = "..."</c> from config.toml — the value before any
    /// <c>[section]</c> header (a <c>model</c> key inside a section is unrelated). Returns null
    /// when absent. Tolerant of quotes, comments, and surrounding whitespace.
    /// </summary>
    public static string? ParseConfigModel(string tomlText)
    {
        foreach (var rawLine in tomlText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            // Stop at the first table header — top-level keys only appear above it.
            if (line[0] == '[') break;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            if (!string.Equals(key, "model", StringComparison.Ordinal)) continue;

            var value = line[(eq + 1)..].Trim();
            // Strip an inline comment outside quotes is overkill here; TOML model values are simple.
            value = value.Trim('"', '\'').Trim();
            return value.Length == 0 ? null : value;
        }
        return null;
    }

    public async Task<CodexResult> RunAsync(
        string prompt, string? cwd, string? model, string sandbox, string? conversationId, CancellationToken ct)
    {
        if (TryGetActiveUsageLimit(out var limitedUntil))
            return UsageLimitResult(limitedUntil);

        // Resolve the default before selecting a reusable session: a config model change
        // must not resume a thread created under the old implicit default.
        model ??= _configModel;
        var effectiveCwd = cwd ?? _opts.DefaultCwd;
        if (!_opts.ReuseSession || (_opts.RequireConversationIdForReuse && string.IsNullOrEmpty(conversationId)))
        {
            var freshRun = await RunProcessAsync(prompt, effectiveCwd, model, sandbox, resumeSessionId: null, captureSession: false, ct);
            return HandleUsageLimitIfNeeded(freshRun) ?? freshRun.Result;
        }

        var effectivePrompt = _opts.IgnoreSessionContext ? WithSessionContextGuard(prompt) : prompt;
        var key = new CodexSessionKey(effectiveCwd, model ?? "", sandbox, conversationId ?? "");
        var pool = _pools.GetOrAdd(key, _ => new SessionPool(_poolSize));

        // One slot == permission to run one call AND to hold at most one live thread. A single Codex
        // thread can't take concurrent turns, so we check a thread out of the pool, use it, and check
        // it back in before releasing the slot — bounding both concurrency and live threads to N.
        await pool.Slots.WaitAsync(ct);
        var session = pool.TryCheckout();
        try
        {
            if (session is not null && ShouldRotateSession(session, out var rotateReason))
            {
                _log.LogInformation(
                    "rotating codex session {SessionId}: {Reason}",
                    session.Id, rotateReason);
                session = null;
            }

            var run = await RunProcessAsync(effectivePrompt, effectiveCwd, model, sandbox, session?.Id, captureSession: true, ct);
            var limitResult = HandleUsageLimitIfNeeded(run);
            if (limitResult is not null)
            {
                SaveSessionState();
                return limitResult;
            }

            if (!run.Result.Ok && session is not null && LooksLikeSessionRolloverFailure(run.FailureDetail ?? ""))
            {
                _log.LogInformation("codex resume failed for session {SessionId}; retrying with a fresh session", session.Id);
                session = null;
                run = await RunProcessAsync(effectivePrompt, effectiveCwd, model, sandbox, resumeSessionId: null, captureSession: true, ct);
                limitResult = HandleUsageLimitIfNeeded(run);
                if (limitResult is not null)
                {
                    SaveSessionState();
                    return limitResult;
                }
            }

            if (run.Result.Ok)
            {
                var sessionId = run.ThreadId ?? session?.Id;
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    pool.CheckIn(new CodexSessionState(
                        sessionId,
                        FindSessionFile(sessionId, session?.FilePath),
                        run.InputTokens ?? session?.LastInputTokens,
                        DateTimeOffset.UtcNow));
                }
                else
                    _log.LogWarning("codex completed but did not report a thread id; session reuse skipped for this turn");
            }
            // A failed turn's thread is dropped (not checked back in): a killed run may have left the
            // JSONL inconsistent, so resuming it could reorder history. The next call recreates one.

            // Persist the settled pool every turn; the snapshot taken inside the write lock means the
            // last concurrent writer records the full pool (all N threads after a fresh burst).
            SaveSessionState();
            return run.Result;
        }
        finally
        {
            pool.Slots.Release();
        }
    }

    private async Task<ProcessRun> RunProcessAsync(
        string prompt,
        string cwd,
        string? model,
        string sandbox,
        string? resumeSessionId,
        bool captureSession,
        CancellationToken ct)
    {
        var lastMsgFile = Path.Combine(Path.GetTempPath(), $"codex-bridge-{Guid.NewGuid():N}.txt");

        var psi = new ProcessStartInfo
        {
            FileName = _opts.CodexBinary,
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Build argv explicitly — never interpolate user input into a shell string.
        psi.ArgumentList.Add("exec");
        if (!string.IsNullOrEmpty(resumeSessionId))
        {
            psi.ArgumentList.Add("resume");
            psi.ArgumentList.Add("--skip-git-repo-check");
        }
        else
        {
            psi.ArgumentList.Add("--skip-git-repo-check");
            psi.ArgumentList.Add("--sandbox");
            psi.ArgumentList.Add(sandbox);
        }
        // The host's shared config can contain our client registrations. Prevent
        // provider calls from invoking these services again through inherited MCP.
        // A disabled entry still needs a valid transport when no registration exists.
        foreach (var server in new[] { "ai_mcp_codex", "ai_mcp_claude" })
        {
            psi.ArgumentList.Add("--config");
            psi.ArgumentList.Add($"mcp_servers.{server}={{command=\"/bin/false\",enabled=false}}");
        }
        if (!string.IsNullOrEmpty(model))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(model);
        }
        psi.ArgumentList.Add("--json");
        if (_opts.Ephemeral && string.IsNullOrEmpty(resumeSessionId))
            psi.ArgumentList.Add("--ephemeral");
        psi.ArgumentList.Add("--output-last-message");
        psi.ArgumentList.Add(lastMsgFile);
        if (string.IsNullOrEmpty(resumeSessionId))
        {
            psi.ArgumentList.Add("--color");
            psi.ArgumentList.Add("never");
        }
        if (!string.IsNullOrEmpty(resumeSessionId))
            psi.ArgumentList.Add(resumeSessionId);
        psi.ArgumentList.Add("-"); // read prompt from stdin

        if (!string.IsNullOrEmpty(_opts.CodexHome))
            psi.Environment["CODEX_HOME"] = _opts.CodexHome;

        ServiceSecurity.ConfigureProviderEnvironment(psi);
        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start())
                return new ProcessRun(new CodexResult(false, "", "spawn_failed", "Failed to start codex process."), null, null, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "codex spawn failed");
            return new ProcessRun(new CodexResult(false, "", "spawn_failed", "Failed to start codex process."), null, null, ex.Message);
        }

        // The deadline covers stdin, process exit and both output pipes.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_opts.Timeout);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
        string stdout;
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
                // A CLI may exit before consuming stdin; classify its exit below.
                _log.LogWarning("codex stdin closed before the prompt was consumed");
            }
            await proc.WaitForExitAsync(timeoutCts.Token);
            stdout = await stdoutTask;
            stderr = await stderrTask;
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            if (!await ProviderProcess.ObserveOutputAsync(proc, stdoutTask, stderrTask))
                _log.LogWarning("codex output cleanup exceeded its deadline");
            TryDelete(lastMsgFile);
            return !ct.IsCancellationRequested
                ? new ProcessRun(new CodexResult(false, "", "timeout", $"Codex timed out after {_opts.Timeout.TotalSeconds:F0}s."), null, null, null)
                : new ProcessRun(new CodexResult(false, "", "cancelled", "Request was cancelled."), null, null, null);
        }

        var threadId = captureSession ? TryParseThreadIdFromJsonEvents(stdout) : null;
        var nativeUsage = NativeUsage.ParseCodexEvents(stdout, resumeSessionId is not null);
        var inputTokens = captureSession ? nativeUsage.LastInputTokens : null;

        if (proc.ExitCode != 0)
        {
            var detail = (stderr + stdout).Trim();
            _log.LogWarning("codex exited {Code}; provider output omitted", proc.ExitCode);
            TryDelete(lastMsgFile);
            // Classify structured backend errors without exposing raw stderr or prompt text.
            var capacity = HasModelCapacityError(stdout);
            return new ProcessRun(
                new CodexResult(false, "", capacity ? "model_at_capacity" : "codex_failed",
                    capacity ? "The selected Codex model is at capacity. Try again later or select a different model."
                             : $"Codex exited with code {proc.ExitCode}."),
                threadId,
                inputTokens,
                detail);
        }

        string answer;
        try
        {
            answer = File.Exists(lastMsgFile)
                ? (await File.ReadAllTextAsync(lastMsgFile, ct)).TrimEnd('\n', '\r')
                : "";
        }
        finally
        {
            TryDelete(lastMsgFile);
        }

        // Report the effective model: an explicit request override wins, else config.toml's model.
        var servedModel = !string.IsNullOrEmpty(model) ? model : _configModel;
        return new ProcessRun(new CodexResult(true, answer, null, null, servedModel, Usage: nativeUsage.Usage), threadId, inputTokens, null);
    }

    public static string WithSessionContextGuard(string prompt) =>
        """
        Bridge instruction:
        Treat the current request as independent. Do not use facts, entities, conclusions, code snippets, or details from earlier turns in this CLI session unless they are explicitly repeated in the current user prompt. Prior session history may only be used for runtime continuity; it is not task context.

        Current user prompt:
        """ + "\n" + prompt;

    public static string? TryParseThreadIdFromJsonEvents(string stdout)
    {
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] != '{') continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var type)
                    && type.GetString() == "thread.started"
                    && root.TryGetProperty("thread_id", out var threadId))
                {
                    var value = threadId.GetString();
                    return string.IsNullOrWhiteSpace(value) ? null : value;
                }
            }
            catch (JsonException)
            {
                // Codex may print non-event noise around JSONL diagnostics; ignore it.
            }
        }
        return null;
    }

    public static long? TryParseLastInputTokensFromJsonEvents(string stdout) =>
        NativeUsage.ParseCodexEvents(stdout).LastInputTokens;

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

    public static bool LooksLikeCodexUsageLimit(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return false;
        var d = detail.ToLowerInvariant();
        return d.Contains("api_error_status\":429", StringComparison.Ordinal)
               || d.Contains("status\":429", StringComparison.Ordinal)
               || d.Contains("http 429", StringComparison.Ordinal)
               || d.Contains("429 too many requests", StringComparison.Ordinal)
               || d.Contains("session limit", StringComparison.Ordinal)
               || d.Contains("usage limit", StringComparison.Ordinal)
               || d.Contains("rate limit", StringComparison.Ordinal)
               || d.Contains("rate_limit", StringComparison.Ordinal)
               || d.Contains("quota exceeded", StringComparison.Ordinal)
               || (d.Contains("hit your", StringComparison.Ordinal) && d.Contains("limit", StringComparison.Ordinal));
    }

    public static DateTimeOffset? TryParseUsageLimitResetUtc(string detail, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(detail)) return null;

        var match = System.Text.RegularExpressions.Regex.Match(
            detail,
            @"resets(?:\s+at)?\s+(?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<ampm>am|pm)?\s*(?:\(UTC\)|UTC)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        if (!int.TryParse(match.Groups["hour"].Value, out var hour)) return null;
        var minute = 0;
        if (match.Groups["minute"].Success && !int.TryParse(match.Groups["minute"].Value, out minute))
            return null;

        var ampm = match.Groups["ampm"].Success ? match.Groups["ampm"].Value.ToLowerInvariant() : "";
        if (ampm == "pm" && hour < 12) hour += 12;
        if (ampm == "am" && hour == 12) hour = 0;
        if (hour is < 0 or > 23 || minute is < 0 or > 59) return null;

        var reset = new DateTimeOffset(nowUtc.Year, nowUtc.Month, nowUtc.Day, hour, minute, 0, TimeSpan.Zero);
        if (reset <= nowUtc)
            reset = reset.AddDays(1);
        return reset;
    }

    private CodexResult? HandleUsageLimitIfNeeded(ProcessRun run)
    {
        if (run.Result.Ok || !LooksLikeCodexUsageLimit(run.FailureDetail ?? ""))
            return null;

        var now = DateTimeOffset.UtcNow;
        var until = TryParseUsageLimitResetUtc(run.FailureDetail ?? "", now)
                    ?? now.Add(UsageLimitFallbackCooldown);
        SetUsageLimitCooldown(until);
        _log.LogWarning("codex usage limit hit; suppressing new codex processes until {Until:u}", until);
        return UsageLimitResult(until);
    }

    private bool TryGetActiveUsageLimit(out DateTimeOffset until)
    {
        lock (_usageLimitGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_usageLimitUntilUtc is { } activeUntil && activeUntil > now)
            {
                until = activeUntil;
                return true;
            }

            _usageLimitUntilUtc = null;
            until = default;
            return false;
        }
    }

    private void SetUsageLimitCooldown(DateTimeOffset until)
    {
        lock (_usageLimitGate)
        {
            if (_usageLimitUntilUtc is null || until > _usageLimitUntilUtc)
                _usageLimitUntilUtc = until;
        }
    }

    private static CodexResult UsageLimitResult(DateTimeOffset until) =>
        new(
            false,
            "",
            "rate_limited",
            $"Codex usage limit reached. Retry after {until:yyyy-MM-dd HH:mm:ss} UTC.");

    private bool ShouldRotateSession(CodexSessionState session, out string reason)
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

    private bool IsSessionFileTooLarge(CodexSessionState session, out long bytes)
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
            _log.LogDebug(ex, "could not stat codex session file {Path}", path);
            return false;
        }
    }

    private string? FindSessionFile(string sessionId, string? existingPath)
    {
        if (!string.IsNullOrEmpty(existingPath) && File.Exists(existingPath))
            return existingPath;

        var sessionsDir = Path.Combine(GetCodexHome(), "sessions");
        if (!Directory.Exists(sessionsDir)) return null;

        try
        {
            return Directory
                .EnumerateFiles(sessionsDir, "*.jsonl", SearchOption.AllDirectories)
                .Where(p => Path.GetFileName(p).Contains(sessionId, StringComparison.Ordinal))
                .OrderByDescending(p => File.GetLastWriteTimeUtc(p))
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "could not locate codex session file for {SessionId}", sessionId);
            return null;
        }
    }

    private string GetCodexHome() =>
        string.IsNullOrEmpty(_opts.CodexHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : _opts.CodexHome;

    private string GetSessionStatePath() =>
        string.IsNullOrWhiteSpace(_opts.SessionStatePath)
            ? Path.Combine(GetCodexHome(), "codex-bridge-sessions.json")
            : _opts.SessionStatePath;

    private void LoadSessionState()
    {
        var path = GetSessionStatePath();
        if (!File.Exists(path)) return;

        try
        {
            var state = JsonSerializer.Deserialize<PersistedCodexSessionStore>(File.ReadAllText(path));
            if (state?.Sessions is null) return;

            foreach (var item in state.Sessions)
            {
                if (string.IsNullOrWhiteSpace(item.Id)) continue;
                var key = new CodexSessionKey(item.Cwd ?? "", item.Model ?? "", item.Sandbox ?? "", item.ConversationId ?? "");
                if (string.IsNullOrWhiteSpace(key.Cwd) || string.IsNullOrWhiteSpace(key.Sandbox)) continue;
                var pool = _pools.GetOrAdd(key, _ => new SessionPool(_poolSize));
                if (pool.IdleCount >= _poolSize) continue;
                pool.CheckIn(new CodexSessionState(
                    item.Id,
                    File.Exists(item.FilePath) ? item.FilePath : FindSessionFile(item.Id, null),
                    item.LastInputTokens,
                    item.UpdatedAtUtc == default ? DateTimeOffset.UtcNow : item.UpdatedAtUtc));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "could not load codex bridge session state from {Path}", path);
        }
    }

    private void SaveSessionState()
    {
        var path = GetSessionStatePath();
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Snapshot + write inside the lock so the last concurrent writer captures the freshest
            // idle set (snapshotting outside lets a smaller early snapshot win the race).
            lock (_persistGate)
            {
                var state = new PersistedCodexSessionStore(
                    Version: 1,
                    Sessions: _pools
                        .SelectMany(kvp => kvp.Value.SnapshotIdle()
                            .Select(s => new PersistedCodexSession(
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
            _log.LogWarning(ex, "could not save codex bridge session state to {Path}", path);
        }
    }

    private void PruneInactiveSessionFiles()
    {
        if (_opts.PruneSessionDays <= 0 && _opts.MaxTotalSessionBytes <= 0) return;

        var sessionsDir = Path.Combine(GetCodexHome(), "sessions");
        if (!Directory.Exists(sessionsDir)) return;

        var activeIds = _pools.Values.SelectMany(p => p.SnapshotIdle()).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

        List<FileInfo> files;
        try
        {
            files = Directory
                .EnumerateFiles(sessionsDir, "*.jsonl", SearchOption.AllDirectories)
                .Select(p => new FileInfo(p))
                .Where(f => !activeIds.Any(id => f.Name.Contains(id, StringComparison.Ordinal)))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "could not enumerate codex session files for pruning");
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

    private void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (Exception ex) { _log.LogWarning(ex, "failed to kill codex process"); }
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _log.LogDebug(ex, "failed to delete temp file {Path}", path); }
    }

    /// <summary>
    /// A bounded set of reusable threads for one request key. <see cref="Slots"/> caps how many calls
    /// run at once (== how many threads can be live at once); idle threads wait in the stack between
    /// calls. Checkout/check-in are lock-free; a checked-out thread is held by exactly one caller.
    /// </summary>
    private sealed class SessionPool
    {
        public SemaphoreSlim Slots { get; }
        private readonly ConcurrentStack<CodexSessionState> _idle = new();

        public SessionPool(int size) => Slots = new SemaphoreSlim(size, size);

        public CodexSessionState? TryCheckout() => _idle.TryPop(out var s) ? s : null;
        public void CheckIn(CodexSessionState session) => _idle.Push(session);
        public CodexSessionState[] SnapshotIdle() => _idle.ToArray();
        public int IdleCount => _idle.Count;
    }

    private readonly record struct CodexSessionKey(string Cwd, string Model, string Sandbox, string ConversationId);

    private sealed record CodexSessionState(string Id, string? FilePath, long? LastInputTokens, DateTimeOffset UpdatedAtUtc);

    private sealed record ProcessRun(CodexResult Result, string? ThreadId, long? InputTokens, string? FailureDetail);

    private sealed record PersistedCodexSessionStore(int Version, List<PersistedCodexSession> Sessions);

    private sealed record PersistedCodexSession(
        string Cwd,
        string Model,
        string Sandbox,
        string ConversationId,
        string Id,
        string? FilePath,
        long? LastInputTokens,
        DateTimeOffset UpdatedAtUtc);
}
