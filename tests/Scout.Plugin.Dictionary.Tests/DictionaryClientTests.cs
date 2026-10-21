using System.Net;
using Scout.Plugin.Dictionary;
using Scout.Plugin.Dictionary.Tests.Fakes;

namespace Scout.Plugin.Dictionary.Tests;

/// <summary>
/// Networked client tests over a stub <see cref="HttpMessageHandler"/> (no real
/// sockets). Covers both external boundaries: dictionaryapi.dev (definitions) and
/// Datamuse (synonyms and spelling), including their failure modes required for
/// production readiness.
/// </summary>
public sealed class DictionaryClientTests
{
    private static (DefinitionsClient client, StubHttpMessageHandler handler) BuildDefinitions()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.dictionaryapi.dev/"),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return (new DefinitionsClient(http), handler);
    }

    private static (DatamuseClient client, StubHttpMessageHandler handler) BuildDatamuse()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.datamuse.com/"),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return (new DatamuseClient(http), handler);
    }

    // --- dictionaryapi.dev (definitions) boundary ----------------------------

    [Fact]
    public async Task Define_Sends_Expected_Headers_And_Path()
    {
        var (client, handler) = BuildDefinitions();
        handler.EnqueueJson(HttpStatusCode.OK, "[]");

        await client.DefineAsync("hello", CancellationToken.None);

        var req = handler.LastRequest.ShouldNotBeNull();
        req.Headers.UserAgent.ToString().ShouldContain("Scout");
        req.RequestUri!.ToString().ShouldContain("api/v2/entries/en/hello");
    }

    [Fact]
    public async Task Define_Parses_And_Flattens_Meanings()
    {
        var (client, handler) = BuildDefinitions();
        handler.EnqueueJson(HttpStatusCode.OK,
            """
            [{"word":"hello","meanings":[
              {"partOfSpeech":"noun","definitions":[{"definition":"A greeting.","example":"a warm hello"}]},
              {"partOfSpeech":"verb","definitions":[{"definition":"To greet."}]}
            ]}]
            """);

        var result = await client.DefineAsync("hello", CancellationToken.None);

        var definitions = result.ShouldNotBeNull();
        definitions.Count.ShouldBe(2);
        definitions[0].PartOfSpeech.ShouldBe("noun");
        definitions[0].Text.ShouldBe("A greeting.");
        definitions[0].Example.ShouldBe("a warm hello");
        definitions[1].PartOfSpeech.ShouldBe("verb");
        definitions[1].Example.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Define_NotFound_Returns_Null_Not_Throw()
    {
        var (client, handler) = BuildDefinitions();
        handler.EnqueueJson(HttpStatusCode.NotFound,
            """{"title":"No Definitions Found","message":"Sorry pal, we couldn't find definitions."}""");

        var result = await client.DefineAsync("qwertys", CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task Define_ServerError_Throws_Unavailable_Message()
    {
        var (client, handler) = BuildDefinitions();
        handler.EnqueueJson(HttpStatusCode.InternalServerError, "{}");

        var ex = await Should.ThrowAsync<DictionaryApiException>(
            () => client.DefineAsync("hello", CancellationToken.None));

        ex.Message.ShouldContain("unavailable");
        ex.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task Define_RateLimited_Throws_RateLimit_Message()
    {
        var (client, handler) = BuildDefinitions();
        handler.EnqueueJson(HttpStatusCode.TooManyRequests, "{}");

        var ex = await Should.ThrowAsync<DictionaryApiException>(
            () => client.DefineAsync("hello", CancellationToken.None));

        ex.Message.ShouldContain("rate limiting");
    }

    [Fact]
    public async Task Define_Malformed_Body_Throws_Unreadable_Message()
    {
        var (client, handler) = BuildDefinitions();
        handler.EnqueueJson(HttpStatusCode.OK, "{ this is not valid json");

        var ex = await Should.ThrowAsync<DictionaryApiException>(
            () => client.DefineAsync("hello", CancellationToken.None));

        ex.Message.ShouldContain("unreadable");
    }

    [Fact]
    public async Task Define_Timeout_Propagates_As_OperationCanceled()
    {
        var (client, handler) = BuildDefinitions();
        handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<TaskCanceledException>(
            () => client.DefineAsync("hello", CancellationToken.None));
    }

    [Fact]
    public async Task Define_Transport_Failure_Propagates_As_HttpRequestException()
    {
        var (client, handler) = BuildDefinitions();
        handler.EnqueueThrow(new HttpRequestException("connection reset"));

        await Should.ThrowAsync<HttpRequestException>(
            () => client.DefineAsync("hello", CancellationToken.None));
    }

    // --- Datamuse (synonyms + spelling) boundary -----------------------------

    [Fact]
    public async Task Synonyms_Sends_Expected_Path()
    {
        var (client, handler) = BuildDatamuse();
        handler.EnqueueJson(HttpStatusCode.OK, "[]");

        await client.GetSynonymsAsync("happy", CancellationToken.None);

        var req = handler.LastRequest.ShouldNotBeNull();
        req.Headers.UserAgent.ToString().ShouldContain("Scout");
        req.RequestUri!.ToString().ShouldContain("words?rel_syn=happy");
    }

    [Fact]
    public async Task Synonyms_Parses_Words_In_Order()
    {
        var (client, handler) = BuildDatamuse();
        handler.EnqueueJson(HttpStatusCode.OK,
            """[{"word":"glad","score":900},{"word":"joyful","score":800}]""");

        var synonyms = await client.GetSynonymsAsync("happy", CancellationToken.None);

        synonyms.ShouldBe(new[] { "glad", "joyful" });
    }

    [Fact]
    public async Task Synonyms_Empty_Array_Returns_Empty_List()
    {
        var (client, handler) = BuildDatamuse();
        handler.EnqueueJson(HttpStatusCode.OK, "[]");

        var synonyms = await client.GetSynonymsAsync("happy", CancellationToken.None);

        synonyms.ShouldBeEmpty();
    }

    [Fact]
    public async Task Spelling_Sends_Expected_Path()
    {
        var (client, handler) = BuildDatamuse();
        handler.EnqueueJson(HttpStatusCode.OK, "[]");

        await client.GetSpellingSuggestionsAsync("recieve", CancellationToken.None);

        handler.LastRequest!.RequestUri!.ToString().ShouldContain("words?sp=recieve");
    }

    [Fact]
    public async Task Spelling_Parses_Suggestions_In_Order()
    {
        var (client, handler) = BuildDatamuse();
        handler.EnqueueJson(HttpStatusCode.OK,
            """[{"word":"receive","score":900},{"word":"relieve","score":800}]""");

        var suggestions = await client.GetSpellingSuggestionsAsync("recieve", CancellationToken.None);

        suggestions.ShouldBe(new[] { "receive", "relieve" });
    }

    [Fact]
    public async Task Datamuse_ServerError_Throws_Unavailable_Message()
    {
        var (client, handler) = BuildDatamuse();
        handler.EnqueueJson(HttpStatusCode.InternalServerError, "{}");

        var ex = await Should.ThrowAsync<DictionaryApiException>(
            () => client.GetSynonymsAsync("happy", CancellationToken.None));

        ex.Message.ShouldContain("unavailable");
    }

    [Fact]
    public async Task Datamuse_Malformed_Body_Throws_Unreadable_Message()
    {
        var (client, handler) = BuildDatamuse();
        handler.EnqueueJson(HttpStatusCode.OK, "{ this is not valid json");

        var ex = await Should.ThrowAsync<DictionaryApiException>(
            () => client.GetSynonymsAsync("happy", CancellationToken.None));

        ex.Message.ShouldContain("unreadable");
    }

    [Fact]
    public async Task Datamuse_Timeout_Propagates_As_OperationCanceled()
    {
        var (client, handler) = BuildDatamuse();
        handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<TaskCanceledException>(
            () => client.GetSpellingSuggestionsAsync("recieve", CancellationToken.None));
    }

    [Fact]
    public async Task Datamuse_Transport_Failure_Propagates_As_HttpRequestException()
    {
        var (client, handler) = BuildDatamuse();
        handler.EnqueueThrow(new HttpRequestException("connection reset"));

        await Should.ThrowAsync<HttpRequestException>(
            () => client.GetSynonymsAsync("happy", CancellationToken.None));
    }
}
