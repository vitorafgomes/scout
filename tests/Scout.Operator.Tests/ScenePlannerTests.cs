using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Operator.Tests.Fakes;

namespace Scout.Operator.Tests;

public sealed class ScenePlannerTests
{
    private static OperatorScript Script(params string[] sections)
        => new(
            "Bread in ten minutes?",
            [.. sections.Select((n, i) => new OperatorScriptSection($"Part {i + 1}", n))],
            "Subscribe for the next loaf.",
            ["A", "B", "C"],
            "How to bake.",
            [],
            []);

    private static string Sentences(int count, int length)
        => string.Join(" ", Enumerable.Range(1, count).Select(i => $"S{i:00} " + new string('x', length - 5) + "."));

    [Fact]
    public async Task The_Hook_Comes_First_Each_Section_Follows_In_Order_And_The_Call_To_Action_Comes_Last()
    {
        var chat = ScriptedChat.Answering(Given.Prompts(4));

        var scenes = await new ScenePlanner(chat).PlanAsync(
            Script("Feed the starter daily.", "Bake at 250 degrees."), Given.Mandate(), string.Empty, CancellationToken.None);

        scenes.Select(s => s.Narration).ShouldBe([
            "Bread in ten minutes?", "Feed the starter daily.", "Bake at 250 degrees.", "Subscribe for the next loaf.",
        ]);
        scenes.Select(s => (s.Id, s.Order, s.VisualPrompt)).ShouldBe([
            ("s1", 1, "Picture 1"), ("s2", 2, "Picture 2"), ("s3", 3, "Picture 3"), ("s4", 4, "Picture 4"),
        ]);
        scenes.ShouldAllBe(s => s.Status == "planned" && !s.Locked && s.AudioSource == "none"
            && s.VisualSource == "text_card" && s.AudioFile == null && s.VisualFile == null && s.Error == null);
    }

    [Fact]
    public async Task A_Section_Longer_Than_700_Characters_Is_Split_At_Sentence_Boundaries_Into_Scenes_Of_At_Most_700()
    {
        var longSection = Sentences(6, 200);
        var chat = ScriptedChat.Answering(Given.Prompts(4), Given.Prompts(3));

        var scenes = await new ScenePlanner(chat).PlanAsync(Script(longSection), Given.Mandate(), string.Empty, CancellationToken.None);

        var parts = scenes.Skip(1).SkipLast(1).Select(s => s.Narration).ToList();
        parts.Count.ShouldBe(2);
        parts.ShouldAllBe(p => p.Length <= 700 && p.EndsWith('.'));
        string.Join(" ", parts).ShouldBe(longSection);
        parts[0].ShouldStartWith("S01 ");
        parts[1].ShouldStartWith("S04 ");
    }

    [Fact]
    public async Task The_Model_Is_Asked_Once_For_Every_Scenes_Prompt_In_The_Mandates_Voice_Without_Real_People_Or_Logos()
    {
        var chat = ScriptedChat.Answering(Given.Prompts(3));

        await new ScenePlanner(chat).PlanAsync(Script("Feed the starter daily."), Given.Mandate(), string.Empty, CancellationToken.None);

        chat.Requests.ShouldHaveSingleItem();
        var system = chat.Requests[0].Single(m => m.Role == AuthorRole.System).Text;
        system.ShouldContain("warm and precise");
        system.ShouldContain("Never ask for a real person's likeness");
        system.ShouldContain("logo");
        system.ShouldContain("exactly 3 prompts");
        chat.Offers.ShouldHaveSingleItem().ShouldBe((null, null));
        chat.Requests[0].Single(m => m.Role == AuthorRole.User).Text.ShouldContain("2. Feed the starter daily.");
    }

    [Fact]
    public async Task The_planner_asks_for_search_terms_per_scene()
    {
        var chat = ScriptedChat.Answering(
            "{\"prompts\":[\"Picture 1\",\"Picture 2\",\"Picture 3\"],\"search_terms\":[\"fresh \\\"bread\\\" loaf\",\"  \"]}");

        var scenes = await new ScenePlanner(chat).PlanAsync(Script("Feed the starter daily."), Given.Mandate(), string.Empty, CancellationToken.None);

        var system = chat.Requests.ShouldHaveSingleItem().Single(m => m.Role == AuthorRole.System).Text;
        system.ShouldContain("\"search_terms\"");
        system.ShouldContain("2 to 4 plain English nouns or adjectives");
        scenes.Select(s => s.VisualQuery).ShouldBe(["fresh bread loaf", string.Empty, string.Empty]);
    }

    [Fact]
    public async Task The_brief_shapes_the_picture_prompts_and_search_terms()
    {
        var briefed = ScriptedChat.Answering(Given.Prompts(3));
        var plain = ScriptedChat.Answering(Given.Prompts(3));

        await new ScenePlanner(briefed).PlanAsync(
            Script("Feed the starter daily."), Given.Mandate(), "  Hand-drawn watercolour, soft pastel light.\nSlow and calm.  ", CancellationToken.None);
        await new ScenePlanner(plain).PlanAsync(Script("Feed the starter daily."), Given.Mandate(), "   ", CancellationToken.None);

        var system = briefed.Requests.ShouldHaveSingleItem().Single(m => m.Role == AuthorRole.System).Text!;
        system.ShouldContain(
            "Visual style the owner asked for: Hand-drawn watercolour, soft pastel light.\nSlow and calm.\n" +
            "Write every prompt and every entry of \"search_terms\" in that style.",
            Case.Sensitive);
        system.IndexOf("Visual style the owner asked for", StringComparison.Ordinal)
            .ShouldBeGreaterThan(system.IndexOf("warm and precise", StringComparison.Ordinal));
        var unbriefed = plain.Requests.ShouldHaveSingleItem().Single(m => m.Role == AuthorRole.System).Text!;
        unbriefed.ShouldNotContain("Visual style", Case.Sensitive);
        unbriefed.ShouldNotContain("in that style", Case.Sensitive);
        unbriefed.ShouldContain("warm and precise");
    }

    [Fact]
    public async Task A_Prompt_List_Of_The_Wrong_Length_Is_Asked_For_Once_More_And_A_Second_One_Fails_The_Plan()
    {
        var retried = ScriptedChat.Answering(Given.Prompts(4), Given.Prompts(3));
        var twice = ScriptedChat.Answering(Given.Prompts(2), "not json");

        var scenes = await new ScenePlanner(retried).PlanAsync(Script("Feed it."), Given.Mandate(), string.Empty, CancellationToken.None);
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => new ScenePlanner(twice).PlanAsync(Script("Feed it."), Given.Mandate(), string.Empty, CancellationToken.None));

        scenes.Count.ShouldBe(3);
        retried.Requests.Count.ShouldBe(2);
        retried.Requests[1].Last().Text.ShouldBe(ModelJson.Retry);
        ex.Message.ShouldBe("The AI model did not return a picture prompt for every scene in the expected form, twice.");
    }
}
