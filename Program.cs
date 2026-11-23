using System.Text.Json;
using AiMcpContainers;

var builder = WebApplication.CreateBuilder(args);

// ---- Configuration (env vars override appsettings) ----
// AI_MCP_CODEX_BIN       absolute path to the codex binary (required if not in appsettings)
// AI_MCP_CODEX_HOME      CODEX_HOME for auth/config (default: inherit user's)
// AI_MCP_TOKEN     required private bearer token, at least 32 characters
// AI_MCP_ALLOWED_ROOTS     ';'-separated absolute roots that 'cwd' may live under
// AI_MCP_DEFAULT_CWD   working dir used when a request omits 'cwd'
// AI_MCP_SANDBOX   default sandbox: read-only | workspace-write
// AI_MCP_CODEX_INNER_SANDBOX  auto (default: at startup run `codex sandbox -c sandbox_mode="<mode>" -- /bin/true`
//                        for read-only and workspace-write) | available | unavailable —
//                        whether codex's own read-only/workspace-write sandbox can start here
// AI_MCP_TIMEOUT_SECONDS  per-request timeout (default 300)
// AI_MCP_MAX_PROMPT_CHARS  maximum UTF-16 prompt length (default 1000000; maximum 8000000)
// AI_MCP_CODEX_REUSE_SESSION  true to resume the previous Codex session for matching cwd/model/sandbox
// AI_MCP_CODEX_EPHEMERAL  true to avoid persisting fresh Codex session files when not resuming
// AI_MCP_CODEX_IGNORE_SESSION_CONTEXT  prepend an instruction to ignore earlier resumed-session content
// AI_MCP_CODEX_MAX_SESSION_BYTES  rotate a reused Codex session once its JSONL file reaches this size
// AI_MCP_CODEX_MAX_SESSION_INPUT_TOKENS  rotate when previous turn input context reaches this token count
// AI_MCP_CODEX_PRUNE_SESSION_DAYS  delete inactive Codex session files older than this many days
// AI_MCP_CODEX_MAX_TOTAL_SESSION_BYTES  keep inactive Codex session files under this byte budget
// AI_MCP_CLAUDE_REUSE_SESSION  true to resume the previous Claude session for matching cwd/model/sandbox
// AI_MCP_CLAUDE_NO_SESSION_PERSISTENCE  true to avoid persisting fresh Claude sessions when not resuming
// AI_MCP_CLAUDE_IGNORE_SESSION_CONTEXT  prepend an instruction to ignore earlier resumed-session content
// AI_MCP_CLAUDE_MAX_SESSION_BYTES  rotate a reused Claude session once its JSONL file reaches this size
// AI_MCP_CLAUDE_MAX_SESSION_INPUT_TOKENS  rotate when previous Claude turn input context reaches this token count
// AI_MCP_CLAUDE_PRUNE_SESSION_DAYS  delete inactive Claude session files older than this many days
// AI_MCP_CLAUDE_MAX_TOTAL_SESSION_BYTES  keep inactive Claude session files under this byte budget
var cfg = builder.Configuration;

string? codexBin = cfg["AI_MCP_CODEX_BIN"] ?? cfg["AiMcp:Binary"];
// If the configured path is missing (e.g. the extension auto-updated to a new version
// folder), rediscover the newest bundled codex binary instead of hard-failing.
if (string.IsNullOrWhiteSpace(codexBin) || !File.Exists(codexBin))
    codexBin = ResolveCodexBinary() ?? codexBin;
string? codexHome = cfg["AI_MCP_CODEX_HOME"] ?? cfg["AiMcp:Home"];
string? token = cfg["AI_MCP_TOKEN"] ?? cfg["AiMcp:Token"];
ServiceSecurity.RequireToken(token);
bool readOnlyProjects = ParseBool(cfg["AI_MCP_READ_ONLY_PROJECTS"] ?? cfg["AiMcp:ReadOnlyProjects"], defaultValue: true);
int maxConcurrency = int.TryParse(cfg["AI_MCP_MAX_CONCURRENT_REQUESTS"], out var mc) && mc > 0 ? Math.Min(mc, 32) : 6;
int maxPromptLength = ChatRequestValidator.MaxPromptLength;
if (cfg["AI_MCP_MAX_PROMPT_CHARS"] is { } promptLimit
    && (!int.TryParse(promptLimit, System.Globalization.NumberStyles.None,
        System.Globalization.CultureInfo.InvariantCulture, out maxPromptLength)
        || maxPromptLength < 1 || maxPromptLength > ChatRequestValidator.MaxConfiguredPromptLength))
    throw new InvalidOperationException("AI_MCP_MAX_PROMPT_CHARS must be an integer between 1 and 8000000.");
string defaultCwd = cfg["AI_MCP_DEFAULT_CWD"] ?? cfg["AiMcp:DefaultCwd"]
    ?? Directory.GetCurrentDirectory();
string defaultSandbox = cfg["AI_MCP_SANDBOX"] ?? cfg["AiMcp:Sandbox"] ?? "read-only";
int timeoutSeconds = int.TryParse(cfg["AI_MCP_TIMEOUT_SECONDS"] ?? cfg["AiMcp:TimeoutSeconds"], out var t)
    ? t : 300;
bool reuseCodexSession = ParseBool(cfg["AI_MCP_CODEX_REUSE_SESSION"] ?? cfg["AiMcp:ReuseSession"], defaultValue: false);
int codexSessionPoolSize = int.TryParse(cfg["AI_MCP_CODEX_SESSION_POOL_SIZE"] ?? cfg["AiMcp:SessionPoolSize"], out var cxps) && cxps > 0
    ? cxps : 1;
bool codexEphemeral = ParseBool(cfg["AI_MCP_CODEX_EPHEMERAL"] ?? cfg["AiMcp:Ephemeral"], defaultValue: false);
bool codexIgnoreSessionContext = ParseBool(cfg["AI_MCP_CODEX_IGNORE_SESSION_CONTEXT"] ?? cfg["AiMcp:IgnoreSessionContext"], defaultValue: false);
long maxCodexSessionBytes = long.TryParse(cfg["AI_MCP_CODEX_MAX_SESSION_BYTES"] ?? cfg["AiMcp:MaxSessionBytes"], out var msb)
    ? msb : 8L * 1024 * 1024;
long maxCodexSessionInputTokens = long.TryParse(cfg["AI_MCP_CODEX_MAX_SESSION_INPUT_TOKENS"] ?? cfg["AiMcp:MaxSessionInputTokens"], out var msit)
    ? msit : 0;
// Host Codex sessions are not owned by this service and must never be pruned here.
const int pruneCodexSessionDays = 0;
const long maxCodexTotalSessionBytes = 0;
bool reuseClaudeSession = ParseBool(cfg["AI_MCP_CLAUDE_REUSE_SESSION"] ?? cfg["AiMcp:ClaudeReuseSession"], defaultValue: false);
int claudeSessionPoolSize = int.TryParse(cfg["AI_MCP_CLAUDE_SESSION_POOL_SIZE"] ?? cfg["AiMcp:ClaudeSessionPoolSize"], out var clps) && clps > 0
    ? clps : 1;
bool claudeNoSessionPersistence = ParseBool(cfg["AI_MCP_CLAUDE_NO_SESSION_PERSISTENCE"] ?? cfg["AiMcp:ClaudeNoSessionPersistence"], defaultValue: false);
bool claudeIgnoreSessionContext = ParseBool(cfg["AI_MCP_CLAUDE_IGNORE_SESSION_CONTEXT"] ?? cfg["AiMcp:ClaudeIgnoreSessionContext"], defaultValue: false);
long maxClaudeSessionBytes = long.TryParse(cfg["AI_MCP_CLAUDE_MAX_SESSION_BYTES"] ?? cfg["AiMcp:ClaudeMaxSessionBytes"], out var cmsb)
    ? cmsb : 8L * 1024 * 1024;
long maxClaudeSessionInputTokens = long.TryParse(cfg["AI_MCP_CLAUDE_MAX_SESSION_INPUT_TOKENS"] ?? cfg["AiMcp:ClaudeMaxSessionInputTokens"], out var cmsit)
    ? cmsit : 0;
int pruneClaudeSessionDays = int.TryParse(cfg["AI_MCP_CLAUDE_PRUNE_SESSION_DAYS"] ?? cfg["AiMcp:ClaudePruneSessionDays"], out var cpsd)
    ? cpsd : 0;
long maxClaudeTotalSessionBytes = long.TryParse(cfg["AI_MCP_CLAUDE_MAX_TOTAL_SESSION_BYTES"] ?? cfg["AiMcp:ClaudeMaxTotalSessionBytes"], out var cmtsb)
    ? cmtsb : 0;

// Provider selection: codex (ChatGPT) is always present; claude is added when its binary exists.
string defaultProvider = cfg["AI_MCP_PROVIDER"] ?? cfg["AiMcp:Provider"] ?? "codex";
string? claudeBin = cfg["AI_MCP_CLAUDE_BIN"] ?? cfg["AiMcp:ClaudeBinary"];
if (string.IsNullOrWhiteSpace(claudeBin) || !File.Exists(claudeBin))
    claudeBin = ResolveClaudeBinary() ?? claudeBin;
string? claudeHome = cfg["AI_MCP_CLAUDE_HOME"] ?? cfg["AiMcp:ClaudeHome"];
// Read-only host-credentials mount: on a 401 the runner reseeds from here and retries once,
// so a host-side OAuth refresh no longer requires a container restart.
string? claudeCredentialSeed = cfg["AI_MCP_CLAUDE_CREDENTIAL_SEED"] ?? cfg["AiMcp:ClaudeCredentialSeed"];

var rootsRaw = cfg["AI_MCP_ALLOWED_ROOTS"] ?? cfg["AiMcp:Roots"] ?? defaultCwd;
var allowedRoots = rootsRaw
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(Path.GetFullPath)
    .ToList();
var writeRootsRaw = cfg["AI_MCP_WRITE_ROOTS"] ?? cfg["AiMcp:WriteRoots"] ?? "";
var allowedWriteRoots = writeRootsRaw
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(Path.GetFullPath)
    .ToList();

// The operator default may also be "danger-full-access" (see AllowedDefaultSandboxes) —
// clients still can't request it; only the server default may be set this permissive.
if (!ChatRequestValidator.AllowedDefaultSandboxes.Contains(defaultSandbox))
    throw new InvalidOperationException(
        $"AI_MCP_SANDBOX must be one of: {string.Join(", ", ChatRequestValidator.AllowedDefaultSandboxes)}.");

if (defaultSandbox == "workspace-write"
    && !ChatRequestValidator.IsWithinAllowedRoot(defaultCwd, allowedWriteRoots))
    throw new InvalidOperationException(
        "AI_MCP_DEFAULT_CWD must be inside AI_MCP_WRITE_ROOTS when the default sandbox is workspace-write.");

if (!ChatRequestValidator.AllowedProviders.Contains(defaultProvider))
    throw new InvalidOperationException(
        $"AI_MCP_PROVIDER must be one of: {string.Join(", ", ChatRequestValidator.AllowedProviders)}.");

// Optional allowlist: restrict which providers this instance exposes (e.g. "claude" for a
// Claude-only service, "codex" for a ChatGPT-only one). Empty/unset = expose every provider
// whose binary is actually present.
var providerAllowlist = (cfg["AI_MCP_PROVIDERS"] ?? cfg["AiMcp:Providers"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .ToHashSet(StringComparer.Ordinal);
bool Allowed(string p) => providerAllowlist.Count == 0 || providerAllowlist.Contains(p);

var codexAvailable = !string.IsNullOrWhiteSpace(codexBin) && File.Exists(codexBin) && Allowed("codex");
var claudeAvailable = !string.IsNullOrWhiteSpace(claudeBin) && File.Exists(claudeBin) && Allowed("claude");

// Which providers this instance can actually serve. Both CLIs are optional; the only hard
// rule is that the *default* provider must be one of them.
var availableProviders = new List<string>();
if (codexAvailable) availableProviders.Add("codex");
if (claudeAvailable) availableProviders.Add("claude");

if (availableProviders.Count == 0)
    throw new InvalidOperationException(
        "No provider is available. Configure AI_MCP_CODEX_BIN (codex) and/or AI_MCP_CLAUDE_BIN (claude), " +
        "and make sure AI_MCP_PROVIDERS (if set) names at least one of them.");

// If the request omits a provider we fall back to defaultProvider — it must be available.
// When the configured default isn't available, fall back to the first one that is.
if (!availableProviders.Contains(defaultProvider))
    defaultProvider = availableProviders[0];

if (readOnlyProjects && (defaultSandbox == "workspace-write" || allowedWriteRoots.Count != 0))
    throw new InvalidOperationException("Read-only services cannot configure writable project roots or a workspace-write default.");

if (!Directory.Exists(defaultCwd) || !ChatRequestValidator.IsWithinAllowedRoot(defaultCwd, allowedRoots))
    throw new InvalidOperationException("AI_MCP_DEFAULT_CWD must be an existing directory inside AI_MCP_ALLOWED_ROOTS.");

int maxOutputBytes = 8 * 1024 * 1024;
if (cfg["AI_MCP_MAX_OUTPUT_BYTES"] is { } outputLimit
    && (!int.TryParse(outputLimit, out maxOutputBytes) || maxOutputBytes < 64 * 1024 || maxOutputBytes > 64 * 1024 * 1024))
    throw new InvalidOperationException("AI_MCP_MAX_OUTPUT_BYTES must be between 65536 and 67108864.");

var timeout = TimeSpan.FromSeconds(timeoutSeconds);

// Client-requested read-only / workspace-write need codex's own sandbox. Measure once, with codex itself,
// whether it can start here; when it cannot, such requests are refused (sandbox_unavailable)
// instead of returning a review that could not open a single file.
var codexInnerSandbox = codexAvailable
    ? CodexSandboxProbe.Resolve(CodexSandboxProbe.Setting(key => cfg[key]), () => CodexSandboxProbe.Probe(codexBin!, codexHome))
    : null;

// Register one runner per available CLI; the registry routes each request to the right one.
builder.Services.AddSingleton<ProviderHealth>();
builder.Services.AddSingleton<IAgentRunnerRegistry>(sp =>
{
    var lf = sp.GetRequiredService<ILoggerFactory>();
    var map = new Dictionary<string, IAgentRunner>(StringComparer.Ordinal);
    if (codexAvailable)
    {
        map["codex"] = new CodexRunner(
            new CodexRunnerOptions
            {
                CodexBinary = codexBin!,
                CodexHome = codexHome,
                DefaultCwd = defaultCwd,
                Timeout = timeout,
                MaxOutputBytes = maxOutputBytes,
                ReuseSession = reuseCodexSession,
                SessionPoolSize = codexSessionPoolSize,
                Ephemeral = codexEphemeral,
                IgnoreSessionContext = codexIgnoreSessionContext,
                MaxSessionBytes = maxCodexSessionBytes,
                MaxSessionInputTokens = maxCodexSessionInputTokens,
                PruneSessionDays = pruneCodexSessionDays,
                MaxTotalSessionBytes = maxCodexTotalSessionBytes,
                RequireConversationIdForReuse = true,
            },
            lf.CreateLogger<CodexRunner>());
    }
    if (claudeAvailable)
    {
        map["claude"] = new ClaudeRunner(
            new ClaudeRunnerOptions
            {
                ClaudeBinary = claudeBin!,
                DefaultCwd = defaultCwd,
                Timeout = timeout,
                MaxOutputBytes = maxOutputBytes,
                HomeDir = claudeHome,
                ReuseSession = reuseClaudeSession,
                SessionPoolSize = claudeSessionPoolSize,
                NoSessionPersistence = claudeNoSessionPersistence,
                IgnoreSessionContext = claudeIgnoreSessionContext,
                MaxSessionBytes = maxClaudeSessionBytes,
                MaxSessionInputTokens = maxClaudeSessionInputTokens,
                PruneSessionDays = pruneClaudeSessionDays,
                MaxTotalSessionBytes = maxClaudeTotalSessionBytes,
                RequireConversationIdForReuse = true,
                CredentialSeedPath = claudeCredentialSeed,
            },
            lf.CreateLogger<ClaudeRunner>());
    }
    return new AgentRunnerRegistry(map.ToDictionary(pair => pair.Key,
        pair => (IAgentRunner)new BoundedAgentRunner(pair.Value, maxConcurrency,
            sp.GetRequiredService<ProviderHealth>(), pair.Key)));
});

// Default to localhost:8787 only when nothing else picked a binding (ASPNETCORE_URLS,
// --urls, etc.). Containers set ASPNETCORE_URLS, so this never fights the container.
if (string.IsNullOrEmpty(cfg["urls"]) && string.IsNullOrEmpty(cfg["ASPNETCORE_URLS"]))
    builder.WebHost.UseUrls("http://127.0.0.1:8787");

// JSON can escape each UTF-16 code unit as six bytes (\uXXXX). Allow that
// representation plus bounded request metadata, while retaining a finite body cap.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 6L * maxPromptLength + 64 * 1024);

var app = builder.Build();

app.Logger.LogInformation(
    "AiMcpContainers starting. providers=[{Providers}] default={Default} sandbox={Sandbox} roots=[{Roots}] writeRoots=[{WriteRoots}] auth={Auth} timeout={Timeout}s codexReuseSession={ReuseSession} codexSessionPoolSize={CodexSessionPoolSize} codexEphemeral={CodexEphemeral} codexIgnoreSessionContext={CodexIgnoreSessionContext} codexMaxSessionBytes={MaxSessionBytes} codexMaxSessionInputTokens={MaxSessionInputTokens} codexPruneSessionDays={PruneSessionDays} codexMaxTotalSessionBytes={MaxTotalSessionBytes} claudeReuseSession={ClaudeReuseSession} claudeSessionPoolSize={ClaudeSessionPoolSize} claudeNoSessionPersistence={ClaudeNoSessionPersistence} claudeIgnoreSessionContext={ClaudeIgnoreSessionContext} claudeMaxSessionBytes={ClaudeMaxSessionBytes} claudeMaxSessionInputTokens={ClaudeMaxSessionInputTokens} claudePruneSessionDays={ClaudePruneSessionDays} claudeMaxTotalSessionBytes={ClaudeMaxTotalSessionBytes}",
    string.Join(", ", availableProviders), defaultProvider, defaultSandbox, string.Join(", ", allowedRoots), string.Join(", ", allowedWriteRoots),
    string.IsNullOrEmpty(token) ? "disabled" : "bearer-token", timeoutSeconds,
    reuseCodexSession, codexSessionPoolSize, codexEphemeral, codexIgnoreSessionContext, maxCodexSessionBytes, maxCodexSessionInputTokens, pruneCodexSessionDays, maxCodexTotalSessionBytes,
    reuseClaudeSession, claudeSessionPoolSize, claudeNoSessionPersistence, claudeIgnoreSessionContext, maxClaudeSessionBytes, maxClaudeSessionInputTokens, pruneClaudeSessionDays, maxClaudeTotalSessionBytes);

if (codexInnerSandbox is { } inner)
{
    if (inner.Available)
        app.Logger.LogInformation("codex inner sandbox: available ({Detail})", inner.Detail);
    else
        app.Logger.LogWarning(
            "codex inner sandbox: unavailable ({Detail}). Requests for provider 'codex' with sandbox read-only/workspace-write are refused with sandbox_unavailable.",
            inner.Detail);
}

// Only the public UI and minimal health route bypass authentication. New API
// routes are protected by default, including case and trailing-slash variants.
static bool RequiresAuth(PathString path)
{
    var normalized = new PathString(path.Value?.TrimEnd('/'));
    return normalized != "" && normalized != "/index.html"
        && normalized != "/health" && normalized != "/favicon.ico";
}

// ---- Bearer-token gate ----
app.Use(async (ctx, next) =>
{
    if (RequiresAuth(ctx.Request.Path))
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        var ok = header.StartsWith(prefix, StringComparison.Ordinal)
                 && CryptographicEquals(header[prefix.Length..], token);
        if (!ok)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(
                new ErrorResponse { Error = "Unauthorized.", Code = "unauthorized" });
            return;
        }
    }
    await next();
});

// Serve the bundled login + test UI from wwwroot (index.html at "/").
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Json(new { status = "ok" }));

// Local prerequisites and recent failures only. No model calls or credential reads.
// Authentication stays required, and readiness never prevents a recovery request.
app.MapGet("/ready", (ProviderHealth health) =>
{
    var checks = availableProviders.Select(provider =>
    {
        var executable = ProviderHealth.ExecutableExists(provider == "codex" ? codexBin : claudeBin);
        var directory = Directory.Exists(defaultCwd);
        var sandbox = provider != "codex" || defaultSandbox == "danger-full-access"
            || codexInnerSandbox?.Available == true;
        var observation = health.State(provider);
        return new { provider, executable, directory, sandbox, observation,
            ready = executable && directory && sandbox && observation is not ("authentication_failed" or "quota_limited") };
    }).ToArray();
    var ready = checks.All(check => check.ready);
    return Results.Json(new { status = ready ? "ready" : "not_ready", verification = "local_and_recent_observations", checks },
        statusCode: ready ? 200 : 503);
});

// The token gate protects this route without sending a model prompt.
app.MapGet("/auth/verify", () => Results.Json(new
{
    ok = true,
    authRequired = true,
    providers = availableProviders,
    defaultProvider,
    defaultSandbox,
    codexInnerSandbox = codexInnerSandbox is null ? "not-applicable" : codexInnerSandbox.Available ? "available" : "unavailable",
}));

app.MapPost("/chat", async (
    HttpContext http,
    ChatRequest? req,
    IAgentRunnerRegistry registry,
    ILoggerFactory lf,
    CancellationToken ct) =>
{
    var log = lf.CreateLogger("Chat");

    var v = ChatRequestValidator.Validate(req, allowedRoots, defaultSandbox, Directory.Exists, defaultProvider, allowedWriteRoots, defaultCwd,
        codexInnerSandboxAvailable: codexInnerSandbox?.Available ?? true, readOnlyProjects: readOnlyProjects,
        maxPromptLength: maxPromptLength);
    if (!v.Ok)
        return Results.Json(v.Error, statusCode: v.StatusCode);

    var runner = registry.Get(v.Provider);
    if (runner is null)
        return Results.Json(
            new ErrorResponse { Error = $"Provider '{v.Provider}' is not available on this server.", Code = "provider_unavailable" },
            statusCode: 400);

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var result = await runner.RunAsync(v.Prompt!, v.Cwd, v.Model, v.Sandbox, v.ConversationId, ct);
    sw.Stop();

    if (!result.Ok)
    {
        var status = result.ErrorCode switch
        {
            "rate_limited" => 429,
            "bridge_busy" => 429,
            "model_at_capacity" => 503,
            "claude_auth_failed" => 401,
            "claude_access_forbidden" => 403,
            "timeout" => 504,
            "cancelled" => 499,
            _ => 502,
        };
        log.LogWarning("chat failed provider={Provider} code={Code} in {Ms}ms",
            v.Provider, result.ErrorCode, sw.ElapsedMilliseconds);
        WriteRetryAfter(http, result);
        return Results.Json(
            new ErrorResponse { Error = result.ErrorMessage ?? "Agent run failed.", Code = result.ErrorCode, RetryAtUtc = result.RetryAtUtc, LimitKind = result.LimitKind },
            statusCode: status);
    }

    return Results.Json(new ChatResponse
    {
        Answer = result.Output,
        Provider = v.Provider,
        // Prefer the model the CLI actually used (claude reports it); else the requested override;
        // else "default". Never claim a model the backend didn't run.
        Model = result.Model ?? v.Model ?? "default",
        Sandbox = v.Sandbox,
        Usage = result.Usage,
        DurationMs = sw.ElapsedMilliseconds,
    });
});

// Text-only Responses subset. Unsupported parameters are rejected explicitly.
// The request's `model` is informational — this instance's own
// provider serves the call. Body is flattened to a prompt, run, and returned in OpenAI shape.
app.MapPost("/v1/responses", async (
    HttpRequest httpReq,
    IAgentRunnerRegistry registry,
    ILoggerFactory lf,
    CancellationToken ct) =>
{
    var log = lf.CreateLogger("Responses");

    JsonDocument doc;
    try
    {
        doc = await JsonDocument.ParseAsync(httpReq.Body, cancellationToken: ct);
    }
    catch (JsonException)
    {
        return Results.Json(new ErrorResponse { Error = "Request body is not valid JSON.", Code = "bad_json" }, statusCode: 400);
    }

    using (doc)
    {
        var root = doc.RootElement;

        var requestError = ResponsesApi.ValidateRequest(root);
        if (requestError is not null)
            return Results.Json(requestError, statusCode: 400);

        var prompt = ResponsesApi.BuildPrompt(root);
        if (string.IsNullOrWhiteSpace(prompt))
            return Results.Json(new ErrorResponse { Error = "Field 'input' is required and must contain text.", Code = "missing_input" }, statusCode: 400);
        if (prompt.Length > maxPromptLength)
            return Results.Json(new ErrorResponse { Error = $"Flattened 'input' exceeds {maxPromptLength} characters.", Code = "prompt_too_long" }, statusCode: 400);

        // This endpoint always uses the instance's default provider + sandbox (no provider
        // switching mid-request — that's what separate bridge instances are for).
        var runner = registry.Get(defaultProvider);
        if (runner is null)
            return Results.Json(new ErrorResponse { Error = $"Provider '{defaultProvider}' is not available on this server.", Code = "provider_unavailable" }, statusCode: 400);

        // Same rule as /chat: a codex sandbox that cannot start yields an answer that read nothing.
        if (defaultProvider == "codex" && codexInnerSandbox is { Available: false }
            && ChatRequestValidator.AllowedSandboxes.Contains(defaultSandbox))
            return Results.Json(new ErrorResponse
            {
                Error = $"This bridge's default sandbox '{defaultSandbox}' cannot run for provider 'codex': codex's own sandbox cannot start here. The operator must change AI_MCP_SANDBOX.",
                Code = "sandbox_unavailable",
            }, statusCode: 400);

        var result = await runner.RunAsync(prompt, cwd: null, model: null, sandbox: defaultSandbox, conversationId: null, ct);

        if (!result.Ok)
        {
            var status = result.ErrorCode switch
            {
                "rate_limited" => 429,
            "bridge_busy" => 429,
            "model_at_capacity" => 503,
                "claude_auth_failed" => 401,
                "claude_access_forbidden" => 403,
                "timeout" => 504,
                "cancelled" => 499,
                _ => 502,
            };
            log.LogWarning("responses failed provider={Provider} code={Code}", defaultProvider, result.ErrorCode);
            WriteRetryAfter(httpReq.HttpContext, result);
            return Results.Json(new ErrorResponse { Error = result.ErrorMessage ?? "Agent run failed.", Code = result.ErrorCode, RetryAtUtc = result.RetryAtUtc, LimitKind = result.LimitKind }, statusCode: status);
        }

        // Report the model the backend ACTUALLY used when the CLI tells us (claude exposes it via
        // modelUsage). Never echo the caller's `model` as if we'd honored it — this endpoint runs
        // the instance's own provider on its CLI default, so the request model is not authoritative.
        // Fall back to "<provider>-default" so the logged model still names the real backend.
        var servedModel = result.Model ?? $"{defaultProvider}-default";
        return Results.Json(ResponsesApi.BuildResponse(result.Output, servedModel, defaultProvider, result.Usage));
    }
});

app.Run();

// Glob ~/.vscode*/extensions/openai.chatgpt-*/bin/<arch>/codex and pick the newest.
static string? ResolveCodexBinary()
{
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "linux-aarch64",
        _ => "linux-x86_64",
    };
    var exe = OperatingSystem.IsWindows() ? "codex.exe" : "codex";

    var candidates = new List<string>();
    foreach (var extRoot in new[] { ".vscode/extensions", ".vscode-server/extensions" })
    {
        var dir = Path.Combine(home, extRoot);
        if (!Directory.Exists(dir)) continue;
        try
        {
            candidates.AddRange(Directory
                .EnumerateDirectories(dir, "openai.chatgpt-*")
                .Select(d => Path.Combine(d, "bin", arch, exe))
                .Where(File.Exists));
        }
        catch { /* unreadable dir — skip */ }
    }

    // Folder names sort lexicographically close enough to version order; newest wins.
    return candidates.OrderByDescending(p => p, StringComparer.Ordinal).FirstOrDefault();
}

// Find the claude CLI: PATH first, then common nvm / npm-global install locations.
static string? ResolveClaudeBinary()
{
    if (OperatingSystem.IsWindows()) return null; // not targeted here

    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // 1. On PATH.
    var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
    foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
    {
        var cand = Path.Combine(dir, "claude");
        if (File.Exists(cand)) return cand;
    }

    // 2. nvm: ~/.nvm/versions/node/<ver>/bin/claude — newest version wins.
    var nvm = Path.Combine(home, ".nvm", "versions", "node");
    if (Directory.Exists(nvm))
    {
        try
        {
            var fromNvm = Directory.EnumerateDirectories(nvm)
                .Select(d => Path.Combine(d, "bin", "claude"))
                .Where(File.Exists)
                .OrderByDescending(p => p, StringComparer.Ordinal)
                .FirstOrDefault();
            if (fromNvm is not null) return fromNvm;
        }
        catch { /* skip */ }
    }

    // 3. Common global bins.
    foreach (var cand in new[]
             {
                 Path.Combine(home, ".claude", "local", "claude"),
                 Path.Combine(home, ".local", "share", "npm", "bin", "claude"),
                 "/usr/local/bin/claude",
             })
    {
        if (File.Exists(cand)) return cand;
    }

    return null;
}

// Constant-time comparison so token check doesn't leak length/content via timing.
static bool CryptographicEquals(string a, string b)
{
    var ba = System.Text.Encoding.UTF8.GetBytes(a);
    var bb = System.Text.Encoding.UTF8.GetBytes(b);
    return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ba, bb);
}

static bool ParseBool(string? value, bool defaultValue)
{
    if (string.IsNullOrWhiteSpace(value)) return defaultValue;
    return value.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "yes" or "on" => true,
        "0" or "false" or "no" or "off" => false,
        _ => defaultValue,
    };
}

// Exposed so the test project can spin up the app with WebApplicationFactory.

static void WriteRetryAfter(HttpContext http, CodexResult result)
{
    if (result.ErrorCode == "rate_limited" && result.RetryAtUtc is { } until)
        http.Response.Headers.RetryAfter = Math.Max(1, (long)Math.Ceiling((until - DateTimeOffset.UtcNow).TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public partial class Program { }
