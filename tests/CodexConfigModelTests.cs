using AiMcpContainers;
using Xunit;

namespace AiMcpContainers.Tests;

// codex exec --json does NOT report the model it used, so the bridge reads the authoritative
// top-level `model` from $CODEX_HOME/config.toml to log the truth (instead of a "codex-default"
// placeholder). A `model` key inside a [section] is unrelated and must be ignored.
public class CodexConfigModelTests
{
    [Fact]
    public void Reads_top_level_model()
    {
        const string toml = """
        model = "gpt-5.5"
        model_reasoning_effort = "high"
        """;
        Assert.Equal("gpt-5.5", CodexRunner.ParseConfigModel(toml));
    }

    [Fact]
    public void Ignores_model_key_inside_a_section()
    {
        // Only the top-level model counts; a model under [projects...] or [mcp_servers...] is noise.
        const string toml = """
        model = "gpt-5.5"
        [projects."/x"]
        model = "should-be-ignored"
        """;
        Assert.Equal("gpt-5.5", CodexRunner.ParseConfigModel(toml));
    }

    [Fact]
    public void Returns_null_when_only_a_sectioned_model_exists()
    {
        const string toml = """
        [mcp_servers.foo]
        model = "in-section-only"
        """;
        Assert.Null(CodexRunner.ParseConfigModel(toml));
    }

    [Fact]
    public void Tolerates_comments_blank_lines_and_whitespace()
    {
        const string toml = """

        # the model we use
          model   =   "o3"

        """;
        Assert.Equal("o3", CodexRunner.ParseConfigModel(toml));
    }

    [Fact]
    public void Single_quoted_value_is_unwrapped()
        => Assert.Equal("gpt-4o", CodexRunner.ParseConfigModel("model = 'gpt-4o'"));

    [Fact]
    public void Returns_null_when_no_model_key()
        => Assert.Null(CodexRunner.ParseConfigModel("approval_policy = \"never\""));

    [Fact]
    public void Returns_null_for_empty_model_value()
        => Assert.Null(CodexRunner.ParseConfigModel("model = \"\""));

    [Fact]
    public void Does_not_match_model_prefixed_keys()
    {
        // `model_reasoning_effort` must not be mistaken for `model`.
        Assert.Null(CodexRunner.ParseConfigModel("model_reasoning_effort = \"high\""));
    }
}
