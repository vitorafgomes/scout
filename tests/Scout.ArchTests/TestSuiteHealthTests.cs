using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Scout.ArchTests;

/// <summary>
/// The test tree itself: no suite carries a skipped test, every source project has its test project (or is one of
/// the two the project map covers from Scout.ControlPlane.Tests), every test project is in the solution (a project
/// outside it never runs in CI), and no test directory outlives its project file.
/// </summary>
public partial class TestSuiteHealthTests
{
    /// <summary>Source projects without a test project of their own; the project map names where their tests live.</summary>
    private static readonly string[] CoveredElsewhere = ["Scout.ControlPlane.Contracts", "Scout.Host.ControlPlane"];

    [GeneratedRegex(@"\[\s*(?:Fact|Theory)\s*\([^)]*\bSkip\s*=", RegexOptions.Multiline)]
    private static partial Regex SkippedTestAttribute();

    private static string Root => ArchitectureTests.SolutionRoot();

    internal static bool DeclaresASkippedTest(string source) =>
        SkippedTestAttribute().IsMatch(SourceText.StripLiteralsAndComments(source));

    [Fact]
    public void NoTestIsSkipped()
    {
        var offenders = SourceText.SourceFiles("tests")
            .Where(file => DeclaresASkippedTest(File.ReadAllText(file)))
            .Select(SourceText.Relative)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "a skipped test reports green while executing nothing; fix the behaviour, or delete the test and "
          + "record the gap in the vault roadmap - do not park it here");
    }

    [Fact]
    public void EverySourceProjectHasATestProject()
    {
        var projects = Directory.EnumerateDirectories(Path.Combine(Root, "src"), "Scout.*", SearchOption.TopDirectoryOnly)
            .Select(dir => new DirectoryInfo(dir).Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        projects.ShouldNotBeEmpty("the project scan found nothing under src/");

        var missing = projects
            .Where(name => CoveredElsewhere.Contains(name) is false)
            .Where(name => File.Exists(Path.Combine(Root, "tests", $"{name}.Tests", $"{name}.Tests.csproj")) is false)
            .ToList();

        missing.ShouldBeEmpty("a project without tests/<Name>.Tests has no surface for gate 4 to run; a project covered from another suite is named in CoveredElsewhere and in the project map");
    }

    [Fact]
    public void EveryTestProjectIsInTheSolution()
    {
        var solution = File.ReadAllText(Path.Combine(Root, "Scout.slnx"));

        var missing = SourceText.TestProjects()
            .Select(SourceText.Relative)
            .Where(rel => solution.Contains($"Path=\"{rel}\"", StringComparison.Ordinal) is false)
            .ToList();

        missing.ShouldBeEmpty("a test project outside Scout.slnx never runs in CI and reports nothing locally either");
    }

    [Fact]
    public void EveryTestDirectoryStillHasItsProject()
    {
        var orphans = Directory.EnumerateDirectories(Path.Combine(Root, "tests"), "Scout.*", SearchOption.TopDirectoryOnly)
            .Where(dir => Directory.EnumerateFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly).Any() is false)
            .Select(SourceText.Relative)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        orphans.ShouldBeEmpty("a test directory without a project is a suite that looks present and never runs");
    }

    [Theory]
    [InlineData("[Fact(Skip = \"flaky\")]", true)]
    [InlineData("[Theory(Skip = \"later\")]", true)]
    [InlineData("[Fact]", false)]
    [InlineData("// [Fact(Skip = \"example\")]", false)]
    [InlineData("var s = \"[Fact(Skip = x)]\";", false)]
    public void TheDetectorSeesOnlyASkippedTestAttribute(string source, bool skipped) =>
        DeclaresASkippedTest(source).ShouldBe(skipped);

    [Fact]
    public void TheRuleReadsTheTestTreeAtAll() =>
        SourceText.TestProjects().Count.ShouldBeGreaterThanOrEqualTo(15, "the test tree scan found too few projects");
}
