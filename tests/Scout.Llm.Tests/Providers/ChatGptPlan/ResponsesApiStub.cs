using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Scout.Llm.Tests.Providers.ChatGptPlan;

/// <summary>
/// Fake HTTP handler under the real ResponsesClient: records every request
/// (path, Authorization header, JSON body) and answers each from a script.
/// </summary>
internal sealed class ResponsesApiStub : HttpMessageHandler
{
    private readonly Func<int, RecordedRequest, HttpResponseMessage> _script;

    public ResponsesApiStub(Func<int, RecordedRequest, HttpResponseMessage> script)
    {
        _script = script;
    }

    public List<RecordedRequest> Requests { get; } = [];

    public static ResponsesApiStub Answering(string text)
        => new((_, _) => Sse(TextEvents(text)));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? new JsonObject()
            : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
        var recorded = new RecordedRequest(
            request.RequestUri!.AbsolutePath,
            request.Headers.Authorization?.ToString(),
            body);
        Requests.Add(recorded);
        return _script(Requests.Count, recorded);
    }

    public static HttpResponseMessage Sse(IEnumerable<string> events)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                string.Concat(events.Select(e => $"data: {e}\n\n")),
                Encoding.UTF8,
                "text/event-stream"),
        };

    public static HttpResponseMessage Error(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static IEnumerable<string> TextEvents(string text) =>
    [
        $$"""{"type":"response.output_text.delta","sequence_number":1,"item_id":"msg_1","output_index":0,"content_index":0,"delta":{{JsonValue.Create(text).ToJsonString()}}}""",
        Completed("[]"),
    ];

    public static IEnumerable<string> FunctionCallEvents(string name)
    {
        var item = $$"""{"type":"function_call","id":"fc_1","call_id":"call_1","name":{{JsonValue.Create(name).ToJsonString()}},"arguments":"{}","status":"completed"}""";
        return
        [
            $$"""{"type":"response.output_item.done","sequence_number":1,"output_index":0,"item":{{item}}}""",
            Completed($"[{item}]"),
        ];
    }

    public static IEnumerable<string> ReasoningThenFunctionCallEvents(string name, string encryptedContent)
    {
        var reasoning = $$"""{"type":"reasoning","id":"rs_1","summary":[],"encrypted_content":{{JsonValue.Create(encryptedContent).ToJsonString()}}}""";
        var call = $$"""{"type":"function_call","id":"fc_1","call_id":"call_1","name":{{JsonValue.Create(name).ToJsonString()}},"arguments":"{}","status":"completed"}""";
        return
        [
            $$"""{"type":"response.output_item.done","sequence_number":1,"output_index":0,"item":{{reasoning}}}""",
            $$"""{"type":"response.output_item.done","sequence_number":2,"output_index":1,"item":{{call}}}""",
            Completed($"[{reasoning},{call}]"),
        ];
    }

    private static string Completed(string output) =>
        $$$"""{"type":"response.completed","sequence_number":2,"response":{"id":"resp_1","object":"response","created_at":1700000000,"status":"completed","model":"gpt-5.6-luna","output":{{{output}}},"parallel_tool_calls":true,"tool_choice":"auto","tools":[]}}""";
}

internal sealed record RecordedRequest(string Path, string? Authorization, JsonObject Body);
