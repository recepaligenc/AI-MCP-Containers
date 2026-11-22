using System.Text.Json;
using AiMcpContainers;
using Xunit;

namespace AiMcpContainers.Tests;

public class ResponsesApiTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ---- BuildPrompt ----
    [Fact]
    public void Plain_string_input_becomes_prompt()
    {
        var p = ResponsesApi.BuildPrompt(Parse("""{"input":"hello world"}"""));
        Assert.Equal("hello world", p);
    }

    [Fact]
    public void Developer_and_user_messages_are_ordered_instructions_first()
    {
        var json = """
        {"input":[
          {"role":"user","content":"the news item"},
          {"role":"developer","content":"classify this"}
        ]}
        """;
        var p = ResponsesApi.BuildPrompt(Parse(json));
        // developer (instruction) must come before user (body) regardless of array order
        Assert.Equal("classify this\n\nthe news item", p);
    }

    [Fact]
    public void Content_as_part_array_is_concatenated()
    {
        var json = """
        {"input":[
          {"role":"user","content":[{"type":"input_text","text":"a"},{"type":"input_text","text":"b"}]}
        ]}
        """;
        var p = ResponsesApi.BuildPrompt(Parse(json));
        Assert.Equal("ab", p);
    }

    [Fact]
    public void System_role_treated_as_instruction()
    {
        var json = """{"input":[{"role":"system","content":"sys"},{"role":"user","content":"u"}]}""";
        Assert.Equal("sys\n\nu", ResponsesApi.BuildPrompt(Parse(json)));
    }

    [Theory]
    [InlineData("""{"model":"x"}""")]          // no input field
    [InlineData("""{"input":""}""")]            // empty string
    [InlineData("""{"input":"   "}""")]         // whitespace
    [InlineData("""{"input":[]}""")]            // empty array
    [InlineData("""{"input":[{"role":"user"}]}""")] // message without content
    public void Missing_or_empty_input_returns_null(string json)
    {
        Assert.Null(ResponsesApi.BuildPrompt(Parse(json)));
    }

    // ---- BuildResponse ----
    [Fact]
    public void Response_has_openai_shape_with_answer_text()
    {
        var obj = ResponsesApi.BuildResponse("the answer", "gpt-x", "codex");
        var json = JsonSerializer.Serialize(obj);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("response", root.GetProperty("object").GetString());
        Assert.Equal("gpt-x", root.GetProperty("model").GetString());
        Assert.Equal("codex", root.GetProperty("provider").GetString());
        Assert.Equal("the answer", root.GetProperty("output_text").GetString());

        // output[0].content[0].text == answer (the path OpenAI clients read)
        var text = root.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Equal("the answer", text);

        // Missing native usage stays unavailable; subscription billing cannot be inferred.
        var usage = root.GetProperty("usage");
        Assert.Equal(JsonValueKind.Null, usage.ValueKind);
    }
}
