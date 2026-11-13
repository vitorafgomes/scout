using Microsoft.Extensions.Logging;
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

    private static SkillFileLoader Loader() => new(NullLogger<SkillFileLoader>.Instance);

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

    [Fact]
    public void A_Functions_Line_Declares_The_Plugin_Functions_The_Skill_May_Call_In_Order()
    {
        WriteSkill("review.md",
            "---\nname: review\ndescription: review a pull request\nfunctions: GitHub.get_pull_request ,GitHubActions.submit_pull_request_review,  ProjectKnowledge.find_symbol\n---\nRead it, then post it.");

        var skill = Loader().Load(_dir).ShouldHaveSingleItem();

        skill.Functions.ShouldBe(["GitHub.get_pull_request", "GitHubActions.submit_pull_request_review", "ProjectKnowledge.find_symbol"]);
        skill.Body.ShouldBe("Read it, then post it.");
    }

    [Fact]
    public void A_Skill_Without_A_Functions_Line_Declares_No_Function()
    {
        WriteSkill("python.md", "---\nname: python\ndescription: python rules\n---\nUse type hints.");

        Loader().Load(_dir).ShouldHaveSingleItem().Functions.ShouldBeEmpty();
    }

    [Fact]
    public void A_Functions_Entry_That_Is_Not_Plugin_Dot_Function_Is_Dropped_With_A_Warning_And_The_Rest_Kept()
    {
        WriteSkill("issues.md",
            "---\nname: issues\ndescription: open issues\nfunctions: GitHubActions.create_issue, create_issue, .x, GitHub., A.b.c\n---\nOpen it.");
        var logger = new RecordingLogger<SkillFileLoader>();

        var skill = new SkillFileLoader(logger).Load(_dir).ShouldHaveSingleItem();

        skill.Functions.ShouldBe(["GitHubActions.create_issue"]);
        logger.Entries.Select(e => (e.Level, e.Message)).ShouldBe(
        [
            (LogLevel.Warning, $"Skill {Path.Combine(_dir, "issues.md")} declares 'create_issue', which is not Plugin.function; it is not offered."),
            (LogLevel.Warning, $"Skill {Path.Combine(_dir, "issues.md")} declares '.x', which is not Plugin.function; it is not offered."),
            (LogLevel.Warning, $"Skill {Path.Combine(_dir, "issues.md")} declares 'GitHub.', which is not Plugin.function; it is not offered."),
            (LogLevel.Warning, $"Skill {Path.Combine(_dir, "issues.md")} declares 'A.b.c', which is not Plugin.function; it is not offered."),
        ]);
    }
}
