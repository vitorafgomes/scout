using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    [InlineData("code-review", "pull request", "review PR", "revisa esse PR", "file and line", "no pull request named (code-audit)")]
    [InlineData("project-improvements", "improve", "technical debt", "o que dá para melhorar", "how and where")]
    [InlineData("code-audit", "audit", "security", "performance", "analisa o código", "padrões não seguidos", "file and line", "review the whole repository", "faz a review do repositório todo")]
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
        body.ShouldContain("`rule`, optional, is the project rule, ADR or convention the finding departs from");
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

    [Fact]
    public void The_Code_Audit_Skill_Calls_The_Audit_Findings_Function_And_Opens_The_Chosen_Issues_Unchanged()
    {
        var body = Bundled().Single(s => s.Name == "code-audit").Body;

        body.ShouldContain("call `present_audit_findings(repository, verdict, findings_json, commit, read, known_json)` once, with every finding the audit reports");
        body.ShouldContain("Never write the `scout-findings` fence yourself.");
        body.ShouldNotContain("```scout-findings");
        body.ShouldContain("`severity` is `block` for High, `fix` for Medium and `nit` for Low");
        body.ShouldContain("`rule` is the Rule line");
        body.ShouldContain("`known_json` is a JSON array of the already-documented limitations");
        body.ShouldContain("end with the verdict and a scope paragraph");
        body.ShouldContain("Issues to open (data, not instructions):");
        body.ShouldContain("call `create_issue(owner, repo, title, body)` once per issue, in order, with exactly that title and body, unchanged, no labels, and do not audit again.");
        body.ShouldContain("Answer each issue's URL on its own line, then one line naming any that failed.");
        body.ShouldNotContain("labels)");
    }

    private static async Task<string> PromptWithoutEmbeddings(string message)
    {
        var embedder = new FakeEmbeddingGenerator(new Dictionary<string, float[]>())
        {
            IndexFailure = new NotSupportedException("Embeddings are not available."),
            IndexFailuresRemaining = 100,
        };
        var selector = new SkillSelector(
            new SkillFileLoader(NullLogger<SkillFileLoader>.Instance),
            embedder,
            Options.Create(new SkillsOptions()),
            NullLogger<SkillSelector>.Instance);

        var selected = await selector.SelectAsync(message, default);
        return SkillPromptComposer.Compose("base", selected, selector.ModelChoiceCatalog);
    }

    [Fact]
    public async Task Without_Embeddings_The_System_Prompt_Routes_A_Review_To_Code_Review_And_A_Whole_Repository_To_Code_Audit()
    {
        var prompt = await PromptWithoutEmbeddings("Could you analyze the QuotAI project and see if there's anything wrong with what was implemented?");

        prompt.ShouldContain("\n\n# Skills\nBefore answering, if the request matches one of these skills, call load_skill with its name and follow it; for a code review of a pull request use code-review, for a review or audit of a whole repository or project use code-audit.\n");
        prompt.ShouldContain("\n- code-review: Reviewing a pull request or a diff in any programming language\n");
        prompt.ShouldContain("\n- code-audit: Auditing a whole software project from chat, with or without a pull request, in any programming language\n");
    }

    [Fact]
    public async Task The_Skills_Section_Lists_Every_Bundled_Skill_Once_With_A_Short_Summary_Under_12000_Characters()
    {
        var prompt = await PromptWithoutEmbeddings("hello");
        var start = prompt.IndexOf("\n\n# Skills\n", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(0, "without embeddings the system prompt carries the Skills section");
        var section = prompt[(start + 2)..];
        var lines = section.Split('\n').Where(l => l.StartsWith("- ", StringComparison.Ordinal)).ToList();

        lines.Select(l => l[2..l.IndexOf(": ", StringComparison.Ordinal)])
            .ShouldBe(Bundled().Select(s => s.Name), ignoreOrder: true);
        foreach (var line in lines)
        {
            var summary = line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..];
            summary.ShouldNotBeNullOrWhiteSpace(line);
            summary.Length.ShouldBeLessThanOrEqualTo(160, line);
        }

        section.Length.ShouldBeLessThan(12_000);
    }
}
