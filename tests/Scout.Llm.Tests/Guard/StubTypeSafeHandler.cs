using System.Net;
using System.Text;

namespace Scout.Llm.Tests.Guard;

internal sealed class StubTypeSafeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    : HttpMessageHandler
{
    public HttpRequestMessage? Request { get; private set; }

    public string? Body { get; private set; }

    public int Calls { get; private set; }

    public static StubTypeSafeHandler Answering(double noul) =>
        new((_, _) => Task.FromResult(Noul(noul)));

    public static HttpResponseMessage Noul(double noul) =>
        Json(
            HttpStatusCode.OK,
            $$$"""{"model":"jev-latest","answers":{"executive_write":{"type":"noul","noul":{{{noul.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}}},"usage":{"input_tokens":110,"output_tokens":10}}""");

    public static StubTypeSafeHandler Failing(HttpStatusCode status, string body) =>
        new((_, _) => Task.FromResult(Json(status, body)));

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Calls++;
        Request = request;
        Body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return await responder(request, cancellationToken).ConfigureAwait(false);
    }
}
