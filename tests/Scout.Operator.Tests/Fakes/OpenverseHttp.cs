using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace Scout.Operator.Tests.Fakes;

/// <summary>
/// An Openverse search and the picture hosts it names, answered in process: <see cref="Search"/> answers the search,
/// <see cref="Picture"/> every other URL, and every request is recorded. A primary handler follows no redirect, so a
/// redirected picture is answered here as its final response.
/// </summary>
internal sealed class OpenverseHttp : HttpMessageHandler
{
    public const string PictureUrl = "https://live.staticflickr.com/65535/lake_b.jpg";

    public Func<HttpRequestMessage, HttpResponseMessage> Search { get; set; } = _ => Json(Results(Result()));

    public Func<HttpRequestMessage, HttpResponseMessage> Picture { get; set; } = _ => Image();

    public List<HttpRequestMessage> Requests { get; } = [];

    public TimeProvider? Clock { get; set; }

    public List<DateTimeOffset> Starts { get; } = [];

    public IHttpClientFactory Factory()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(OpenversePictures.ClientName).Returns(_ => new HttpClient(this, disposeHandler: false));
        return factory;
    }

    public static byte[] PictureBytes { get; } = [.. Enumerable.Range(0, 2048).Select(i => (byte)i)];

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Image(string mediaType = "image/jpeg")
    {
        var content = new ByteArrayContent(PictureBytes);
        content.Headers.ContentType = new(mediaType);
        return new(HttpStatusCode.OK) { Content = content };
    }

    public static string Results(params string[] results) => $"{{\"result_count\":{results.Length},\"results\":[{string.Join(",", results)}]}}";

    public static string Result(
        string license = "by",
        string url = PictureUrl,
        string? filetype = null,
        int? width = 1024,
        string? attribution = "\"Mountain lake\" by Thomas Nes Myhre is licensed under CC BY 2.0.",
        string title = "Mountain lake",
        string creator = "Thomas Nes Myhre")
        => "{" +
           $"\"title\":\"{title}\",\"url\":\"{url}\",\"creator\":\"{creator}\",\"license\":\"{license}\",\"license_version\":\"2.0\"," +
           $"\"filetype\":{(filetype is null ? "null" : $"\"{filetype}\"")},\"width\":{(width is null ? "null" : width.ToString())},\"height\":683," +
           $"\"attribution\":{(attribution is null ? "null" : System.Text.Json.JsonSerializer.Serialize(attribution))}" +
           "}";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Starts.Add(Clock?.GetUtcNow() ?? default);
        var response = request.RequestUri!.Host == "api.openverse.org" ? Search(request) : Picture(request);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}

/// <summary>A fake clock whose timers fire at once by moving it forward by their delay, so a wait costs no real time.</summary>
internal sealed class SteppingTime(DateTimeOffset start) : FakeTimeProvider(start)
{
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        if (dueTime > TimeSpan.Zero)
        {
            Advance(dueTime);
        }

        return timer;
    }
}
