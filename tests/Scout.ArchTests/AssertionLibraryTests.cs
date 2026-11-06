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
        var missing = SourceText.TestProjects()
            .Where(csproj => File.ReadAllText(csproj).Contains("Include=\"Shouldly\"", StringComparison.Ordinal) is false)
            .Select(SourceText.Relative)
            .ToList();

        missing.ShouldBeEmpty("a test project without Shouldly asserts with something else, or with nothing");
    }

    [Fact]
    public void NothingReferencesFluentAssertions()
    {
        var packages = File.ReadAllText(Path.Combine(Root, "Directory.Packages.props"));
        packages.ShouldNotContain("Include=\"FluentAssertions", customMessage: "vault/pages/conventions/testing.md: FluentAssertions is never pinned, let alone referenced");

        var projects = SourceText.Files(["src", "tests"], "*.csproj")
            .Where(csproj => File.ReadAllText(csproj).Contains("FluentAssertions", StringComparison.Ordinal))
            .Select(SourceText.Relative)
            .ToList();

        projects.ShouldBeEmpty("vault/pages/conventions/testing.md: FluentAssertions is never referenced");

        var sources = SourceText.SourceFiles("tests")
            .Where(file => SourceText.StripLiteralsAndComments(File.ReadAllText(file)).Contains("using FluentAssertions", StringComparison.Ordinal))
            .Select(SourceText.Relative)
            .ToList();

        sources.ShouldBeEmpty("vault/pages/conventions/testing.md: a test asserts with Shouldly");
    }

    [Fact]
    public void TheRuleFindsTheTestProjectsAtAll() =>
        SourceText.TestProjects().Count.ShouldBeGreaterThanOrEqualTo(5);
}
