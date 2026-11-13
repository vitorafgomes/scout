using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scout.Host.Console.Tests;

/// <summary>
/// Answers of Ollama's native chat endpoint, newline-delimited JSON over a stream that cannot seek: a streamed answer is
/// the message on one line and a closing done line; a one-reply answer is the message on its done line.
/// </summary>
internal static class OllamaWire
{
    public static JsonObject Text(string text) => new() { ["role"] = "assistant", ["content"] = text };

    public static JsonObject ToolCall(string name, string arguments) => new()
    {
        ["role"] = "assistant",
        ["content"] = string.Empty,
        ["tool_calls"] = new JsonArray(new JsonObject
        {
            ["function"] = new JsonObject { ["name"] = name, ["arguments"] = JsonNode.Parse(arguments) },
        }),
    };

    /// <summary>A tool name as the model sees it, <c>Plugin_function</c>, as <c>Plugin.function</c>.</summary>
    public static string Qualified(string toolName)
    {
        var separator = toolName.IndexOf('_', StringComparison.Ordinal);
        return $"{toolName[..separator]}.{toolName[(separator + 1)..]}";
    }

    /// <summary>The result a tool message carries: the client sends it as <c>{"CallId":...,"Result":...}</c>.</summary>
    public static string ToolResult(string content)
    {
        var result = JsonNode.Parse(content)!["Result"]!;
        return result.GetValueKind() == JsonValueKind.String ? result.GetValue<string>() : result.ToJsonString();
    }

    public static HttpResponseMessage Streamed(JsonObject message)
        => Lines(Line(message, done: false), Line(Text(string.Empty), done: true));

    public static HttpResponseMessage Reply(JsonObject message) => Lines(Line(message, done: true));

    private static string Line(JsonObject message, bool done)
    {
        var line = new JsonObject
        {
            ["model"] = "llama3.1",
            ["created_at"] = "2026-11-12T10:00:00Z",
            ["message"] = message,
            ["done"] = done,
        };
        if (done)
        {
            line["done_reason"] = "stop";
            line["prompt_eval_count"] = 10;
            line["eval_count"] = 5;
        }

        return line.ToJsonString();
    }

    private static HttpResponseMessage Lines(params string[] lines) => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(new NetworkLikeStream(Encoding.UTF8.GetBytes(string.Concat(lines.Select(line => line + "\n")))))
        {
            Headers = { { "Content-Type", "application/x-ndjson" } },
        },
    };

    private sealed class NetworkLikeStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
