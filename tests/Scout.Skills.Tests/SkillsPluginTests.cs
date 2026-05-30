using Shouldly;
using Xunit;

namespace Scout.Skills.Tests;

public sealed class SkillsPluginTests
{
    private sealed class StubCatalog(IReadOnlyList<Skill> skills) : ISkillCatalog
    {
        public IReadOnlyList<Skill> All() => skills;

        public Skill? Find(string name)
            => skills.FirstOrDefault(s =>
                string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static Skill MakeSkill(string name, string description, string body)
        => new(name, description, body, $"/skills/{name}.md");

    [Fact]
    public void ListSkills_Returns_Name_And_Description_Lines()
    {
        var plugin = new SkillsPlugin(new StubCatalog(
        [
            MakeSkill("alpha", "use alpha when X", "alpha body"),
            MakeSkill("beta", "use beta when Y", "beta body"),
        ]));

        var result = plugin.ListSkills();

        result.ShouldBe("alpha: use alpha when X\nbeta: use beta when Y");
    }

    [Fact]
    public void ListSkills_Returns_Clean_Message_When_Empty()
    {
        var plugin = new SkillsPlugin(new StubCatalog([]));

        plugin.ListSkills().ShouldBe("No skills available.");
    }

    [Fact]
    public void LoadSkill_Returns_Body_For_Known_Name()
    {
        var plugin = new SkillsPlugin(new StubCatalog(
        [
            MakeSkill("alpha", "use alpha when X", "the alpha instructions"),
        ]));

        plugin.LoadSkill("alpha").ShouldBe("the alpha instructions");
    }

    [Fact]
    public void LoadSkill_Returns_NotFound_Message_For_Unknown_Name()
    {
        var plugin = new SkillsPlugin(new StubCatalog([]));

        plugin.LoadSkill("missing")
            .ShouldBe("Skill 'missing' not found. Use list_skills to see available skills.");
    }
}
