using System.Text;
using System.Text.Json;

namespace AiMcpContainers;

/// <summary>
/// Minimal OpenAI-"Responses API"–compatible surface so existing OpenAI clients (which POST to
/// /v1/responses with an `input` message array and read `output[].content[].text`) can target a
/// bridge instance using the documented text-only subset. The instance's own provider
/// (codex or claude) actually serves the request; the request's `model` field is informational.
/// </summary>
public static class ResponsesApi
{
    private static readonly HashSet<string> SupportedFields = new(StringComparer.Ordinal)
        { "input", "instructions", "model", "stream", "tools", "previous_response_id" };

    public static ErrorResponse? ValidateRequest(JsonElement root)
    {
        ErrorResponse Bad(string message, string code = "bad_request") => new() { Error = message, Code = code };
        if (root.ValueKind != JsonValueKind.Object)
            return Bad("Request body must be a JSON object.", "bad_json_type");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
            if (!names.Add(field.Name))
                return Bad("Duplicate request fields are not supported.");
        if (names.Any(name => !SupportedFields.Contains(name)))
            return Bad("Request contains an unsupported parameter.", "unsupported_parameter");

        // Check all types before rejecting active unsupported features.
        foreach (var field in root.EnumerateObject())
        {
            var kind = field.Value.ValueKind;
            var valid = field.Name switch
            {
                "input" => kind is JsonValueKind.String or JsonValueKind.Array or JsonValueKind.Null,
                "instructions" or "model" or "previous_response_id" => kind is JsonValueKind.String or JsonValueKind.Null,
                "stream" => kind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null,
                "tools" => kind is JsonValueKind.Array or JsonValueKind.Null,
                _ => false,
            };
            if (!valid) return Bad($"Field '{field.Name}' has an invalid type.");
        }
        if (root.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True)
            return Bad("Field 'stream' is unsupported when true.", "unsupported_parameter");
        if (root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0)
            return Bad("Field 'tools' must be empty; tool execution is unsupported.", "unsupported_parameter");
        if (root.TryGetProperty("previous_response_id", out var previous) && previous.ValueKind == JsonValueKind.String
            && previous.GetString()!.Length > 0)
            return Bad("Field 'previous_response_id' must be empty; response chaining is unsupported.", "unsupported_parameter");
        return null;
    }

    /// <summary>
    /// Flatten an OpenAI Responses request body into a single prompt string.
    /// Accepts `input` as either a plain string or an array of {role, content} messages,
    /// where each content is itself a string or an array of {type, text} parts.
    /// developer/system messages are emitted first as instructions, then user/other content.
    /// Returns null when no usable text is found.
    /// </summary>
    public static string? BuildPrompt(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var input = BuildInputPrompt(root);
        if (string.IsNullOrWhiteSpace(input)) return null;
        if (root.TryGetProperty("instructions", out var instructions) && instructions.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(instructions.GetString()))
            return instructions.GetString() + "\n\n" + input;
        return input;
    }

    private static string? BuildInputPrompt(JsonElement root)
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

    /// <summary>Native usage is optional; subscription credits cannot be inferred from tokens.</summary>
    public static object BuildResponse(string answer, string model, string provider, ProviderUsage? providerUsage = null) => new
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
        usage = NativeUsage.ToResponsesUsage(providerUsage),
        provider_usage = providerUsage
    };
}
