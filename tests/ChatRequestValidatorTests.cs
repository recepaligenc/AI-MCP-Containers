using AiMcpContainers;
using Xunit;

namespace AiMcpContainers.Tests;

public class ChatRequestValidatorTests
{
    private static readonly IReadOnlyList<string> Roots = new[] { "/mnt/projects/Projects" };

    private static ValidationResult Run(ChatRequest? req, string sandbox = "read-only",
        Func<string, bool>? dirExists = null)
        => ChatRequestValidator.Validate(req, Roots, sandbox, dirExists ?? (_ => true),
            allowedWriteRoots: Roots, defaultCwd: "/mnt/projects/Projects");

    // ---- happy path ----
    [Fact]
    public void Valid_minimal_request_passes()
    {
        var r = Run(new ChatRequest { Prompt = "hello" });
        Assert.True(r.Ok);
        Assert.Equal("hello", r.Prompt);
        Assert.Equal("read-only", r.Sandbox);
        Assert.Null(r.Model);
        Assert.Null(r.Cwd);
    }

    [Fact]
    public void Valid_full_request_passes()
    {
        var r = Run(new ChatRequest
        {
            Prompt = "do x",
            Model = "gpt-5.5",
            Sandbox = "workspace-write",
            Cwd = "/mnt/projects/Projects",
            ConversationId = "sentiment-AAPL",
        });
        Assert.True(r.Ok);
        Assert.Equal("gpt-5.5", r.Model);
        Assert.Equal("workspace-write", r.Sandbox);
        Assert.Equal("/mnt/projects/Projects", r.Cwd);
        Assert.Equal("sentiment-AAPL", r.ConversationId);
    }

    // ---- missing / bad input ----
    [Fact]
    public void Null_body_is_400()
    {
        var r = Run(null);
        Assert.False(r.Ok);
        Assert.Equal(400, r.StatusCode);
        Assert.Equal("missing_body", r.Error!.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Missing_or_blank_prompt_is_400(string? prompt)
    {
        var r = Run(new ChatRequest { Prompt = prompt });
        Assert.False(r.Ok);
        Assert.Equal(400, r.StatusCode);
        Assert.Equal("missing_prompt", r.Error!.Code);
    }

    [Fact]
    public void Bad_sandbox_is_400()
    {
        var r = Run(new ChatRequest { Prompt = "x", Sandbox = "danger-full-access" });
        Assert.False(r.Ok);
        Assert.Equal(400, r.StatusCode);
        Assert.Equal("bad_sandbox", r.Error!.Code);
    }

    // danger-full-access is an OPERATOR-only default (for unprivileged containers where
    // codex's bwrap can't create a user namespace); a CLIENT can never request it.
    [Fact]
    public void Client_cannot_request_danger_full_access()
        => Assert.DoesNotContain("danger-full-access", ChatRequestValidator.AllowedSandboxes);

    [Fact]
    public void Operator_default_may_be_danger_full_access()
    {
        Assert.Contains("danger-full-access", ChatRequestValidator.AllowedDefaultSandboxes);
        Assert.Contains("read-only", ChatRequestValidator.AllowedDefaultSandboxes);
        Assert.Contains("workspace-write", ChatRequestValidator.AllowedDefaultSandboxes);
    }

    // When the operator runs danger-full-access as the default, a request that omits
    // sandbox inherits it — and that is allowed (the server, not the client, chose it).
    [Fact]
    public void Request_without_sandbox_inherits_operator_default()
    {
        var r = Run(new ChatRequest { Prompt = "x" }, sandbox: "danger-full-access");
        Assert.True(r.Ok);
        Assert.Equal("danger-full-access", r.Sandbox);
    }

    [Theory]
    [InlineData("gpt 5; rm -rf /")]
    [InlineData("model$(whoami)")]
    [InlineData("--dangerously-bypass-approvals-and-sandbox")]
    [InlineData("a/../b")]
    public void Malicious_model_is_400(string model)
    {
        var r = Run(new ChatRequest { Prompt = "x", Model = model });
        Assert.False(r.Ok);
        Assert.Equal(400, r.StatusCode);
        Assert.Equal("bad_model", r.Error!.Code);
    }

    // ---- provider ----
    [Fact]
    public void Default_provider_is_codex()
    {
        var r = Run(new ChatRequest { Prompt = "x" });
        Assert.True(r.Ok);
        Assert.Equal("codex", r.Provider);
    }

    [Fact]
    public void Explicit_claude_provider_passes()
    {
        var r = Run(new ChatRequest { Prompt = "x", Provider = "claude" });
        Assert.True(r.Ok);
        Assert.Equal("claude", r.Provider);
    }

    [Theory]
    [InlineData("gemini")]
    [InlineData("CODEX")]
    [InlineData("claude; rm -rf /")]
    [InlineData("")]
    public void Bad_provider_is_400(string provider)
    {
        var r = Run(new ChatRequest { Prompt = "x", Provider = provider });
        Assert.False(r.Ok);
        Assert.Equal(400, r.StatusCode);
        Assert.Equal("bad_provider", r.Error!.Code);
    }

    [Theory]
    [InlineData("../bad")]
    [InlineData("bad id")]
    [InlineData("--bad")]
    public void Bad_conversation_id_is_400(string conversationId)
    {
        var r = Run(new ChatRequest { Prompt = "x", ConversationId = conversationId });
        Assert.False(r.Ok);
        Assert.Equal(400, r.StatusCode);
        Assert.Equal("bad_conversation_id", r.Error!.Code);
    }

    // ---- cwd / path traversal ----
    [Fact]
    public void Cwd_outside_root_is_403()
    {
        var r = Run(new ChatRequest { Prompt = "x", Cwd = "/etc" });
        Assert.False(r.Ok);
        Assert.Equal(403, r.StatusCode);
        Assert.Equal("cwd_forbidden", r.Error!.Code);
    }

    [Fact]
    public void Cwd_traversal_escape_is_403()
    {
        // Resolves to /mnt/projects which is the parent of the root — must be rejected.
        var r = Run(new ChatRequest { Prompt = "x", Cwd = "/mnt/projects/Projects/../../etc" });
        Assert.False(r.Ok);
        Assert.Equal(403, r.StatusCode);
        Assert.Equal("cwd_forbidden", r.Error!.Code);
    }

    [Fact]
    public void Cwd_inside_root_but_missing_is_400()
    {
        var r = Run(new ChatRequest { Prompt = "x", Cwd = "/mnt/projects/Projects/nope-xyz" },
            dirExists: _ => false);
        Assert.False(r.Ok);
        Assert.Equal(400, r.StatusCode);
        Assert.Equal("cwd_not_found", r.Error!.Code);
    }

    [Fact]
    public void Workspace_write_outside_narrow_write_root_is_403()
    {
        var r = ChatRequestValidator.Validate(
            new ChatRequest
            {
                Prompt = "edit",
                Sandbox = "workspace-write",
                Cwd = "/mnt/projects/Projects/read-only-project",
            },
            Roots,
            "read-only",
            _ => true,
            allowedWriteRoots: new[] { "/mnt/projects/Projects/writable-project" },
            defaultCwd: "/mnt/projects/Projects/read-only-project");

        Assert.False(r.Ok);
        Assert.Equal(403, r.StatusCode);
        Assert.Equal("cwd_not_writable", r.Error!.Code);
    }

    // ---- boundaries ----
    [Fact]
    public void Prompt_at_max_length_passes()
    {
        var r = Run(new ChatRequest { Prompt = new string('a', ChatRequestValidator.MaxPromptLength) });
        Assert.True(r.Ok);
    }

    [Fact]
    public void Prompt_over_max_length_is_400()
    {
        var r = Run(new ChatRequest { Prompt = new string('a', ChatRequestValidator.MaxPromptLength + 1) });
        Assert.False(r.Ok);
        Assert.Equal("prompt_too_long", r.Error!.Code);
    }

    [Fact]
    public void Root_itself_is_within_root()
        => Assert.True(ChatRequestValidator.IsWithinAllowedRoot("/mnt/projects/Projects", Roots));

    [Fact]
    public void Sibling_prefix_is_not_within_root()
        // "/mnt/projects/Projects-evil" must NOT match root "/mnt/projects/Projects".
        => Assert.False(ChatRequestValidator.IsWithinAllowedRoot("/mnt/projects/Projects-evil", Roots));

    // ---- codex's own sandbox cannot start ----
    private static ValidationResult RunInner(ChatRequest req, string defaultSandbox, bool innerAvailable)
        => ChatRequestValidator.Validate(req, Roots, defaultSandbox, _ => true,
            allowedWriteRoots: Roots, defaultCwd: "/mnt/projects/Projects", codexInnerSandboxAvailable: innerAvailable);

    [Theory]
    [InlineData("read-only")]
    [InlineData("workspace-write")]
    public void Codex_sandbox_is_refused_when_inner_sandbox_unavailable(string sandbox)
    {
        var r = RunInner(new ChatRequest { Prompt = "x", Sandbox = sandbox }, "danger-full-access", innerAvailable: false);
        Assert.False(r.Ok);
        Assert.Equal(400, r.StatusCode);
        Assert.Equal("sandbox_unavailable", r.Error!.Code);
        Assert.Contains("Omit 'sandbox'", r.Error.Error);
    }

    [Fact]
    public void Explicit_sandbox_with_unrunnable_default_names_the_operator_setting()
    {
        var r = RunInner(new ChatRequest { Prompt = "x", Sandbox = "workspace-write" }, "read-only", innerAvailable: false);
        Assert.Equal("sandbox_unavailable", r.Error!.Code);
        Assert.Contains("AI_MCP_SANDBOX", r.Error.Error);
        Assert.DoesNotContain("Omit 'sandbox'", r.Error.Error);
    }

    [Fact]
    public void Codex_sandbox_passes_when_inner_sandbox_available()
    {
        var r = RunInner(new ChatRequest { Prompt = "x", Sandbox = "read-only" }, "danger-full-access", innerAvailable: true);
        Assert.True(r.Ok);
        Assert.Equal("read-only", r.Sandbox);
    }

    [Fact]
    public void Operator_full_access_default_passes_when_inner_sandbox_unavailable()
    {
        var r = RunInner(new ChatRequest { Prompt = "x" }, "danger-full-access", innerAvailable: false);
        Assert.True(r.Ok);
        Assert.Equal("danger-full-access", r.Sandbox);
    }

    [Fact]
    public void Claude_sandbox_is_not_affected_by_codex_inner_sandbox()
    {
        var r = RunInner(new ChatRequest { Prompt = "x", Provider = "claude", Sandbox = "read-only" }, "danger-full-access", innerAvailable: false);
        Assert.True(r.Ok);
        Assert.Equal("claude", r.Provider);
    }

    // read-only-web (isa-analysis card 1.1.71): Claude only; codex has no such mode.
    [Fact]
    public void ReadOnlyWeb_is_refused_for_codex()
    {
        var r = Run(new ChatRequest { Prompt = "x", Sandbox = "read-only-web", Provider = "codex" });
        Assert.False(r.Ok);
        Assert.Equal(400, r.StatusCode);
        Assert.Equal("sandbox_provider_mismatch", r.Error!.Code);
    }

    [Fact]
    public void ReadOnlyWeb_is_accepted_for_claude()
    {
        var r = Run(new ChatRequest { Prompt = "x", Sandbox = "read-only-web", Provider = "claude" });
        Assert.True(r.Ok);
        Assert.Equal("read-only-web", r.Sandbox);
    }
}
