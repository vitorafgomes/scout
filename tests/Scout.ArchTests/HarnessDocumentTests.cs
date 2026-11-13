using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Scout.ArchTests;

/// <summary>
/// The repository's policy, agent and skill documents state what the process around them really does: the security
/// policy names the releases that exist, the implementer runs every UI check the pipeline requires, a wiki skill asks
/// consent for every file it writes, and two lint runs on one day keep both reports.
/// </summary>
public partial class HarnessDocumentTests
{
    private static string Root => ArchitectureTests.SolutionRoot();

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative));

    [GeneratedRegex(@"\bno\s+(?:released\s+versions|releases)\b|\bnot\s+(?:yet\s+)?released\b", RegexOptions.IgnoreCase)]
    private static partial Regex DeniesReleases();

    [GeneratedRegex(@"bun run [\w:-]+")]
    private static partial Regex BunCommand();

    [GeneratedRegex(@"\bexists\b", RegexOptions.IgnoreCase)]
    private static partial Regex ChecksForAnEarlierFile();

    [GeneratedRegex(@"lint-YYYY-MM-DD-(?<suffix>[^`\s.]+)\.md", RegexOptions.IgnoreCase)]
    private static partial Regex SuffixedReportPath();

    [GeneratedRegex(@"^Report:\s*archive/lint-YYYY-MM-DD\.md\s*$", RegexOptions.Multiline)]
    private static partial Regex FixedReportPathInTheLog();

    [GeneratedRegex(@"dotnet\s+tool\s+install\b[^\n`]*?\s(?:-g|--global)\b")]
    private static partial Regex GlobalToolInstall();

    [GeneratedRegex(@"git\s+archive\s+origin/main\b")]
    private static partial Regex ArchiveOfTheDefaultBranch();

    [GeneratedRegex(@"\.claude/hooks/(?<name>[\w-]+)\.sh\b")]
    private static partial Regex HookScript();

    internal static bool TellsAGlobalToolInstall(string text) => GlobalToolInstall().IsMatch(text);

    /// <summary>The body under a heading line that starts with <paramref name="heading"/>, up to the next heading of the same or a higher level outside a code fence.</summary>
    internal static string Section(string markdown, string heading)
    {
        var lines = markdown.Split('\n');
        var start = Array.FindIndex(lines, line => line.StartsWith(heading, StringComparison.Ordinal));

        if (start < 0)
        {
            return string.Empty;
        }

        var level = heading.TakeWhile(c => c == '#').Count();
        var body = new List<string>();
        var fenced = false;

        foreach (var line in lines.Skip(start + 1))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
            }

            var depth = line.TakeWhile(c => c == '#').Count();

            if (!fenced && depth > 0 && depth <= level && line.Length > depth && line[depth] == ' ')
            {
                break;
            }

            body.Add(line);
        }

        return string.Join('\n', body);
    }

    internal static bool DeniesThatReleasesExist(string text) => DeniesReleases().IsMatch(text);

    /// <summary>
    /// True when the report step gives a second run on one day a path of its own: a suffixed report name, chosen when
    /// the day's report already exists or carrying the time of the run. Appending to, or overwriting, the day's one
    /// file names no second path, so it is false.
    /// </summary>
    internal static bool GivesASecondRunItsOwnPath(string reportStep)
    {
        var suffixed = SuffixedReportPath().Matches(reportStep);

        return suffixed.Count > 0
            && (ChecksForAnEarlierFile().IsMatch(reportStep) || suffixed.Any(m => m.Groups["suffix"].Value.Contains("HH", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheSecurityPolicyDoesNotDenyTheReleasesTheReleaseWorkflowPublishes()
    {
        File.Exists(Path.Combine(Root, ".github", "workflows", "release.yml")).ShouldBeTrue("the rule is about the releases this workflow publishes");

        var supported = Section(Read(".github/SECURITY.md"), "## Supported versions");

        supported.ShouldNotBeNullOrWhiteSpace(".github/SECURITY.md has no Supported versions section");
        DeniesThatReleasesExist(supported).ShouldBeFalse(
            "SECURITY.md says there are no releases while .github/workflows/release.yml publishes them; a user of a released build cannot tell whether it gets security fixes");
        supported.ShouldContain(".github/workflows/release.yml", customMessage: "the supported-versions section names how releases are published");
    }

    [Theory]
    [InlineData("Security fixes are applied to `main`. There are no released versions yet.", true)]
    [InlineData("There are no releases yet.", true)]
    [InlineData("Scout is not released yet.", true)]
    [InlineData("Releases are published by the Release workflow. No support policy for older releases is written yet.", false)]
    public void TheDenialDetectorSeesOnlyAClaimThatNothingIsReleased(string text, bool denies) =>
        DeniesThatReleasesExist(text).ShouldBe(denies);

    [Fact]
    public void TheImplementerRunsEveryUiCheckTheGateFourTableNames()
    {
        var uiRow = Read(".claude/skills/scout-pipeline/reference/test-surface.md").Split('\n')
            .Single(line => line.TrimStart().StartsWith("| `ui/**` |", StringComparison.Ordinal));
        var required = BunCommand().Matches(uiRow).Select(m => m.Value).Distinct().ToList();

        var gates = Section(Read(".claude/agents/scout-implementer.md"), "## Checks");
        var run = BunCommand().Matches(gates).Select(m => m.Value).Distinct().ToList();

        required.ShouldNotBeEmpty("the test surface's ui/** row names no bun command");
        run.ShouldBe(required, ignoreOrder: true,
            customMessage: "the implementer's UI checks must be exactly the checks the pipeline's test surface requires for ui/**, or a UI change reaches review unchecked");
    }

    [Fact]
    public void TheGateForThePipelineSkillIsTheGraderTableAndTheLiveEvalIsAdvisory()
    {
        var row = Read(".claude/skills/scout-pipeline/reference/test-surface.md").Split('\n')
            .Single(line => line.TrimStart().StartsWith("| `.claude/skills/scout-pipeline/**` |", StringComparison.Ordinal));
        var surface = row.Split('|', StringSplitOptions.TrimEntries)[2];

        surface.ShouldStartWith("`dotnet test tests/Scout.ArchTests`",
            customMessage: "the gate for a change to the pipeline skill is the deterministic grader table, run by dotnet test tests/Scout.ArchTests in CI and locally");
        surface.ShouldContain(nameof(PipelineEvalGraderTests), customMessage: "the gate row names the table it runs");
        surface.ShouldNotContain("passes every case", customMessage: "the live eval is model-judged and noisy at the suite threshold, so it cannot be the gate");
        surface.ShouldContain("advisory", customMessage: "the gate row says the live claude plugin eval is advisory");
        surface.ShouldContain("PR body", customMessage: "the gate row says where the live eval's result is recorded");
    }

    [Fact]
    public void FilingAQueryAnswerAsksConsentForEveryFileItWrites()
    {
        var step = Section(Read(".claude/skills/wiki-query/SKILL.md"), "### Step 6");
        var cut = step.IndexOf("If the user agrees", StringComparison.Ordinal);

        cut.ShouldBeGreaterThan(0, "Step 6 of wiki-query has no consent followed by the filing writes");

        var consent = step[..cut];
        var filing = step[cut..];

        var implied = new (string Trigger, string[] Writes)[]
        {
            ("backlink", ["backlink"]),
            ("log.md", ["log.md"]),
            ("wiki-integrate", ["index.md", "backlink", "log.md"]),
        };

        var required = implied
            .Where(entry => filing.Contains(entry.Trigger, StringComparison.OrdinalIgnoreCase))
            .SelectMany(entry => entry.Writes)
            .Distinct()
            .ToList();

        required.ShouldNotBeEmpty("the filing step names none of the writes this rule knows; read the step again");
        required.Where(write => consent.Contains(write, StringComparison.OrdinalIgnoreCase) is false).ShouldBeEmpty(
            "the consent wiki-query asks before filing must name every file the filing writes (wiki-integrate adds the index entry, backlinks and its own log entry)");
    }

    [Fact]
    public void TwoLintRunsOnOneDayWriteTwoReports()
    {
        var skill = Read(".claude/skills/wiki-lint/SKILL.md");
        var write = Section(skill, "### Step 8");
        var log = Section(skill, "### Step 9");

        write.ShouldNotBeNullOrWhiteSpace("wiki-lint has no Step 8");
        GivesASecondRunItsOwnPath(write).ShouldBeTrue("Step 8 maps every run of one day to one report path, so a second run overwrites or appends to the first");
        FixedReportPathInTheLog().IsMatch(log).ShouldBeFalse("Step 9's log entry points at the fixed daily path, not at the report this run wrote");
    }

    [Theory]
    [InlineData("Create `[wiki-root]/archive/lint-YYYY-MM-DD.md`:", false)]
    [InlineData("Create `archive/lint-YYYY-MM-DD.md`. If that file already exists, append this report below its content.", false)]
    [InlineData("Create `archive/lint-YYYY-MM-DD.md`. If that file already exists, overwrite it with this report.", false)]
    [InlineData("Create `archive/lint-YYYY-MM-DD.md`; a run later the same day appends a new section to the same file.", false)]
    [InlineData("Create `archive/lint-YYYY-MM-DD.md`. If that file already exists, write `lint-YYYY-MM-DD-2.md`, or the next free number.", true)]
    [InlineData("Create `archive/lint-YYYY-MM-DD-HHMMSS.md`, the time of the run in the name.", true)]
    public void TheSameDayDetectorAcceptsOnlyAPathOfItsOwnForTheSecondRun(string reportStep, bool ownPath) =>
        GivesASecondRunItsOwnPath(reportStep).ShouldBe(ownPath);

    [Fact]
    public void NoRecipeInstallsADotnetToolGloballyAndTheMutationToolIsPinnedInTheManifest()
    {
        var recipes = Directory.EnumerateFiles(Path.Combine(Root, ".claude", "skills"), "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Root, path))
            .Append("AGENTS.md")
            .Append(Path.Combine("context", "pages", "conventions", "tooling.md"))
            .ToList();

        recipes.ShouldContain(Path.Combine(".claude", "skills", "stryker-project", "SKILL.md"));
        recipes.Where(path => TellsAGlobalToolInstall(Read(path))).ShouldBeEmpty(
            "a recipe tells the reader to install a dotnet tool globally: that writes outside the repository and fetches whatever version is current; pin it in .config/dotnet-tools.json and run it after dotnet tool restore");
        Read(Path.Combine(".claude", "skills", "stryker-project", "SKILL.md")).ShouldContain("dotnet tool restore",
            customMessage: "the mutation-testing recipe restores the tool from the repository's manifest");

        using var manifest = JsonDocument.Parse(Read(Path.Combine(".config", "dotnet-tools.json")));
        manifest.RootElement.GetProperty("tools").TryGetProperty("dotnet-stryker", out var stryker)
            .ShouldBeTrue("dotnet-stryker is not in the repository's tool manifest");
        stryker.GetProperty("version").GetString().ShouldNotBeNullOrWhiteSpace("the manifest entry has no fixed version");
        stryker.GetProperty("rollForward").GetBoolean().ShouldBeFalse("the manifest entry may roll forward to another version");
    }

    [Theory]
    [InlineData("Stryker.NET is a dotnet tool; install once with `dotnet tool install -g dotnet-stryker`.", true)]
    [InlineData("Run `dotnet tool install --global dotnet-stryker` first.", true)]
    [InlineData("Run `dotnet tool install dotnet-stryker -g` first.", true)]
    [InlineData("Run `dotnet tool restore`, then `dotnet tool run dotnet-stryker` from the test project.", false)]
    [InlineData("Run `dotnet tool install --local dotnet-stryker --version 5.1.0` once.", false)]
    public void TheGlobalInstallDetectorSeesOnlyAGlobalInstall(string text, bool global) =>
        TellsAGlobalToolInstall(text).ShouldBe(global);

    [Fact]
    public void TheProveRedArchiveIsTheMergeBaseOfTheBranchNotTheDefaultBranch()
    {
        var proveRed = Section(Read(Path.Combine(".claude", "agents", "scout-mechanic.md")), "## Steps");

        proveRed.ShouldNotBeNullOrWhiteSpace("scout-mechanic has no Steps section for the .prove-red");
        proveRed.ShouldContain("git merge-base HEAD origin/main",
            customMessage: "the proof that a test is red must run on the commit the branch started from, the merge base of HEAD and origin/main");
        ArchiveOfTheDefaultBranch().IsMatch(proveRed).ShouldBeFalse(
            "the .prove-red archive is taken from origin/main, which a stale or advanced default branch makes a different codebase from the one the change started from");
    }

    [Fact]
    public void EveryHookTheSettingsRunExistsHasASelfTestAndCiRunsEverySelfTestUnderClaude()
    {
        using var settings = JsonDocument.Parse(Read(Path.Combine(".claude", "settings.json")));
        var commands = settings.RootElement.GetProperty("hooks").EnumerateObject()
            .SelectMany(hookEvent => hookEvent.Value.EnumerateArray())
            .SelectMany(group => group.GetProperty("hooks").EnumerateArray())
            .Select(hook => hook.GetProperty("command").GetString() ?? string.Empty)
            .ToList();
        var ci = Read(Path.Combine(".github", "workflows", "ci.yml"));

        commands.ShouldNotBeEmpty(".claude/settings.json registers no hook");
        commands.Where(command => !HookScript().IsMatch(command)).ShouldBeEmpty(
            "a hook in .claude/settings.json runs something other than a script under .claude/hooks, which no self-test covers");

        var hooks = commands.Select(command => HookScript().Match(command).Groups["name"].Value).Distinct().ToList();
        hooks.Where(name => !File.Exists(Path.Combine(Root, ".claude", "hooks", name + ".sh"))).ShouldBeEmpty(
            "a hook .claude/settings.json runs does not exist, so Claude Code fails it on every event");
        hooks.Where(name => !File.Exists(Path.Combine(Root, ".claude", "hooks", name + ".test.sh"))).ShouldBeEmpty(
            "a hook .claude/settings.json runs has no .test.sh beside it");

        var selfTests = Directory.EnumerateFiles(Path.Combine(Root, ".claude", "hooks"), "*.test.sh")
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, ".claude", "workflows"), "*.test.sh"))
            .Select(path => Path.GetRelativePath(Root, path).Replace('\\', '/'))
            .ToList();
        var ciRuns = ci.Split('\n').Select(line => line.Trim()).ToHashSet(StringComparer.Ordinal);
        selfTests.Where(test => !ciRuns.Contains($"bash {test}")).ShouldBeEmpty(
            "a self-test under .claude/hooks or .claude/workflows is not run by .github/workflows/ci.yml, so a regression in it reaches main");
    }
}
