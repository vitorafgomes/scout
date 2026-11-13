using System.Text.RegularExpressions;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Scout.ArchTests;

/// <summary>
/// Keeps the test tree honest: nothing is parked behind a skip, each project under src has a suite of its own
/// (two are exercised from the control-plane suite instead), each suite is listed in the solution so CI runs it,
/// and no suite folder is left behind without its project file.
/// </summary>
public partial class TestSuiteHealthTests
{
    /// <summary>The src projects whose tests live in the control-plane suite, which references and drives both.</summary>
    private static readonly HashSet<string> ExercisedByTheControlPlaneSuite = new(StringComparer.Ordinal)
    {
        "Scout.ControlPlane.Contracts",
        "Scout.Host.ControlPlane",
    };

    [GeneratedRegex(@"(?<=[\[,]\s*)(?:\w+\.)*(?:Fact|Theory)(?:Attribute)?\s*\((?<arguments>[^()]*)\)")]
    private static partial Regex TestAttributeWithArguments();

    [GeneratedRegex(@"\bSkip\s*=")]
    private static partial Regex SkipArgument();

    /// <summary>True when a Fact or Theory attribute in the code, not in a comment or a string, sets Skip.</summary>
    internal static bool SkipsATest(string source) =>
        TestAttributeWithArguments()
            .Matches(SourceText.CodeOnly(source))
            .Any(attribute => SkipArgument().IsMatch(attribute.Groups["arguments"].Value));

    private static string InRepo(params string[] parts) => Path.Combine([ArchitectureTests.SolutionRoot(), .. parts]);

    private static List<string> ScoutFoldersIn(string parent) =>
        [.. Directory.EnumerateDirectories(InRepo(parent), "Scout.*", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal)];

    [Fact]
    public void NoSuiteParksATestBehindASkip()
    {
        var skipping = SourceText.CSharpUnder("tests")
            .Where(file => SkipsATest(File.ReadAllText(file)))
            .Select(SourceText.RepoPath)
            .Order(StringComparer.Ordinal)
            .ToList();

        skipping.ShouldBeEmpty(
            "a skipped test is counted as green and runs nothing; make it pass, or remove it and write the missing "
          + "coverage down as a follow-up in the context folder");
    }

    [Fact]
    public void EachSourceProjectOwnsATestSuite()
    {
        var sourceProjects = ScoutFoldersIn("src").Select(folder => new DirectoryInfo(folder).Name).ToList();

        sourceProjects.ShouldNotBeEmpty("no Scout.* folder was found under src, so the rule checked nothing");

        var withoutASuite = sourceProjects
            .Except(ExercisedByTheControlPlaneSuite)
            .Where(name => File.Exists(InRepo("tests", name + ".Tests", name + ".Tests.csproj")) is false)
            .ToList();

        withoutASuite.ShouldBeEmpty(
            "gate 4 runs tests/<Name>.Tests for a change to <Name>, so a project without one changes untested; a project "
          + "tested from another suite belongs in ExercisedByTheControlPlaneSuite and in the project map");
    }

    [Fact]
    public void EverySuiteIsListedInTheSolution()
    {
        var listed = XDocument.Load(InRepo("Scout.slnx"))
            .Descendants("Project")
            .Select(project => ((string?)project.Attribute("Path") ?? string.Empty).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);

        var unlisted = SourceText.TestProjectFiles()
            .Select(SourceText.RepoPath)
            .Where(path => listed.Contains(path) is false)
            .ToList();

        unlisted.ShouldBeEmpty("dotnet test on Scout.slnx, locally and in CI, never builds a suite the solution does not list");
    }

    [Fact]
    public void NoSuiteFolderOutlivesItsProject()
    {
        var empty = ScoutFoldersIn("tests")
            .Where(folder => Directory.EnumerateFiles(folder, "*.csproj", SearchOption.TopDirectoryOnly).Any() is false)
            .Select(SourceText.RepoPath)
            .ToList();

        empty.ShouldBeEmpty("a suite folder with no project file looks like coverage and runs nothing");
    }

    [Theory]
    [InlineData("[Fact(Skip = \"waiting on a fix\")]", true)]
    [InlineData("[Theory(Skip = \"data source down\")]", true)]
    [InlineData("[Trait(\"Category\", \"Docker\"), Fact(Skip = \"needs docker\")]", true)]
    [InlineData("[Theory]", false)]
    [InlineData("/* [Fact(Skip = \"inside a comment\")] */", false)]
    [InlineData("var text = \"[Theory(Skip = y)]\";", false)]
    [InlineData("[Fact(DisplayName = \"skips nothing\")]", false)]
    public void OnlyASkipOnATestAttributeCounts(string source, bool skips) =>
        SkipsATest(source).ShouldBe(skips);

    [Fact]
    public void TheTreeScanSeesTheSuites() =>
        SourceText.TestProjectFiles().Count.ShouldBeGreaterThanOrEqualTo(15, "fewer suites than Scout has means the scan looked in the wrong place");
}
