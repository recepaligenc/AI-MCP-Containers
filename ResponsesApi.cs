using System.Text;
using System.Text.Json;

namespace AiMcpContainers;

/// <summary>
/// Minimal OpenAI-"Responses API"–compatible surface so existing OpenAI clients (which POST to
/// /v1/responses with an `input` message array and read `output[].content[].text`) can target a
/// bridge instance unchanged — only the base URL differs. The instance's own provider
/// (codex or claude) actually serves the request; the request's `model` field is informational.
/// </summary>
public static class ResponsesApi
{
    /// <summary>
    /// Flatten an OpenAI Responses request body into a single prompt string.
    /// Accepts `input` as either a plain string or an array of {role, content} messages,
    /// where each content is itself a string or an array of {type, text} parts.
    /// developer/system messages are emitted first as instructions, then user/other content.
    /// Returns null when no usable text is found.
    /// </summary>
    public static string? BuildPrompt(JsonElement root)
    {
        if (!root.TryGetProperty("input", out var input))
            return null;

        // input: "plain string"
        if (input.ValueKind == JsonValueKind.String)
        {
            var s = input.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        if (input.ValueKind != JsonValueKind.Array)
            return null;

        var instructions = new List<string>();
        var body = new List<string>();

        foreach (var msg in input.EnumerateArray())
        {
            if (msg.ValueKind != JsonValueKind.Object) continue;

            var role = msg.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
                ? (r.GetString() ?? "user")
                : "user";

            var text = ExtractContent(msg);
            if (string.IsNullOrWhiteSpace(text)) continue;

            // developer/system steer the model; keep them up top as instructions.
            if (role is "developer" or "system")
                instructions.Add(text);
            else
                body.Add(text);
        }

        var sb = new StringBuilder();
        if (instructions.Count > 0)
            sb.Append(string.Join("\n\n", instructions));
        if (body.Count > 0)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(string.Join("\n\n", body));
        }

        var prompt = sb.ToString().Trim();
        return prompt.Length == 0 ? null : prompt;
    }

    private static string ExtractContent(JsonElement msg)
    {
        if (!msg.TryGetProperty("content", out var content))
            return "";

        // content: "plain string"
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";

        // content: [{ type, text }, ...]
        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.String)
                {
                    sb.Append(part.GetString());
                }
                else if (part.ValueKind == JsonValueKind.Object &&
                         part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    sb.Append(t.GetString());
                }
            }
            return sb.ToString();
        }

        return "";
    }

    /// <summary>
    /// Shape the agent's answer as an OpenAI Responses payload. The usage block reports zeros:
    /// bridge providers run on a subscription with no per-token billing, so zero cost is the
    /// honest value (the caller's cost tracker will price it at $0).
    /// </summary>
    public static object BuildResponse(string answer, string model, string provider) => new
    {
        id = "resp_bridge",
        @object = "response",
        model,
        provider,
        status = "completed",
        output = new object[]
        {
            new
            {
                type = "message",
                role = "assistant",
                content = new object[]
                {
                    new { type = "output_text", text = answer, annotations = Array.Empty<object>() }
                }
            }
        },
        output_text = answer,
        usage = new
        {
            input_tokens = 0,
            output_tokens = 0,
            total_tokens = 0,
            input_tokens_details = new { cached_tokens = 0 }
        }
    };
}
