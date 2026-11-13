using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Scout.Llm.Tests.Providers.Anthropic;

/// <summary>
/// Fake HTTP handler under the real Anthropic client: records every request (URI, headers, JSON body) and answers
/// each from a script with Claude API server-sent events or an error.
/// </summary>
internal sealed class ClaudeApiStub(Func<int, ClaudeRequest, HttpResponseMessage> script) : HttpMessageHandler
{
    public List<ClaudeRequest> Requests { get; } = [];

    public static ClaudeApiStub Answering(params string[] chunks)
        => new((_, _) => Sse(TextEvents("claude-opus-5-5", chunks)));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? new JsonObject()
            : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
        var recorded = new ClaudeRequest(
            request.RequestUri!,
            request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value)),
            body);
        Requests.Add(recorded);
        return script(Requests.Count, recorded);
    }

    public static HttpResponseMessage Sse(IEnumerable<string> events)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                string.Concat(events.Select(e => $"event: {JsonNode.Parse(e)!["type"]!.GetValue<string>()}\ndata: {e}\n\n")),
                Encoding.UTF8,
                "text/event-stream"),
        };

    public static HttpResponseMessage Error(HttpStatusCode status, string type, string message = "refused", string? errorCode = null)
    {
        var error = new JsonObject { ["type"] = type, ["message"] = message };
        if (errorCode is not null)
        {
            error["details"] = new JsonObject { ["error_code"] = errorCode };
        }

        var body = new JsonObject
        {
            ["type"] = "error",
            ["error"] = error,
        };
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        response.Headers.Add("retry-after-ms", "0");
        return response;
    }

    public static HttpResponseMessage Message(string model, string text)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                new JsonObject
                {
                    ["id"] = "msg_1",
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["model"] = model,
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                    ["stop_reason"] = "end_turn",
                    ["stop_sequence"] = null,
                    ["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 },
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json"),
        };

    public static IEnumerable<string> TextEvents(string model, string[] chunks, int input = 10, int output = 5)
        =>
        [
            Start(model, input),
            """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
            .. chunks.Select(chunk => new JsonObject
            {
                ["type"] = "content_block_delta",
                ["index"] = 0,
                ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = chunk },
            }.ToJsonString()),
            """{"type":"content_block_stop","index":0}""",
            End("end_turn", output),
            """{"type":"message_stop"}""",
        ];

    public static IEnumerable<string> ToolUseEvents(string model, string name, int input = 20, int output = 3)
        =>
        [
            Start(model, input),
            new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = 0,
                ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_1", ["name"] = name, ["input"] = new JsonObject() },
            }.ToJsonString(),
            """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{}"}}""",
            """{"type":"content_block_stop","index":0}""",
            End("tool_use", output),
            """{"type":"message_stop"}""",
        ];

    private static string Start(string model, int input) => new JsonObject
    {
        ["type"] = "message_start",
        ["message"] = new JsonObject
        {
            ["id"] = "msg_1",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = model,
            ["content"] = new JsonArray(),
            ["stop_reason"] = null,
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject { ["input_tokens"] = input, ["output_tokens"] = 1 },
        },
    }.ToJsonString();

    private static string End(string stopReason, int output) => new JsonObject
    {
        ["type"] = "message_delta",
        ["delta"] = new JsonObject { ["stop_reason"] = stopReason, ["stop_sequence"] = null },
        ["usage"] = new JsonObject { ["output_tokens"] = output },
    }.ToJsonString();
}

internal sealed record ClaudeRequest(Uri Uri, IReadOnlyDictionary<string, string> Headers, JsonObject Body);
