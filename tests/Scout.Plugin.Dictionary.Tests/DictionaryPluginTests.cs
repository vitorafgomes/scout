using Scout.Plugin.Dictionary;
using Scout.Plugin.Dictionary.Abstractions;

namespace Scout.Plugin.Dictionary.Tests;

public sealed class DictionaryPluginTests
{
    private static IDefinitionsClient Definitions() => Substitute.For<IDefinitionsClient>();

    private static IDatamuseClient Datamuse() => Substitute.For<IDatamuseClient>();

    private static DictionaryPlugin Plugin(IDefinitionsClient definitions, IDatamuseClient datamuse)
        => new(definitions, datamuse);

    // --- define_word ---------------------------------------------------------

    [Fact]
    public async Task DefineWord_Formats_One_Per_Line_With_Example()
    {
        var definitions = Definitions();
        definitions.DefineAsync("hello", Arg.Any<CancellationToken>())
            .Returns(new List<WordDefinition>
            {
                new("noun", "A greeting.", "she gave a warm hello"),
                new("verb", "To greet with hello.", string.Empty),
            });

        var result = await Plugin(definitions, Datamuse())
            .DefineWordAsync("hello", CancellationToken.None);

        var lines = result.Split('\n');
        lines.Length.ShouldBe(2);
        lines[0].ShouldBe("noun: A greeting. (e.g. she gave a warm hello)");
        lines[1].ShouldBe("verb: To greet with hello.");
    }

    [Fact]
    public async Task DefineWord_Lowercases_Before_Lookup()
    {
        var definitions = Definitions();
        definitions.DefineAsync("hello", Arg.Any<CancellationToken>())
            .Returns(new List<WordDefinition> { new("noun", "A greeting.", string.Empty) });

        var result = await Plugin(definitions, Datamuse())
            .DefineWordAsync("Hello", CancellationToken.None);

        result.ShouldContain("A greeting.");
        await definitions.Received(1).DefineAsync("hello", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DefineWord_Unknown_Returns_Friendly_NotFound()
    {
        var definitions = Definitions();
        definitions.DefineAsync("qwertys", Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<WordDefinition>?)null);

        var result = await Plugin(definitions, Datamuse())
            .DefineWordAsync("qwertys", CancellationToken.None);

        result.ShouldBe("No definition found for 'qwertys'. English words only; for other languages try Wikipedia.");
    }

    [Fact]
    public async Task DefineWord_Empty_Entries_Returns_Friendly_NotFound()
    {
        var definitions = Definitions();
        definitions.DefineAsync("hello", Arg.Any<CancellationToken>())
            .Returns(new List<WordDefinition>());

        var result = await Plugin(definitions, Datamuse())
            .DefineWordAsync("hello", CancellationToken.None);

        result.ShouldContain("No definition found for 'hello'.");
    }

    [Fact]
    public async Task DefineWord_Empty_Input_Returns_Teaching()
    {
        var definitions = Definitions();

        var result = await Plugin(definitions, Datamuse())
            .DefineWordAsync("   ", CancellationToken.None);

        result.ShouldBe("Provide a word, for example \"serendipity\".");
        await definitions.DidNotReceiveWithAnyArgs().DefineAsync(default!, default);
    }

    [Fact]
    public async Task DefineWord_NonEnglish_Routes_To_Wikipedia()
    {
        var definitions = Definitions();

        // A word carrying non-ASCII (accented) letters is routed to Wikipedia
        // rather than looked up in the English-only dictionary.
        var result = await Plugin(definitions, Datamuse())
            .DefineWordAsync("café", CancellationToken.None);

        result.ShouldBe("Dictionary covers English words only. For 'café', try Wikipedia instead.");
        await definitions.DidNotReceiveWithAnyArgs().DefineAsync(default!, default);
    }

    [Fact]
    public async Task DefineWord_Junk_Input_Returns_Teaching()
    {
        var definitions = Definitions();

        var result = await Plugin(definitions, Datamuse())
            .DefineWordAsync("abc123", CancellationToken.None);

        result.ShouldBe("Provide a single English word using letters only, for example \"serendipity\".");
        await definitions.DidNotReceiveWithAnyArgs().DefineAsync(default!, default);
    }

    // --- get_synonyms --------------------------------------------------------

    [Fact]
    public async Task GetSynonyms_Formats_One_Per_Line()
    {
        var datamuse = Datamuse();
        datamuse.GetSynonymsAsync("happy", Arg.Any<CancellationToken>())
            .Returns(new List<string> { "glad", "joyful", "content" });

        var result = await Plugin(Definitions(), datamuse)
            .GetSynonymsAsync("happy", CancellationToken.None);

        result.Split('\n').ShouldBe(new[] { "glad", "joyful", "content" });
    }

    [Fact]
    public async Task GetSynonyms_None_Returns_Message()
    {
        var datamuse = Datamuse();
        datamuse.GetSynonymsAsync("happy", Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        var result = await Plugin(Definitions(), datamuse)
            .GetSynonymsAsync("happy", CancellationToken.None);

        result.ShouldBe("No synonyms found for 'happy'.");
    }

    [Fact]
    public async Task GetSynonyms_Empty_Input_Returns_Teaching()
    {
        var datamuse = Datamuse();

        var result = await Plugin(Definitions(), datamuse)
            .GetSynonymsAsync("", CancellationToken.None);

        result.ShouldBe("Provide a word, for example \"serendipity\".");
        await datamuse.DidNotReceiveWithAnyArgs().GetSynonymsAsync(default!, default);
    }

    // --- check_spelling ------------------------------------------------------

    [Fact]
    public async Task CheckSpelling_Correct_When_First_Suggestion_Matches()
    {
        var datamuse = Datamuse();
        datamuse.GetSpellingSuggestionsAsync("hello", Arg.Any<CancellationToken>())
            .Returns(new List<string> { "hello", "hallo", "hell" });

        var result = await Plugin(Definitions(), datamuse)
            .CheckSpellingAsync("hello", CancellationToken.None);

        result.ShouldBe("'hello' is spelled correctly.");
    }

    [Fact]
    public async Task CheckSpelling_Misspelled_Lists_Suggestions()
    {
        var datamuse = Datamuse();
        datamuse.GetSpellingSuggestionsAsync("recieve", Arg.Any<CancellationToken>())
            .Returns(new List<string> { "receive", "relieve", "reprieve" });

        var result = await Plugin(Definitions(), datamuse)
            .CheckSpellingAsync("recieve", CancellationToken.None);

        result.ShouldStartWith("'recieve' may be misspelled. Did you mean:\n");
        result.ShouldContain("receive");
        result.ShouldContain("relieve");
    }

    [Fact]
    public async Task CheckSpelling_No_Suggestions_Returns_Message()
    {
        var datamuse = Datamuse();
        datamuse.GetSpellingSuggestionsAsync("zzzzxq", Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        var result = await Plugin(Definitions(), datamuse)
            .CheckSpellingAsync("zzzzxq", CancellationToken.None);

        result.ShouldBe("No spelling suggestions found for 'zzzzxq'.");
    }

    [Fact]
    public async Task CheckSpelling_Preserves_Display_Case()
    {
        var datamuse = Datamuse();
        datamuse.GetSpellingSuggestionsAsync("hello", Arg.Any<CancellationToken>())
            .Returns(new List<string> { "hello" });

        var result = await Plugin(Definitions(), datamuse)
            .CheckSpellingAsync("Hello", CancellationToken.None);

        result.ShouldBe("'Hello' is spelled correctly.");
        await datamuse.Received(1).GetSpellingSuggestionsAsync("hello", Arg.Any<CancellationToken>());
    }

    // --- error handling (never throws into the kernel) -----------------------

    [Fact]
    public async Task DefineWord_ApiException_Returns_Clean_Error_Without_Throwing()
    {
        var definitions = Definitions();
        definitions.DefineAsync("hello", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<WordDefinition>?>(
                _ => throw new DictionaryApiException("The dictionary service is currently unavailable. Try again later."));

        var plugin = Plugin(definitions, Datamuse());

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.DefineWordAsync("hello", CancellationToken.None));

        result.ShouldBe("The dictionary service is currently unavailable. Try again later.");
    }

    [Fact]
    public async Task DefineWord_Timeout_Returns_Clean_Error_Without_Throwing()
    {
        var definitions = Definitions();
        definitions.DefineAsync("hello", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<WordDefinition>?>(_ => throw new TaskCanceledException("timeout"));

        var plugin = Plugin(definitions, Datamuse());

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.DefineWordAsync("hello", CancellationToken.None));

        result.ShouldBe("The dictionary request timed out. Try again later.");
    }

    [Fact]
    public async Task GetSynonyms_HttpRequestException_Returns_Clean_Error_Without_Throwing()
    {
        var datamuse = Datamuse();
        datamuse.GetSynonymsAsync("happy", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>>(_ => throw new HttpRequestException("connection reset"));

        var plugin = Plugin(Definitions(), datamuse);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.GetSynonymsAsync("happy", CancellationToken.None));

        result.ShouldBe("Could not reach the dictionary service. Try again later.");
    }

    [Fact]
    public async Task CheckSpelling_ApiException_Returns_Clean_Error_Without_Throwing()
    {
        var datamuse = Datamuse();
        datamuse.GetSpellingSuggestionsAsync("hello", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>>(
                _ => throw new DictionaryApiException("The thesaurus service is currently unavailable. Try again later."));

        var plugin = Plugin(Definitions(), datamuse);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.CheckSpellingAsync("hello", CancellationToken.None));

        result.ShouldBe("The thesaurus service is currently unavailable. Try again later.");
    }

    // --- constructor guards --------------------------------------------------

    [Fact]
    public void Ctor_Throws_When_Definitions_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new DictionaryPlugin(null!, Datamuse()));
    }

    [Fact]
    public void Ctor_Throws_When_Datamuse_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new DictionaryPlugin(Definitions(), null!));
    }
}
