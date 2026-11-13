using Shouldly;
using Xunit;

namespace Scout.Skills.Tests;

public sealed class SkillPromptComposerTests
{
    private static Skill Make(string name, string body)
        => new(name, name + " description", body, $"/skills/{name}.md");

    [Fact]
    public void Compose_Returns_Base_Unchanged_When_No_Skills()
    {
        var result = SkillPromptComposer.Compose("base prompt", []);

        result.ShouldBe("base prompt");
    }

    [Fact]
    public void Compose_Appends_Single_Skill_Body_After_Base()
    {
        var result = SkillPromptComposer.Compose(
            "base prompt",
            [Make("debugging", "debug carefully")]);

        result.ShouldStartWith("base prompt");
        result.ShouldContain("# Relevant skill: debugging");
        result.ShouldContain("debug carefully");
        result.IndexOf("base prompt", StringComparison.Ordinal)
            .ShouldBeLessThan(result.IndexOf("debug carefully", StringComparison.Ordinal));
    }

    [Fact]
    public void Compose_Appends_Each_Skill_In_Order()
    {
        var result = SkillPromptComposer.Compose(
            "base",
            [Make("a", "alpha body"), Make("b", "beta body")]);

        result.ShouldContain("# Relevant skill: a");
        result.ShouldContain("# Relevant skill: b");
        result.IndexOf("alpha body", StringComparison.Ordinal)
            .ShouldBeLessThan(result.IndexOf("beta body", StringComparison.Ordinal));
    }

    private static Skill Described(string name, string description)
        => new(name, description, name + " body", $"/skills/{name}.md");

    [Fact]
    public void Without_Auto_Selection_The_Prompt_Ends_With_The_Skills_Section_One_Line_Per_Skill()
    {
        var result = SkillPromptComposer.Compose(
            "base",
            [],
            [
                Described("code-audit", "Auditing a whole project - \"audit\"."),
                Described("code-review", "Reviewing a pull request - \"review PR 42\". Reads the diff."),
                Described("go", "Answering Go questions. Not Rust."),
            ]);

        result.ShouldBe(
            "base\n\n# Skills\n"
            + "Before answering, if the request matches one of these skills, call load_skill with its name and follow it; "
            + "for a code review of a pull request use code-review, for a review or audit of a whole repository or project use code-audit.\n"
            + "- code-audit: Auditing a whole project\n"
            + "- code-review: Reviewing a pull request\n"
            + "- go: Answering Go questions");
    }

    [Theory]
    [InlineData("go", "rust", "Before answering, if the request matches one of these skills, call load_skill with its name and follow it.")]
    [InlineData("code-review", "go", "Before answering, if the request matches one of these skills, call load_skill with its name and follow it; for a code review of a pull request use code-review.")]
    [InlineData("code-audit", "go", "Before answering, if the request matches one of these skills, call load_skill with its name and follow it; for a review or audit of a whole repository or project use code-audit.")]
    public void The_Routing_Sentence_Names_Code_Review_And_Code_Audit_Only_When_The_Catalog_Has_Them(string first, string second, string expectedSentence)
    {
        var result = SkillPromptComposer.Compose("base", [], [Described(first, first + " questions."), Described(second, second + " questions.")]);

        result.ShouldBe($"base\n\n# Skills\n{expectedSentence}\n- {first}: {first} questions\n- {second}: {second} questions");
    }

    [Fact]
    public void A_Skill_Summary_Is_The_First_Sentence_Cut_At_A_Dash_Without_Quotes_And_At_Most_160_Characters()
    {
        var longDescription = "Answering questions about Node.js " + new string('x', 300) + ". Second sentence.";

        var result = SkillPromptComposer.Compose(
            "base",
            [],
            [
                Described("quoted", "\"When the user weighs options.\""),
                Described("emdash", "Answering Azure questions \u2014 choosing services (App Service). Not AWS."),
                Described("long", longDescription),
            ]);

        result.ShouldContain("\n- quoted: When the user weighs options\n");
        result.ShouldContain("\n- emdash: Answering Azure questions\n");
        var longLine = result.Split('\n').Single(l => l.StartsWith("- long: ", StringComparison.Ordinal));
        longLine["- long: ".Length..].ShouldBe(longDescription[..160]);
    }

    [Fact]
    public void A_Selected_Skill_And_An_Empty_Catalog_Add_No_Skills_Section()
    {
        var result = SkillPromptComposer.Compose("base", [Make("debugging", "debug carefully")], []);

        result.ShouldNotContain("# Skills");
        result.ShouldContain("# Relevant skill: debugging");
    }
}
