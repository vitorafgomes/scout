using Microsoft.Extensions.Time.Testing;
using Scout.Llm.Abstractions;
using Scout.Plugin.GitHub.Abstractions;
using Scout.Plugin.GitHub.Tests.Fakes;

namespace Scout.Plugin.GitHub.Tests;

public sealed class GitHubPluginReadOutcomeTests
{
    private static GitHubPlugin Answering(string kind)
    {
        var client = Substitute.For<IGitHubClient>();
        client.GetFileContentAsync("octocat", "hello", "a.cs", "main", Arg.Any<CancellationToken>()).Returns<RepositoryFile?>(_ => kind switch
        {
            "missing" => null,
            "directory" => new RepositoryFile("a.cs", "dir", null, 0, ["b.cs (file)"]),
            "empty-directory" => new RepositoryFile("a.cs", "dir", null, 0, []),
            "symlink" => new RepositoryFile("a.cs", "symlink", null, 0, []),
            "submodule" => new RepositoryFile("a.cs", "submodule", null, 0, []),
            "binary" => new RepositoryFile("a.cs", "file", null, 5_000_000, []),
            "reset" => throw new HttpRequestException("reset"),
            "timeout" => throw new TaskCanceledException(),
            "unavailable" => throw new GitHubApiException("GitHub is currently unavailable. Try again later."),
            _ => new RepositoryFile("a.cs", "file", new string('x', int.Parse(kind, System.Globalization.CultureInfo.InvariantCulture)), 20, []),
        });
        return new GitHubPlugin(client);
    }

    [Theory]
    [InlineData("missing", "a.cs not found in octocat/hello at main.")]
    [InlineData("directory", "a.cs is a directory:\n  - b.cs (file)")]
    [InlineData("empty-directory", "a.cs is an empty directory.")]
    [InlineData("symlink", "a.cs is a symlink, not a file; GitHub returns no text for it.")]
    [InlineData("submodule", "a.cs is a submodule, not a file; GitHub returns no text for it.")]
    [InlineData("binary", "a.cs (5000000 bytes) has no text GitHub returns inline; it is binary or over 1 MB.")]
    [InlineData("reset", "Could not reach GitHub. Try again later.")]
    [InlineData("timeout", "GitHub request timed out. Try again later.")]
    [InlineData("unavailable", "GitHub is currently unavailable. Try again later.")]
    public async Task A_File_GitHub_Did_Not_Hand_Back_Is_Marked_As_Not_Read_And_The_Answer_Stays_The_Same(string kind, string answer)
    {
        using var call = ReadOutcome.Begin();

        var result = await Answering(kind).GetFileContentsAsync("octocat", "hello", "a.cs", "main", CancellationToken.None);

        result.ShouldBe(answer);
        call.NoContent.ShouldBeTrue();
    }

    [Theory]
    [InlineData("../evil", "a.cs", "main", GitHubNames.InvalidRepository)]
    [InlineData("octocat", "/etc/passwd", "main", GitHubNames.InvalidPath)]
    [InlineData("octocat", "a.cs", "a..b", GitHubNames.InvalidReference)]
    public async Task A_Refused_Name_Is_Marked_As_Not_Read(string owner, string path, string reference, string answer)
    {
        using var call = ReadOutcome.Begin();

        var result = await Answering("20").GetFileContentsAsync(owner, "hello", path, reference, CancellationToken.None);

        result.ShouldBe(answer);
        call.NoContent.ShouldBeTrue();
    }

    [Theory]
    [InlineData(20)]
    [InlineData(GitHubPlugin.PatchCap + 500)]
    public async Task The_Text_Of_A_File_Whole_Or_Cut_At_The_Cap_Is_Not_Marked(int length)
    {
        using var call = ReadOutcome.Begin();

        var result = await Answering(length.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .GetFileContentsAsync("octocat", "hello", "a.cs", "main", CancellationToken.None);

        result.ShouldStartWith("a.cs (20 bytes) at main:\n" + new string('x', Math.Min(length, GitHubPlugin.PatchCap)));
        call.NoContent.ShouldBeFalse();
    }

    [Theory]
    [InlineData("src/Checkout.cs", "src/Checkout.cs (41 bytes) at main (local snapshot at abc1234):\nclass Checkout { decimal Total() => 0; }\n", false)]
    [InlineData("src", "src is a directory (local snapshot at abc1234):\n  - Checkout.cs", true)]
    [InlineData("src/Refund.cs", "src/Refund.cs not found in octocat/hello at main (abc1234).", true)]
    [InlineData("logo.bin", "logo.bin (4 bytes) is binary or over 1 MB; no text to show.", true)]
    public async Task From_A_Snapshot_Only_The_Text_Of_A_File_Is_Left_Unmarked(string path, string answer, bool noContent)
    {
        var dir = Directory.CreateTempSubdirectory("scout-read-outcome-").FullName;
        try
        {
            var store = new RepositorySnapshotStore(dir, 0, new FakeTimeProvider());
            await store.ExtractAsync("octocat", "hello", new RepositoryCommit("main", "abc1234def5678", true), new MemoryStream(TarGzFixture.Build(
                TarGzFixture.Text("src/Checkout.cs", "class Checkout { decimal Total() => 0; }\n"),
                ("logo.bin", [1, 0, 2, 0]))), CancellationToken.None);
            var plugin = new GitHubPlugin(Substitute.For<IGitHubClient>(), store);
            using var call = ReadOutcome.Begin();

            var result = await plugin.GetFileContentsAsync("octocat", "hello", path, "abc1234def5678", CancellationToken.None);

            result.ShouldBe(answer);
            call.NoContent.ShouldBe(noContent);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_Read_Outside_Any_Scope_Answers_As_Before_And_Leaves_The_Next_Call_Unmarked()
    {
        var failed = await Answering("reset").GetFileContentsAsync("octocat", "hello", "a.cs", "main", CancellationToken.None);
        using var call = ReadOutcome.Begin();

        var read = await Answering("20").GetFileContentsAsync("octocat", "hello", "a.cs", "main", CancellationToken.None);

        failed.ShouldBe("Could not reach GitHub. Try again later.");
        read.ShouldBe("a.cs (20 bytes) at main:\n" + new string('x', 20));
        call.NoContent.ShouldBeFalse();
    }
}
