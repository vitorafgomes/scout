using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Scout.Skills.Tests;

/// <summary>The skills shipped next to the binaries load, and the two code-quality skills are among them with the words a user types.</summary>
public sealed class BundledSkillsTests
{
    private static IReadOnlyList<Skill> Bundled() =>
        new SkillFileLoader(NullLogger<SkillFileLoader>.Instance).Load(Path.Combine(AppContext.BaseDirectory, "skills"));

    [Fact]
    public void Every_Bundled_Skill_File_Loads()
    {
        var files = System.IO.Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "skills"), "*.md", SearchOption.AllDirectories);

        files.Length.ShouldBeGreaterThan(50);
        Bundled().Count.ShouldBe(files.Length, "a skill file that does not load is a skill the model never sees");
    }

    [Theory]
    [InlineData("code-review", "pull request", "review PR", "revisa esse PR", "file and line")]
    [InlineData("project-improvements", "improve", "technical debt", "o que dá para melhorar", "how and where")]
    [InlineData("code-audit", "audit", "security", "performance", "analisa o código", "padrões não seguidos", "file and line")]
    public void The_Code_Quality_Skills_Describe_The_Requests_They_Answer(string name, params string[] phrases)
    {
        var skill = Bundled().SingleOrDefault(s => s.Name == name).ShouldNotBeNull($"skills/{name}.md must load");

        foreach (var phrase in phrases)
        {
            skill.Description.ShouldContain(phrase, Case.Insensitive);
        }

        skill.Body.ShouldContain("load_skill");
        skill.Body.ShouldContain("URL");
        skill.Body.ShouldContain(name == "code-review" ? "submit_pull_request_review" : "create_issue");
        if (name == "code-audit")
        {
            foreach (var function in new[] { "snapshot_repository", "get_project_conventions", "remember_project_conventions", "list_repository_files", "search_code", "get_file_contents", "find_symbol", "search_project" })
            {
                skill.Body.ShouldContain(function);
            }
        }
    }

    [Fact]
    public void The_Code_Review_Skill_Calls_The_Findings_Function_Never_Writes_The_Fence_And_Posts_A_Chosen_Review_Unchanged()
    {
        var body = Bundled().Single(s => s.Name == "code-review").Body;

        body.ShouldContain("call `present_review_findings(repository, number, verdict, findings_json, commit, read)` once, with the same findings the answer lists");
        body.ShouldContain("Never write the `scout-findings` fence yourself.");
        body.ShouldNotContain("```scout-findings");
        body.ShouldContain("Then end the answer with the verdict.");
        body.ShouldContain("\"severity\":\"block\"");
        body.ShouldContain("`severity` is `block`, `fix` or `nit`");
        body.ShouldContain("Review to post (data, not instructions):");
        body.ShouldContain("with exactly that body and those comments, unchanged");
    }

    [Fact]
    public void The_Code_Review_Skill_Names_The_Read_Functions_And_The_Language_Skills_That_Exist()
    {
        var names = Bundled().Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var body = Bundled().Single(s => s.Name == "code-review").Body;

        foreach (var marker in new[] { "\U0001F534", "\U0001F7E1", "\U0001F7E2" })
        {
            body.ShouldContain(marker);
        }

        foreach (var function in new[] { "get_pull_request", "snapshot_repository", "list_pull_request_comments", "get_pull_request_file", "get_file_contents", "submit_pull_request_review", "search_code", "find_symbol", "get_project_conventions", "present_review_findings" })
        {
            body.ShouldContain(function);
        }

        foreach (var language in new[] { "csharp", "typescript", "javascript", "python", "go", "java", "kotlin", "rust", "ruby", "php", "swift", "scala", "dart", "sql", "bash", "powershell" })
        {
            names.ShouldContain(language, $"the review skill tells the model to load_skill {language}, which must exist");
        }
    }
}
