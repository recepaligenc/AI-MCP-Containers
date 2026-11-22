namespace AiMcpContainers;

/// <summary>Incoming chat request. All fields except <see cref="Prompt"/> are optional.</summary>
public sealed record ChatRequest
{
    /// <summary>The instruction sent to the agent. Required, non-empty, length-bounded.</summary>
    public string? Prompt { get; init; }

    /// <summary>Which CLI to drive: "codex" (ChatGPT) or "claude". Defaults to server config.</summary>
    public string? Provider { get; init; }

    /// <summary>Working directory the agent runs in. Must be an existing dir inside an allowed root.</summary>
    public string? Cwd { get; init; }

    /// <summary>Model override (e.g. "gpt-5.5", "sonnet"). Optional; falls back to the CLI default.</summary>
    public string? Model { get; init; }

    /// <summary>Sandbox policy: "read-only" or "workspace-write". Defaults to server config.</summary>
    public string? Sandbox { get; init; }

    /// <summary>Optional stable key used to keep unrelated Codex conversations in separate sessions.</summary>
    public string? ConversationId { get; init; }
}

/// <summary>Successful chat response.</summary>
public sealed record ChatResponse
{
    public required string Answer { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required string Sandbox { get; init; }
    public required long DurationMs { get; init; }
    public ProviderUsage? Usage { get; init; }
}

/// <summary>Uniform error envelope shared by every route.</summary>
public sealed record ErrorResponse
{
    public required string Error { get; init; }
    public string? Code { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? RetryAtUtc { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? LimitKind { get; init; }

}

/// <summary>Validation outcome — either a sanitized request or an error envelope + status code.</summary>
public readonly record struct ValidationResult(
    bool Ok,
    int StatusCode,
    ErrorResponse? Error,
    string? Prompt,
    string? Cwd,
    string? Model,
    string Sandbox,
    string Provider,
    string? ConversationId);

public static class ChatRequestValidator
{
    public const int MaxPromptLength = 100_000;

    // Sandboxes a CLIENT may request over HTTP. Deliberately excludes
    // "danger-full-access": an untrusted request must never be able to make codex run
    // unsandboxed (see the model-flag injection guard below).
    public static readonly IReadOnlySet<string> AllowedSandboxes =
        new HashSet<string>(StringComparer.Ordinal) { "read-only", "workspace-write", "read-only-web" };

    // "read-only-web": read-only PLUS opening and searching web pages, nothing else (no edit, no shell). Claude only:
    // Codex has no equivalent mode.
    public const string ReadOnlyWeb = "read-only-web";

    // Sandboxes the OPERATOR may set as the server default (AI_MCP_SANDBOX).
    // Superset of AllowedSandboxes with "danger-full-access" — for deployments where
    // codex's own bwrap sandbox cannot run (e.g. inside an UNPRIVILEGED container that
    // can't create user namespaces: "bwrap: No permissions to create a new namespace")
    // and the CONTAINER + a read-only workspace mount ARE the external sandbox. This is
    // an operator-only knob; clients still can't request it (AllowedSandboxes above).
    public static readonly IReadOnlySet<string> AllowedDefaultSandboxes =
        new HashSet<string>(StringComparer.Ordinal) { "read-only", "workspace-write", "danger-full-access" };

    public static readonly IReadOnlySet<string> AllowedProviders =
        new HashSet<string>(StringComparer.Ordinal) { "codex", "claude" };

    // gpt-5.5, gpt-4o, o3, codex-mini-latest … letters/digits/dot/dash, bounded length.
    // MUST start with an alphanumeric so a value like "--dangerously-bypass-..." can never be
    // mistaken for a CLI flag at the codex argv boundary.
    private static readonly System.Text.RegularExpressions.Regex ModelPattern =
        new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex ConversationIdPattern =
        new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <param name="allowedRoots">Absolute, normalized roots that <paramref name="req"/>.Cwd must sit within.</param>
    /// <param name="defaultSandbox">Sandbox used when the request omits one.</param>
    /// <param name="dirExists">Injectable directory-existence probe (real FS in prod, fake in tests).</param>
    public static ValidationResult Validate(
        ChatRequest? req,
        IReadOnlyList<string> allowedRoots,
        string defaultSandbox,
        Func<string, bool> dirExists,
        string defaultProvider = "codex",
        IReadOnlyList<string>? allowedWriteRoots = null,
        string? defaultCwd = null,
        bool codexInnerSandboxAvailable = true,
        bool readOnlyProjects = false)
    {
        ErrorResponse Err(string msg, string code) => new() { Error = msg, Code = code };
        ValidationResult Bad(int status, string msg, string code) =>
            new(false, status, Err(msg, code), null, null, null, defaultSandbox, defaultProvider, null);

        if (req is null)
            return Bad(400, "Request body is required.", "missing_body");

        var prompt = req.Prompt;
        if (string.IsNullOrWhiteSpace(prompt))
            return Bad(400, "Field 'prompt' is required and must be non-empty.", "missing_prompt");

        if (prompt.Length > MaxPromptLength)
            return Bad(400, $"Field 'prompt' exceeds {MaxPromptLength} characters.", "prompt_too_long");

        // Provider: whitelist only.
        var provider = defaultProvider;
        if (req.Provider is { } p)
        {
            if (!AllowedProviders.Contains(p))
                return Bad(400, $"Field 'provider' must be one of: {string.Join(", ", AllowedProviders)}.", "bad_provider");
            provider = p;
        }

        // Sandbox: whitelist only.
        var sandbox = defaultSandbox;
        if (req.Sandbox is { } s)
        {
            if (!AllowedSandboxes.Contains(s))
                return Bad(400, $"Field 'sandbox' must be one of: {string.Join(", ", AllowedSandboxes)}.", "bad_sandbox");
            sandbox = s;
        }
        if (sandbox == ReadOnlyWeb && provider != "claude")
            return Bad(400, "Sandbox 'read-only-web' exists for provider 'claude' only.", "sandbox_provider_mismatch");

        if (readOnlyProjects && sandbox == "workspace-write")
            return Bad(403, "Project write access is disabled on this service.", "project_write_disabled");

        // read-only / workspace-write are enforced by codex's OWN sandbox. Where that sandbox
        // cannot start (see CodexSandboxProbe), codex would still answer — having read nothing —
        // so refuse here instead of returning a review that silently saw no files.
        if (provider == "codex" && !codexInnerSandboxAvailable && AllowedSandboxes.Contains(sandbox) && sandbox != ReadOnlyWeb)
        {
            var why = $"Sandbox '{sandbox}' cannot run for provider 'codex' on this bridge: codex's own sandbox cannot start here.";
            return Bad(400, AllowedSandboxes.Contains(defaultSandbox)
                ? $"{why} Neither can this bridge's default ('{defaultSandbox}'); the operator must change AI_MCP_SANDBOX."
                : $"{why} Omit 'sandbox' to use this bridge's default ('{defaultSandbox}').", "sandbox_unavailable");
        }

        // Model: charset + length guard (defends the shell-arg boundary against injection).
        string? model = null;
        if (req.Model is { } m)
        {
            if (!ModelPattern.IsMatch(m))
                return Bad(400, "Field 'model' contains invalid characters.", "bad_model");
            model = m;
        }

        string? conversationId = null;
        if (req.ConversationId is { Length: > 0 } conv)
        {
            if (!ConversationIdPattern.IsMatch(conv))
                return Bad(400, "Field 'conversationId' contains invalid characters.", "bad_conversation_id");
            conversationId = conv;
        }

        // Cwd: must resolve to an existing directory contained in an allowed root.
        string? cwd = null;
        if (req.Cwd is { } rawCwd && rawCwd.Length > 0)
        {
            string full;
            try { full = ServiceSecurity.ResolveDirectory(rawCwd); }
            catch { return Bad(400, "Field 'cwd' is not a valid path.", "bad_cwd"); }

            if (!IsWithinAllowedRoot(full, allowedRoots))
                return Bad(403, "Field 'cwd' is outside the allowed roots.", "cwd_forbidden");

            if (!dirExists(full))
                return Bad(400, "Field 'cwd' directory does not exist.", "cwd_not_found");

            cwd = full;
        }

        if (sandbox == "workspace-write")
        {
            var effectiveCwd = cwd ?? defaultCwd;
            if (string.IsNullOrWhiteSpace(effectiveCwd)
                || allowedWriteRoots is null
                || !IsWithinAllowedRoot(effectiveCwd, allowedWriteRoots))
                return Bad(403, "Field 'cwd' is outside the configured writable roots.", "cwd_not_writable");
        }

        return new ValidationResult(true, 200, null, prompt, cwd, model, sandbox, provider, conversationId);
    }

    /// <summary>True when <paramref name="candidate"/> equals or is nested under any allowed root.</summary>
    public static bool IsWithinAllowedRoot(string candidate, IReadOnlyList<string> allowedRoots)
    {
        try
        {
            var norm = Normalize(candidate);
            foreach (var root in allowedRoots)
            {
                var r = Normalize(root);
                if (norm == r) return true;
                if (norm.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        return false;
    }

    private static string Normalize(string p)
    {
        var full = ServiceSecurity.ResolveDirectory(p);
        // Strip a trailing separator so "/a/b/" and "/a/b" compare equal.
        if (full.Length > 1 && full[^1] == Path.DirectorySeparatorChar)
            full = full[..^1];
        return full;
    }
}
