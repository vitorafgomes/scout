using Scout.Plugin.GitHub.Internal;

namespace Scout.Plugin.GitHub.Tests;

public class GitHubRepoUrlParserTests
{
    [Theory]
    [InlineData("https://github.com/octocat/Hello-World", "octocat", "Hello-World")]
    [InlineData("http://github.com/octocat/Hello-World", "octocat", "Hello-World")]
    [InlineData("github.com/octocat/Hello-World", "octocat", "Hello-World")]
    [InlineData("www.github.com/octocat/Hello-World", "octocat", "Hello-World")]
    [InlineData("https://github.com/octocat/Hello-World.git", "octocat", "Hello-World")]
    [InlineData("https://github.com/octocat/Hello-World/", "octocat", "Hello-World")]
    [InlineData("https://github.com/octocat/Hello-World.git/", "octocat", "Hello-World")]
    [InlineData("octocat/Hello-World", "octocat", "Hello-World")]
    [InlineData("  octocat/Hello-World  ", "octocat", "Hello-World")]
    public void TryParse_Accepts_Known_Forms(string input, string expectedOwner, string expectedRepo)
    {
        var ok = GitHubRepoUrlParser.TryParse(input, out var owner, out var repo);

        ok.ShouldBeTrue();
        owner.ShouldBe(expectedOwner);
        repo.ShouldBe(expectedRepo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("octocat")]
    [InlineData("octocat/Hello-World/extra")]
    [InlineData("https://gitlab.com/octocat/Hello-World")]
    [InlineData("git@github.com:octocat/Hello-World.git")]
    [InlineData("not a url at all")]
    public void TryParse_Rejects_Garbage(string? input)
    {
        var ok = GitHubRepoUrlParser.TryParse(input, out var owner, out var repo);

        ok.ShouldBeFalse();
        owner.ShouldBe(string.Empty);
        repo.ShouldBe(string.Empty);
    }
}
