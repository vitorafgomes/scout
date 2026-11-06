using Scout.Plugin.GitHub;

namespace Scout.Plugin.GitHub.Tests;

public sealed class GitHubNamesTests
{
    [Theory]
    [InlineData("octocat", "hello-world", true)]
    [InlineData("my-org", "Repo.Name_1", true)]
    [InlineData("a", "b", true)]
    [InlineData("", "hello", false)]
    [InlineData("octocat", "", false)]
    [InlineData("-octocat", "hello", false)]
    [InlineData("..", "hello", false)]
    [InlineData("octocat", "hello/../../user", false)]
    [InlineData("octo cat", "hello", false)]
    [InlineData("octocat?x=1", "hello", false)]
    public void A_Repository_Name_Is_Letters_Digits_Dots_Hyphens_Or_Underscores(string owner, string repo, bool valid) =>
        GitHubNames.IsValidRepository(owner, repo).ShouldBe(valid);

    [Theory]
    [InlineData("README.md", true)]
    [InlineData("src/Orders/Checkout.cs", true)]
    [InlineData("src/a b/c.txt", true)]
    [InlineData("", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("src/../../x", false)]
    [InlineData("src/./x", false)]
    [InlineData("src//x", false)]
    [InlineData("src\\x", false)]
    public void A_Path_Is_Relative_With_No_Dot_Segments(string path, bool valid) =>
        GitHubNames.IsValidPath(path).ShouldBe(valid);

    [Fact]
    public void A_Path_Over_A_Thousand_Characters_Is_Refused() =>
        GitHubNames.IsValidPath(new string('a', 1001)).ShouldBeFalse();

    [Theory]
    [InlineData("", true)]
    [InlineData("main", true)]
    [InlineData("feat/checkout-2", true)]
    [InlineData("v1.2.3", true)]
    [InlineData("0123456789abcdef", true)]
    [InlineData("main?x=1", false)]
    [InlineData("a..b", false)]
    [InlineData("a b", false)]
    public void A_Reference_Is_A_Branch_Tag_Or_Commit_Name(string reference, bool valid) =>
        GitHubNames.IsValidReference(reference).ShouldBe(valid);
}
