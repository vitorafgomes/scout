using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Scout.Llm.Configuration;
using Scout.Llm.Guard;

namespace Scout.Llm.Tests.Guard;

public sealed class TypeSafeClientTests : IDisposable
{
    private const string Key = "ts-secret-key-must-not-leak";

    private readonly List<HttpClient> _clients = [];

    public void Dispose() => _clients.ForEach(client => client.Dispose());

    private TypeSafeClient Client(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        var http = new HttpClient(handler, disposeHandler: false);
        _clients.Add(http);
        if (timeout is { } t)
        {
            http.Timeout = t;
        }

        return new TypeSafeClient(http, Options.Create(new TypeSafeGuardOptions { ApiKey = Key }));
    }

    [Fact]
    public async Task Asks_Jev_The_Noul_Question_With_The_Bearer_Key_And_Returns_The_Probability()
    {
        using var handler = StubTypeSafeHandler.Answering(0.95);

        var p = await Client(handler).AskNoulAsync(
            "delete the prod database", "instr", "yes-case", "no-case", CancellationToken.None);

        p.ShouldBe(0.95);
        handler.Request!.Method.ShouldBe(HttpMethod.Post);
        handler.Request.RequestUri.ShouldBe(new Uri("https://api.typesafe.ai/v1/systemone"));
        handler.Request.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        handler.Request.Headers.Authorization.Parameter.ShouldBe(Key);

        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        root.GetProperty("model").GetString().ShouldBe("jev-latest");
        root.GetProperty("state").GetString().ShouldBe("delete the prod database");
        var question = root.GetProperty("questions").GetProperty("executive_write");
        question.GetProperty("type").GetString().ShouldBe("noul");
        question.GetProperty("instructions").GetString().ShouldBe("instr");
        question.GetProperty("criteria").GetProperty("true").GetString().ShouldBe("yes-case");
        question.GetProperty("criteria").GetProperty("false").GetString().ShouldBe("no-case");
    }

    [Fact]
    public async Task A_422_Names_The_Status_And_The_Provider_Detail_But_Never_The_Key()
    {
        using var handler = StubTypeSafeHandler.Failing(
            HttpStatusCode.UnprocessableEntity,
            $$"""{"detail":[{"loc":["body","state"],"msg":"Field required","input":"Bearer {{Key}}"}]}""");

        var ex = await Should.ThrowAsync<TypeSafeException>(() =>
            Client(handler).AskNoulAsync("x", "i", "t", "f", CancellationToken.None));

        ex.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        ex.Message.ShouldContain("422");
        ex.Message.ShouldContain("Field required");
        ex.Message.ShouldNotContain(Key);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_Failed_Status_Names_The_Status_But_Never_The_Key(HttpStatusCode status)
    {
        using var handler = StubTypeSafeHandler.Failing(status, $$"""{"detail":"bad key {{Key}}"}""");

        var ex = await Should.ThrowAsync<TypeSafeException>(() =>
            Client(handler).AskNoulAsync("x", "i", "t", "f", CancellationToken.None));

        ex.StatusCode.ShouldBe(status);
        ex.Message.ShouldContain(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture));
        ex.Message.ShouldNotContain(Key);
    }

    [Fact]
    public async Task A_Timeout_Raises_A_TypeSafe_Error_Without_The_Key()
    {
        using var handler = new StubTypeSafeHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var ex = await Should.ThrowAsync<TypeSafeException>(() =>
            Client(handler, TimeSpan.FromMilliseconds(50))
                .AskNoulAsync("x", "i", "t", "f", CancellationToken.None));

        ex.Message.ShouldBe("TypeSafe did not answer in time.");
    }

    [Fact]
    public async Task A_Connection_Reset_Raises_A_TypeSafe_Error_Without_The_Key()
    {
        using var handler = new StubTypeSafeHandler((_, _) =>
            throw new HttpRequestException("connection reset"));

        var ex = await Should.ThrowAsync<TypeSafeException>(() =>
            Client(handler).AskNoulAsync("x", "i", "t", "f", CancellationToken.None));

        ex.Message.ShouldBe("TypeSafe could not be reached.");
    }
}
