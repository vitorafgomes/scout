using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Operator.Tests.Fakes;

namespace Scout.Operator.Tests;

public sealed class ScriptWriterTests
{
    private const string Read = "https://www.youtube.com/watch?v=found000001";

    private static readonly TopicResearch Research = new(
        [new OperatorTopicCandidate("Sourdough basics", "fits", 0.9)],
        [new ResearchSource(Read, "Sourdough in 10 minutes", "Bread Lab")]);

    [Fact]
    public async Task Claims_Are_Capped_At_25_And_Every_Claim_Starts_Pending_With_Its_Own_Id()
    {
        var claims = "[" + string.Join(",", Enumerable.Range(1, 30).Select(i => $"{{\"text\":\"Fact {i}\",\"risk\":\"standard\",\"sourceUrls\":[]}}")) + "]";
        var chat = ScriptedChat.Answering(Given.Script(claims));

        var draft = await new ScriptWriter(chat).WriteAsync("sourdough", Given.Mandate(), Research, CancellationToken.None);

        draft.Claims.Count.ShouldBe(25);
        draft.Claims.ShouldAllBe(c => c.Status == "pending");
        draft.Claims.Select(c => c.Id).ShouldBe(Enumerable.Range(1, 25).Select(i => $"c{i}"));
        draft.Claims[^1].Text.ShouldBe("Fact 25");
    }

    [Fact]
    public async Task A_Claim_Keeps_Only_The_Source_Links_The_Research_Read()
    {
        var chat = ScriptedChat.Answering(Given.Script(
            $$"""[{"text":"Starters need daily feeding","risk":"standard","sourceUrls":["{{Read}}","https://example.com/made-up"]}]"""));

        var draft = await new ScriptWriter(chat).WriteAsync("sourdough", Given.Mandate(), Research, CancellationToken.None);

        draft.Claims.ShouldHaveSingleItem().SourceUrls.ShouldBe([Read]);
    }

    [Theory]
    [InlineData("HIGH", "high")]
    [InlineData("medium", "standard")]
    public async Task A_Claims_Risk_Is_High_Or_Standard(string answered, string kept)
    {
        var chat = ScriptedChat.Answering(Given.Script($$"""[{"text":"Raw flour is safe","risk":"{{answered}}"}]"""));

        var draft = await new ScriptWriter(chat).WriteAsync("sourdough", Given.Mandate(), Research, CancellationToken.None);

        draft.Claims.ShouldHaveSingleItem().Risk.ShouldBe(kept);
    }

    [Fact]
    public async Task The_Script_Is_Asked_For_In_The_Mandates_Language()
    {
        var chat = ScriptedChat.Answering(Given.Script());

        await new ScriptWriter(chat).WriteAsync("sourdough", Given.Mandate(language: "Portuguese"), Research, CancellationToken.None);

        var system = chat.Requests.ShouldHaveSingleItem().First();
        system.Role.ShouldBe(AuthorRole.System);
        system.Text.ShouldContain("Write every text in Portuguese.");
    }

    [Fact]
    public async Task The_Script_Keeps_The_Models_Hook_Sections_Titles_And_Chapters()
    {
        var chat = ScriptedChat.Answering(Given.Script());

        var draft = await new ScriptWriter(chat).WriteAsync("sourdough", Given.Mandate(), Research, CancellationToken.None);

        draft.Script.Hook.ShouldBe("Bread in ten minutes?");
        draft.Script.Sections.ShouldBe([new OperatorScriptSection("Starter", "Feed it daily.")]);
        draft.Script.TitleVariants.ShouldBe(["A", "B", "C"]);
        draft.Script.Chapters.ShouldBe([new OperatorChapter("0:00", "Intro")]);
    }

    [Fact]
    public async Task A_Script_With_Fewer_Than_Three_Titles_Is_Asked_Again_And_Two_Such_Answers_Fail_With_A_Readable_Error()
    {
        var twoTitles = Given.Script().Replace("[\"A\",\"B\",\"C\"]", "[\"A\",\"B\"]", StringComparison.Ordinal);
        var chat = ScriptedChat.Answering(twoTitles, twoTitles);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => new ScriptWriter(chat)
            .WriteAsync("sourdough", Given.Mandate(), Research, CancellationToken.None));

        ex.Message.ShouldBe("The AI model did not return a script in the expected form, twice.");
        chat.Requests.Count.ShouldBe(2);
    }
}
