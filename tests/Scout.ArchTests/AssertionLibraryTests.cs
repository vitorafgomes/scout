using Shouldly;
using Xunit;

namespace Scout.ArchTests;

/// <summary>Rule H: every test project asserts with Shouldly, and FluentAssertions (commercially licensed from v8) is referenced nowhere.</summary>
public class AssertionLibraryTests
{
    private static string Root => ArchitectureTests.SolutionRoot();

    [Fact]
    public void EveryTestProjectReferencesShouldly()
    {
        var missing = SourceText.TestProjectFiles()
            .Where(csproj => File.ReadAllText(csproj).Contains("Include=\"Shouldly\"", StringComparison.Ordinal) is false)
            .Select(SourceText.RepoPath)
            .ToList();

        missing.ShouldBeEmpty("a test project without Shouldly asserts with something else, or with nothing");
    }

    [Fact]
    public void NothingReferencesFluentAssertions()
    {
        var packages = File.ReadAllText(Path.Combine(Root, "Directory.Packages.props"));
        packages.ShouldNotContain("Include=\"FluentAssertions", customMessage: "context/pages/conventions/testing.md: FluentAssertions is never pinned, let alone referenced");

        var projects = SourceText.FilesNamed("*.csproj", "src", "tests")
            .Where(csproj => File.ReadAllText(csproj).Contains("FluentAssertions", StringComparison.Ordinal))
            .Select(SourceText.RepoPath)
            .ToList();

        projects.ShouldBeEmpty("context/pages/conventions/testing.md: FluentAssertions is never referenced");

        var sources = SourceText.CSharpUnder("tests")
            .Where(file => SourceText.CodeOnly(File.ReadAllText(file)).Contains("using FluentAssertions", StringComparison.Ordinal))
            .Select(SourceText.RepoPath)
            .ToList();

        sources.ShouldBeEmpty("context/pages/conventions/testing.md: a test asserts with Shouldly");
    }

    [Fact]
    public void TheRuleFindsTheTestProjectsAtAll() =>
        SourceText.TestProjectFiles().Count.ShouldBeGreaterThanOrEqualTo(5);
}
