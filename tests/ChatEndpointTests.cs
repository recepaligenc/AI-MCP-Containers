using System.Net;
using System.Net.Http.Json;
using AiMcpContainers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AiMcpContainers.Tests;

/// <summary>Records every call so tests can assert the runner did / did not fire.</summary>
public sealed class FakeCodexRunner : IAgentRunner
{
    public int Calls { get; private set; }
    public CodexResult Next { get; set; } = new(true, "stub-answer", null, null);

    public Task<CodexResult> RunAsync(string prompt, string? cwd, string? model, string sandbox, string? conversationId, CancellationToken ct)
    {
        Calls++;
        LastPrompt = prompt;
        LastSandbox = sandbox;
        LastConversationId = conversationId;
        return Task.FromResult(Next);
    }

    public string? LastPrompt { get; private set; }
    public string? LastSandbox { get; private set; }
    public string? LastConversationId { get; private set; }
}

public sealed class TestApp : WebApplicationFactory<Program>
{
    public FakeCodexRunner Runner { get; } = new();      // stands in for "codex"
    public FakeCodexRunner ClaudeRunner { get; } = new(); // stands in for "claude"
    public const string DefaultToken = "tttttttttttttttttttttttttttttttt";
    public const string ExplicitToken = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";
    public string? Token { get; init; } = DefaultToken;
    public bool ReadOnlyProjects { get; init; }
    public string DefaultSandbox { get; init; } = "read-only";
    /// <summary>Pinned so the suite never depends on whether the host can run bubblewrap.</summary>
    public string InnerSandbox { get; init; } = "available";
    public string Providers { get; init; } = "";

    public new HttpClient CreateClient()
    {
        var client = base.CreateClient();
        if (Token == DefaultToken) client.DefaultRequestHeaders.Authorization = new("Bearer", Token);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("AiMcp:Binary", "/bin/sh");          // exists → startup passes
        builder.UseSetting("AiMcp:ClaudeBinary", "/bin/sh");    // exists → claude provider available
        builder.UseSetting("AiMcp:Sandbox", DefaultSandbox);
        builder.UseSetting("AiMcp:InnerSandbox", InnerSandbox);
        // The environment key wins over AiMcp:InnerSandbox (CodexSandboxProbe.Setting); pin it too so an
        // exported AI_MCP_CODEX_INNER_SANDBOX on the test host cannot change these tests.
        builder.UseSetting("AI_MCP_CODEX_INNER_SANDBOX", InnerSandbox);
        builder.UseSetting("AiMcp:Providers", Providers);
        builder.UseSetting("AiMcp:Roots", "/tmp");
        builder.UseSetting("AiMcp:WriteRoots", ReadOnlyProjects ? "" : "/tmp/writable");
        builder.UseSetting("AiMcp:ReadOnlyProjects", ReadOnlyProjects.ToString());
        builder.UseSetting("AiMcp:DefaultCwd", "/tmp");
        builder.UseSetting("AiMcp:Token", Token ?? "");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAgentRunnerRegistry>();
            services.AddSingleton<IAgentRunnerRegistry>(new AgentRunnerRegistry(
                new Dictionary<string, IAgentRunner>(StringComparer.Ordinal)
                {
                    ["codex"] = Runner,
                    ["claude"] = ClaudeRunner,
                }));
        });
    }
}

public class ChatEndpointTests
{
    [Fact]
    public async Task Health_is_ok_without_auth()
    {
        using var app = new TestApp { Token = TestApp.ExplicitToken };
        using var client = app.CreateClient();
        var resp = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Theory]
    [InlineData("/future-api")]
    [InlineData("/auth/verify/extra")]
    public async Task Unlisted_routes_are_protected_by_default(string route)
    {
        using var app = new TestApp { Token = TestApp.ExplicitToken };
        using var client = app.CreateClient();
        using var response = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, app.Runner.Calls);
    }

    [Fact]
    public async Task Public_ui_uses_the_current_project_name_and_offers_no_write_mode()
    {
        using var app = new TestApp { Token = TestApp.ExplicitToken };
        using var client = app.CreateClient();
        var html = await client.GetStringAsync("/");
        Assert.Contains("<title>AI-MCP-Containers</title>", html);
        Assert.DoesNotContain("value=\"workspace-write\"", html);
    }

    [Fact]
    public async Task Valid_chat_returns_answer()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/chat", new { prompt = "hi" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ChatResponse>();
        Assert.Equal("stub-answer", body!.Answer);
        Assert.Equal(1, app.Runner.Calls);
        Assert.Equal("hi", app.Runner.LastPrompt);
    }

    [Fact]
    public async Task Missing_prompt_is_400_and_runner_not_called()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/chat", new { notprompt = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var err = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("missing_prompt", err!.Code);
        Assert.Equal(0, app.Runner.Calls); // no side effect
    }

    [Fact]
    public async Task Bad_sandbox_is_400_and_runner_not_called()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/chat", new { prompt = "x", sandbox = "danger-full-access" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(0, app.Runner.Calls);
    }

    [Fact]
    public async Task Cwd_outside_root_is_403_and_runner_not_called()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/chat", new { prompt = "x", cwd = "/etc" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal(0, app.Runner.Calls);
    }

    // ---- auth ----
    [Theory]
    [InlineData("/chat/", false)]
    [InlineData("/CHAT/", false)]
    [InlineData("/v1/responses/", false)]
    [InlineData("/V1/RESPONSES/", false)]
    [InlineData("/auth/verify/", true)]
    [InlineData("/AUTH/VERIFY/", true)]
    public async Task Route_variants_require_valid_token_before_any_provider_call(string path, bool get)
    {
        using var app = new TestApp { Token = TestApp.ExplicitToken };
        using var client = app.CreateClient();
        foreach (var token in new string?[] { null, "wrong", TestApp.ExplicitToken })
        {
            client.DefaultRequestHeaders.Authorization = token is null ? null : new("Bearer", token);
            using var response = get
                ? await client.GetAsync(path)
                : await client.PostAsJsonAsync(path, new { });

            // An authenticated empty body reaches input validation without calling a CLI.
            var expected = token != TestApp.ExplicitToken ? HttpStatusCode.Unauthorized
                : get ? HttpStatusCode.OK : HttpStatusCode.BadRequest;
            Assert.Equal(expected, response.StatusCode);
            Assert.Equal(0, app.Runner.Calls);
            Assert.Equal(0, app.ClaudeRunner.Calls);
        }
    }

    [Fact]
    public async Task Missing_token_is_401_and_runner_not_called()
    {
        using var app = new TestApp { Token = TestApp.ExplicitToken };
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/chat", new { prompt = "x" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal(0, app.Runner.Calls);
    }

    [Fact]
    public async Task Wrong_token_is_401_and_runner_not_called()
    {
        using var app = new TestApp { Token = TestApp.ExplicitToken };
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "wrong");

        var resp = await client.PostAsJsonAsync("/chat", new { prompt = "x" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal(0, app.Runner.Calls);
    }

    [Fact]
    public async Task Correct_token_passes()
    {
        using var app = new TestApp { Token = TestApp.ExplicitToken };
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestApp.ExplicitToken);

        var resp = await client.PostAsJsonAsync("/chat", new { prompt = "x" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, app.Runner.Calls);
    }

    // ---- downstream failure mapping ----
    [Fact]
    public async Task Codex_timeout_maps_to_504()
    {
        using var app = new TestApp();
        app.Runner.Next = new CodexResult(false, "", "timeout", "timed out");
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/chat", new { prompt = "x" });

        Assert.Equal(HttpStatusCode.GatewayTimeout, resp.StatusCode);
        var err = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("timeout", err!.Code);
    }

    [Fact]
    public async Task Codex_failure_maps_to_502()
    {
        using var app = new TestApp();
        app.Runner.Next = new CodexResult(false, "", "codex_failed", "boom");
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/chat", new { prompt = "x" });

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    [Theory]
    [InlineData("claude_auth_failed", HttpStatusCode.Unauthorized)]
    [InlineData("claude_access_forbidden", HttpStatusCode.Forbidden)]
    public async Task Claude_access_failures_keep_their_typed_http_status(
        string errorCode, HttpStatusCode expected)
    {
        using var app = new TestApp();
        app.ClaudeRunner.Next = new CodexResult(false, "", errorCode, "sanitized");
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/chat", new { prompt = "x", provider = "claude" });

        Assert.Equal(expected, resp.StatusCode);
        var err = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal(errorCode, err!.Code);
    }

    [Fact]
    public async Task Workspace_write_outside_write_root_is_rejected_before_runner()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/chat", new
        {
            prompt = "edit",
            sandbox = "workspace-write",
            cwd = "/tmp",
        });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var err = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("cwd_not_writable", err!.Code);
        Assert.Equal(0, app.Runner.Calls);
    }

    // ---- /auth/verify (used by the login screen) ----
    [Fact]
    public async Task Verify_with_correct_token_is_200()
    {
        using var app = new TestApp { Token = TestApp.ExplicitToken };
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestApp.ExplicitToken);

        var resp = await client.GetAsync("/auth/verify");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(0, app.Runner.Calls); // verifying must not run codex
    }

    [Fact]
    public async Task Verify_with_no_token_is_401()
    {
        using var app = new TestApp { Token = TestApp.ExplicitToken };
        using var client = app.CreateClient();

        var resp = await client.GetAsync("/auth/verify");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Verify_with_wrong_token_is_401()
    {
        using var app = new TestApp { Token = TestApp.ExplicitToken };
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "nope");

        var resp = await client.GetAsync("/auth/verify");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("change-me")]
    public void Missing_or_weak_configured_token_refuses_startup(string? token)
    {
        using var app = new TestApp { Token = token };
        Assert.Throws<InvalidOperationException>(() => app.CreateClient());
    }

    [Fact]
    public async Task Read_only_project_policy_refuses_write_requests_before_a_runner_call()
    {
        using var app = new TestApp { ReadOnlyProjects = true };
        using var client = app.CreateClient();
        using var response = await client.PostAsJsonAsync("/chat", new { prompt = "edit", sandbox = "workspace-write" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("project_write_disabled", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Code);
        Assert.Equal(0, app.Runner.Calls);
        Assert.Equal(0, app.ClaudeRunner.Calls);
    }

    // ---- provider routing ----
    [Fact]
    public async Task Default_provider_routes_to_codex()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/chat", new { prompt = "hi" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, app.Runner.Calls);        // codex fired
        Assert.Equal(0, app.ClaudeRunner.Calls);  // claude did not
        var body = await resp.Content.ReadFromJsonAsync<ChatResponse>();
        Assert.Equal("codex", body!.Provider);
    }

    [Fact]
    public async Task Explicit_claude_provider_routes_to_claude()
    {
        using var app = new TestApp();
        app.ClaudeRunner.Next = new CodexResult(true, "claude-answer", null, null);
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/chat", new { prompt = "hi", provider = "claude" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(0, app.Runner.Calls);
        Assert.Equal(1, app.ClaudeRunner.Calls);
        var body = await resp.Content.ReadFromJsonAsync<ChatResponse>();
        Assert.Equal("claude", body!.Provider);
        Assert.Equal("claude-answer", body.Answer);
    }

    [Fact]
    public async Task Unknown_provider_is_400_and_no_runner_called()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/chat", new { prompt = "hi", provider = "gemini" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var err = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("bad_provider", err!.Code);
        Assert.Equal(0, app.Runner.Calls);
        Assert.Equal(0, app.ClaudeRunner.Calls);
    }

    // ---- served-model reporting (truthful usage logging downstream) ----
    [Fact]
    public async Task Chat_reports_model_the_runner_actually_used()
    {
        using var app = new TestApp();
        // Runner ran a different model than any the caller might request.
        app.Runner.Next = new CodexResult(true, "answer", null, null, Model: "claude-sonnet-4-6-20250930",
            Usage: NativeUsage.FromClaudeResult("""{"modelUsage":{"main":{"inputTokens":10,"outputTokens":2}}}"""));
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/chat", new { prompt = "hi", model = "gpt-5" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ChatResponse>();
        Assert.Equal("claude-sonnet-4-6-20250930", body!.Model);
        Assert.Equal("per_call", body.Usage!.Scope);
        Assert.Equal(10, Assert.Single(body.Usage.Models).InputTokens); // actual, not the requested "gpt-5"
    }

    [Fact]
    public async Task Chat_falls_back_to_requested_model_when_runner_reports_none()
    {
        using var app = new TestApp();
        app.Runner.Next = new CodexResult(true, "answer", null, null); // Model = null
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/chat", new { prompt = "hi", model = "gpt-5" });

        var body = await resp.Content.ReadFromJsonAsync<ChatResponse>();
        Assert.Equal("gpt-5", body!.Model);
    }

    [Fact]
    public async Task Chat_passes_optional_conversation_id_to_runner()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/chat", new { prompt = "hi", conversationId = "sentiment-AAPL" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("sentiment-AAPL", app.Runner.LastConversationId);
    }

    [Fact]
    public async Task Bad_conversation_id_is_400_and_runner_not_called()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/chat", new { prompt = "hi", conversationId = "../bad" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var err = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("bad_conversation_id", err!.Code);
        Assert.Equal(0, app.Runner.Calls);
    }

    [Fact]
    public async Task Responses_endpoint_reports_runner_model_not_requested_model()
    {
        using var app = new TestApp();
        app.Runner.Next = new CodexResult(true, "answer", null, null, Model: "claude-sonnet-4-6-20250930");
        using var client = app.CreateClient();

        // OpenAI Responses shape; the request's `model` must NOT be echoed as if honored.
        var resp = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "gpt-5",
            input = new[] { new { role = "user", content = "hi" } }
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("claude-sonnet-4-6-20250930", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Responses_endpoint_falls_back_to_provider_default_model()
    {
        using var app = new TestApp();
        app.Runner.Next = new CodexResult(true, "answer", null, null); // Model = null
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "gpt-5",
            input = new[] { new { role = "user", content = "hi" } }
        });

        using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        // Names the real backend ("codex-default"), never the unhonored requested "gpt-5".
        Assert.Equal("codex-default", doc.RootElement.GetProperty("model").GetString());
    }

    // ---- the login UI itself must be reachable without a token ----
    [Fact]
    public async Task Root_is_not_blocked_by_auth_gate()
    {
        using var app = new TestApp { Token = TestApp.ExplicitToken };
        using var client = app.CreateClient();

        var resp = await client.GetAsync("/");

        // The gate only guards /chat and /auth/verify — the static login page is public.
        // It may be 200 (served) or 404 (file not in test content root), but never 401.
        Assert.NotEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ---- codex's own sandbox cannot start (sandbox_unavailable) ----
    [Theory]
    [InlineData("read-only")]
    [InlineData("workspace-write")]
    public async Task Codex_sandbox_request_is_refused_when_inner_sandbox_unavailable(string sandbox)
    {
        using var app = new TestApp { DefaultSandbox = "danger-full-access", InnerSandbox = "unavailable" };
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/chat", new { prompt = "x", sandbox, cwd = "/tmp" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var err = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("sandbox_unavailable", err!.Code);
        Assert.Contains("Omit 'sandbox'", err.Error);
        Assert.Contains("'danger-full-access'", err.Error);
        Assert.Equal(0, app.Runner.Calls); // no side effect
    }

    [Fact]
    public async Task Omitted_sandbox_uses_operator_default_when_inner_sandbox_unavailable()
    {
        using var app = new TestApp { DefaultSandbox = "danger-full-access", InnerSandbox = "unavailable" };
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/chat", new { prompt = "x" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, app.Runner.Calls);
        Assert.Equal("danger-full-access", app.Runner.LastSandbox);
    }

    [Fact]
    public async Task Claude_read_only_is_unaffected_by_codex_inner_sandbox()
    {
        using var app = new TestApp { DefaultSandbox = "danger-full-access", InnerSandbox = "unavailable" };
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/chat", new { prompt = "x", provider = "claude", sandbox = "read-only" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, app.ClaudeRunner.Calls);
        Assert.Equal("read-only", app.ClaudeRunner.LastSandbox);
        Assert.Equal(0, app.Runner.Calls);
    }

    [Fact]
    public async Task Unrunnable_default_sandbox_is_refused_with_operator_message()
    {
        using var app = new TestApp { InnerSandbox = "unavailable" }; // default read-only
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/chat", new { prompt = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var err = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("sandbox_unavailable", err!.Code);
        Assert.Contains("AI_MCP_SANDBOX", err.Error);
        Assert.Equal(0, app.Runner.Calls);
    }

    [Fact]
    public async Task Responses_endpoint_refuses_unrunnable_default_sandbox()
    {
        using var app = new TestApp { InnerSandbox = "unavailable" }; // default read-only, default provider codex
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/v1/responses", new { input = "hi" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var err = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("sandbox_unavailable", err!.Code);
        Assert.Equal(0, app.Runner.Calls);
    }

    [Fact]
    public async Task Responses_endpoint_runs_under_full_access_default_when_inner_sandbox_unavailable()
    {
        using var app = new TestApp { DefaultSandbox = "danger-full-access", InnerSandbox = "unavailable" };
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/v1/responses", new { input = "hi" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, app.Runner.Calls);
        Assert.Equal("danger-full-access", app.Runner.LastSandbox);
    }

    [Fact]
    public async Task Sandbox_refusal_takes_precedence_over_cwd_errors()
    {
        // An unrunnable sandbox makes every other check moot; the order is pinned so a reorder is noticed.
        using var app = new TestApp { DefaultSandbox = "danger-full-access", InnerSandbox = "unavailable" };
        using var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/chat", new { prompt = "x", sandbox = "workspace-write", cwd = "/etc" });

        var err = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("sandbox_unavailable", err!.Code);
        Assert.Equal(0, app.Runner.Calls);
    }

    [Fact]
    public async Task Verify_reports_not_applicable_without_codex()
    {
        using var app = new TestApp { Providers = "claude", InnerSandbox = "unavailable" };
        using var client = app.CreateClient();

        var resp = await client.GetAsync("/auth/verify");

        using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("not-applicable", doc.RootElement.GetProperty("codexInnerSandbox").GetString());
    }

    [Theory]
    [InlineData("available")]
    [InlineData("unavailable")]
    public async Task Verify_reports_codex_inner_sandbox(string inner)
    {
        using var app = new TestApp { DefaultSandbox = "danger-full-access", InnerSandbox = inner };
        using var client = app.CreateClient();

        var resp = await client.GetAsync("/auth/verify");

        using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(inner, doc.RootElement.GetProperty("codexInnerSandbox").GetString());
    }
}
