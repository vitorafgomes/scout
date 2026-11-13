using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Scout.Llm.Tests.Providers.Ollama;

/// <summary>
/// Fake Ollama server under the real Ollama client: records every request (URI and JSON body) and answers each from a
/// script with the native API's newline-delimited JSON, one object per line, over a stream that cannot seek, or with an
/// error status and body.
/// </summary>
internal sealed class OllamaApiStub(Func<int, OllamaRequest, HttpResponseMessage> script) : HttpMessageHandler
{
    public List<OllamaRequest> Requests { get; } = [];

    /// <summary>A server that answers every request with the chunks, streamed or as one reply as the request asks.</summary>
    public static OllamaApiStub Answering(params string[] chunks)
        => new((_, request) => Answer(request, "llama3.1", chunks));

    public static HttpResponseMessage Answer(OllamaRequest request, string model, params string[] chunks)
        => request.Streamed ? Lines(TextLines(model, chunks)) : Lines([Done(model, string.Concat(chunks))]);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? new JsonObject()
            : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
        var recorded = new OllamaRequest(request.RequestUri!, body);
        Requests.Add(recorded);
        return script(Requests.Count, recorded);
    }

    public static HttpResponseMessage Lines(IEnumerable<string> lines)
        => new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NetworkLikeStream(Encoding.UTF8.GetBytes(
                string.Concat(lines.Select(line => line + "\n")))))
            {
                Headers = { { "Content-Type", "application/x-ndjson" } },
            },
        };

    public static HttpResponseMessage Error(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static IEnumerable<string> TextLines(string model, IEnumerable<string> chunks, int? prompt = 10, int? eval = 5)
        =>
        [
            .. chunks.Select(chunk => Line(model, new JsonObject { ["role"] = "assistant", ["content"] = chunk }, done: false)),
            Done(model, string.Empty, prompt, eval),
        ];

    public static IEnumerable<string> ToolCallLines(string model, string name, JsonObject arguments, int prompt = 20, int eval = 3)
        =>
        [
            Line(
                model,
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = string.Empty,
                    ["tool_calls"] = new JsonArray(new JsonObject
                    {
                        ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments },
                    }),
                },
                done: false),
            Done(model, string.Empty, prompt, eval),
        ];

    public static string Done(string model, string content, int? prompt = 10, int? eval = 5)
    {
        var line = JsonNode.Parse(Line(model, new JsonObject { ["role"] = "assistant", ["content"] = content }, done: true))!.AsObject();
        line["done_reason"] = "stop";
        if (prompt is { } promptCount)
        {
            line["prompt_eval_count"] = promptCount;
        }

        if (eval is { } evalCount)
        {
            line["eval_count"] = evalCount;
        }

        return line.ToJsonString();
    }

    private static string Line(string model, JsonObject message, bool done) => new JsonObject
    {
        ["model"] = model,
        ["created_at"] = "2026-11-12T10:00:00Z",
        ["message"] = message,
        ["done"] = done,
    }.ToJsonString();

    private sealed class NetworkLikeStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}

internal sealed record OllamaRequest(Uri Uri, JsonObject Body)
{
    public bool Streamed => Body["stream"]?.GetValue<bool>() ?? false;

    public int? ContextLength => Body["options"]?["num_ctx"]?.GetValue<int>();

    public string Messages => Body["messages"]?.ToJsonString() ?? string.Empty;
}
