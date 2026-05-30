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
}
