namespace Scout.Plugin.CodeReview.Tests;

public sealed class RepositoryUrlParserTests
{
    [Theory]
    [InlineData("https://github.com/octocat/hello-world", "octocat", "hello-world")]
    [InlineData("https://github.com/octocat/hello-world/", "octocat", "hello-world")]
    [InlineData("https://github.com/octocat/hello-world.git", "octocat", "hello-world")]
    [InlineData("https://github.com/octocat/hello-world/tree/main", "octocat", "hello-world")]
    [InlineData("https://www.github.com/Octo-Cat/Hello.World", "Octo-Cat", "Hello.World")]
    public void Parses_Valid_Urls(string url, string owner, string repo)
    {
        RepositoryUrlParser.TryParse(url, out var coords).ShouldBeTrue();

        coords.ShouldNotBeNull();
        coords!.Owner.ShouldBe(owner);
        coords.Repo.ShouldBe(repo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("https://gitlab.com/owner/repo")]
    [InlineData("https://github.com/onlyowner")]
    [InlineData("ftp://github.com/owner/repo")]
    [InlineData("https://github.com/foo%3Fx=/repo")]
    [InlineData("https://github.com/owner/re po")]
    public void Rejects_Invalid_Urls(string? url)
    {
        RepositoryUrlParser.TryParse(url, out var coords).ShouldBeFalse();
        coords.ShouldBeNull();
    }
}
