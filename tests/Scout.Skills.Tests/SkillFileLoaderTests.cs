using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Scout.Skills.Tests;

public sealed class SkillFileLoaderTests : IDisposable
{
    private readonly string _dir;

    public SkillFileLoaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "scout-skills-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private SkillFileLoader Loader() => new(NullLogger<SkillFileLoader>.Instance);

    private void WriteSkill(string fileName, string content)
        => File.WriteAllText(Path.Combine(_dir, fileName), content);

    [Fact]
    public void Load_Parses_WellFormed_Skill()
    {
        WriteSkill("debugging.md",
            "---\nname: debugging\ndescription: use this when debugging\n---\nStep one. Step two.");

        var skills = Loader().Load(_dir);

        skills.Count.ShouldBe(1);
        var skill = skills[0];
        skill.Name.ShouldBe("debugging");
        skill.Description.ShouldBe("use this when debugging");
        skill.Body.ShouldBe("Step one. Step two.");
        skill.SourcePath.ShouldBe(Path.Combine(_dir, "debugging.md"));
    }

    [Fact]
    public void Load_Skips_Malformed_And_Keeps_Sibling_Valid()
    {
        // Missing description field.
        WriteSkill("broken.md", "---\nname: broken\n---\nbody here");
        // No opening fence.
        WriteSkill("nofence.md", "name: x\ndescription: y\nbody");
        // Empty body.
        WriteSkill("empty-body.md", "---\nname: e\ndescription: d\n---\n");
        // Valid sibling.
        WriteSkill("ok.md", "---\nname: ok\ndescription: good\n---\nbody text");

        var skills = Loader().Load(_dir);

        skills.Count.ShouldBe(1);
        skills[0].Name.ShouldBe("ok");
    }

    [Fact]
    public void Load_Returns_Empty_For_Absent_Directory()
    {
        var skills = Loader().Load(Path.Combine(_dir, "does-not-exist"));

        skills.ShouldBeEmpty();
    }

    [Fact]
    public void Load_Returns_Empty_For_Empty_Directory()
    {
        var skills = Loader().Load(_dir);

        skills.ShouldBeEmpty();
    }
}
