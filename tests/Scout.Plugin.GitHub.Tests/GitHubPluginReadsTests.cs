using Scout.Plugin.GitHub;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub.Tests;

public sealed class GitHubPluginReadsTests
{
    private static IGitHubClient Client() => Substitute.For<IGitHubClient>();

    private static PullRequestDetails PullRequest(params ChangedFile[] files) => new(
        42, "Add checkout", "Sums the order.", "alice", "open", false, "main", "feat/checkout", "0123456789abcdef",
        10, 2, "https://github.com/octocat/hello/pull/42", files.Length, files);

    [Fact]
    public async Task GetPullRequest_Carries_Metadata_Extensions_Files_And_Diffs()
    {
        var client = Client();
        client.GetPullRequestAsync("octocat", "hello", 42, Arg.Any<CancellationToken>())
            .Returns(PullRequest(
                new ChangedFile("src/Checkout.cs", "modified", 9, 2, "@@ -1 +1 @@\n-old\n+new"),
                new ChangedFile("ui/app.ts", "added", 1, 0, "@@ -0,0 +1 @@\n+export {}"),
                new ChangedFile("img/logo.png", "added", 0, 0, null)));

        var result = await new GitHubPlugin(client).GetPullRequestAsync("octocat", "hello", 42, CancellationToken.None);

        result.ShouldStartWith("#42 Add checkout (open, by alice)");
        result.ShouldContain("Branches: feat/checkout -> main at 0123456");
        result.ShouldContain("Extensions: .cs, .png, .ts");
        result.ShouldContain("Description:\nSums the order.");
        result.ShouldContain("  src/Checkout.cs (modified, +9 -2)");
        result.ShouldContain("--- src/Checkout.cs\n@@ -1 +1 @@\n-old\n+new");
        result.ShouldContain("--- img/logo.png\n" + GitHubPlugin.DiffNotProvided);
    }

    [Fact]
    public async Task GetPullRequest_Leaves_Out_The_Diff_That_Does_Not_Fit_The_Budget_And_Keeps_The_Smaller_Ones()
    {
        var client = Client();
        var big = new string('x', GitHubPlugin.PatchBudget - 100);
        client.GetPullRequestAsync("octocat", "hello", 42, Arg.Any<CancellationToken>())
            .Returns(PullRequest(
                new ChangedFile("a.cs", "modified", 1, 1, big),
                new ChangedFile("b.cs", "modified", 1, 1, new string('y', 200)),
                new ChangedFile("c.cs", "modified", 1, 1, "tiny")));

        var result = await new GitHubPlugin(client).GetPullRequestAsync("octocat", "hello", 42, CancellationToken.None);

        result.ShouldContain("--- a.cs\n" + big);
        result.ShouldContain("--- b.cs\n" + GitHubPlugin.DiffLeftOut);
        result.ShouldContain("--- c.cs\ntiny");
    }

    [Fact]
    public async Task GetPullRequestFile_Returns_One_Diff_Cut_At_The_Cap_And_Names_The_Files_When_Absent()
    {
        var client = Client();
        var huge = new string('z', GitHubPlugin.PatchCap + 10);
        client.ListPullRequestFilesAsync("octocat", "hello", 42, Arg.Any<CancellationToken>())
            .Returns(new List<ChangedFile> { new("src/Checkout.cs", "modified", 9, 2, huge) });
        var plugin = new GitHubPlugin(client);

        var diff = await plugin.GetPullRequestFileAsync("octocat", "hello", 42, "src/Checkout.cs", CancellationToken.None);
        var absent = await plugin.GetPullRequestFileAsync("octocat", "hello", 42, "src/Other.cs", CancellationToken.None);

        diff.ShouldStartWith("src/Checkout.cs (modified, +9 -2)\n" + new string('z', GitHubPlugin.PatchCap));
        diff.ShouldEndWith($"... (cut at {GitHubPlugin.PatchCap} characters)");
        absent.ShouldBe("Pull request #42 does not change src/Other.cs. Files: src/Checkout.cs");
        await client.DidNotReceiveWithAnyArgs().GetPullRequestAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task A_Pull_Request_With_More_Files_Than_GitHub_Lists_Says_So_Instead_Of_Denying_A_File()
    {
        var client = Client();
        var listed = Enumerable.Range(1, GitHubPlugin.ListedFilesCap).Select(i => new ChangedFile($"f{i}.cs", "modified", 1, 0, "@@")).ToList();
        client.ListPullRequestFilesAsync("octocat", "hello", 42, Arg.Any<CancellationToken>()).Returns(listed);
        client.GetPullRequestAsync("octocat", "hello", 42, Arg.Any<CancellationToken>())
            .Returns(PullRequest([.. listed]) with { ChangedFileCount = 140 });
        var plugin = new GitHubPlugin(client);

        var file = await plugin.GetPullRequestFileAsync("octocat", "hello", 42, "f120.cs", CancellationToken.None);
        var overview = await plugin.GetPullRequestAsync("octocat", "hello", 42, CancellationToken.None);

        file.ShouldBe("f120.cs is not among the first 100 files GitHub lists for pull request #42; the pull request may change more files than this plugin can list.");
        overview.ShouldContain("in 140 file(s); only the first 100 are listed below, so this review covers part of the pull request");
    }

    [Fact]
    public async Task ListPullRequestComments_Says_Not_Found_For_A_Missing_Pull_Request()
    {
        var client = Client();
        client.ListPullRequestCommentsAsync("octocat", "hello", 9999, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<PullRequestComment>?)null);

        var result = await new GitHubPlugin(client).ListPullRequestCommentsAsync("octocat", "hello", 9999, CancellationToken.None);

        result.ShouldBe("Pull request #9999 not found in octocat/hello or not accessible.");
    }

    [Fact]
    public async Task ListPullRequestComments_Anchors_Each_Comment()
    {
        var client = Client();
        client.ListPullRequestCommentsAsync("octocat", "hello", 42, Arg.Any<CancellationToken>())
            .Returns(new List<PullRequestComment>
            {
                new("bob", "Null here", "src/Checkout.cs", 88, DateTimeOffset.UnixEpoch),
                new("bob", "Line gone", "src/Old.cs", null, DateTimeOffset.UnixEpoch),
                new("alice", "Thanks", null, null, DateTimeOffset.UnixEpoch),
            });

        var result = await new GitHubPlugin(client).ListPullRequestCommentsAsync("octocat", "hello", 42, CancellationToken.None);

        result.ShouldBe("[src/Checkout.cs:88] bob: Null here\n[src/Old.cs] bob: Line gone\n[general] alice: Thanks");
    }

    [Fact]
    public async Task GetIssue_Formats_The_Issue_And_Redirects_A_Pull_Request_Number()
    {
        var client = Client();
        client.GetIssueAsync("octocat", "hello", 13, Arg.Any<CancellationToken>())
            .Returns(new IssueDetails(13, "Crash on start", "Trace attached", "carol", "open", ["bug"], 3, "https://github.com/octocat/hello/issues/13", false));
        client.GetIssueAsync("octocat", "hello", 42, Arg.Any<CancellationToken>())
            .Returns(new IssueDetails(42, "Add checkout", null, "alice", "open", [], 0, "u", true));
        var plugin = new GitHubPlugin(client);

        var issue = await plugin.GetIssueAsync("octocat", "hello", 13, CancellationToken.None);
        var pr = await plugin.GetIssueAsync("octocat", "hello", 42, CancellationToken.None);

        issue.ShouldBe("#13 Crash on start (open, by carol)\nLabels: bug\nComments: 3\nURL: https://github.com/octocat/hello/issues/13\nDescription:\nTrace attached");
        pr.ShouldBe("#42 in octocat/hello is a pull request, not an issue; read it with get_pull_request.");
    }

    [Fact]
    public async Task GetFileContents_Returns_Text_Lists_A_Directory_And_Explains_A_Binary()
    {
        var client = Client();
        client.GetFileContentAsync("octocat", "hello", "src/Program.cs", "main", Arg.Any<CancellationToken>())
            .Returns(new RepositoryFile("src/Program.cs", "file", "Console.WriteLine();", 20, []));
        client.GetFileContentAsync("octocat", "hello", "src", "", Arg.Any<CancellationToken>())
            .Returns(new RepositoryFile("src", "dir", null, 0, ["Program.cs (file)"]));
        client.GetFileContentAsync("octocat", "hello", "big.bin", "", Arg.Any<CancellationToken>())
            .Returns(new RepositoryFile("big.bin", "file", null, 5_000_000, []));
        client.GetFileContentAsync("octocat", "hello", "vendor/lib", "", Arg.Any<CancellationToken>())
            .Returns(new RepositoryFile("vendor/lib", "submodule", null, 0, []));
        var plugin = new GitHubPlugin(client);

        (await plugin.GetFileContentsAsync("octocat", "hello", "src/Program.cs", "main", CancellationToken.None))
            .ShouldBe("src/Program.cs (20 bytes) at main:\nConsole.WriteLine();");
        (await plugin.GetFileContentsAsync("octocat", "hello", "src", "", CancellationToken.None))
            .ShouldBe("src is a directory:\n  - Program.cs (file)");
        (await plugin.GetFileContentsAsync("octocat", "hello", "big.bin", "", CancellationToken.None))
            .ShouldBe("big.bin (5000000 bytes) has no text GitHub returns inline; it is binary or over 1 MB.");
        (await plugin.GetFileContentsAsync("octocat", "hello", "vendor/lib", "", CancellationToken.None))
            .ShouldBe("vendor/lib is a submodule, not a file; GitHub returns no text for it.");
    }

    [Theory]
    [InlineData("../evil", "hello", "README.md", "")]
    [InlineData("octocat", "hello world", "README.md", "")]
    [InlineData("octocat", "hello", "/etc/passwd", "")]
    [InlineData("octocat", "hello", "src/../../x", "")]
    [InlineData("octocat", "hello", "README.md", "main?x=1")]
    [InlineData("octocat", "hello", "README.md", "a..b")]
    public async Task GetFileContents_Refuses_A_Name_That_Could_Leave_The_Repository_Before_Any_Request(
        string owner, string repo, string path, string reference)
    {
        var client = Client();

        var result = await new GitHubPlugin(client).GetFileContentsAsync(owner, repo, path, reference, CancellationToken.None);

        result.ShouldBeOneOf(GitHubNames.InvalidRepository, GitHubNames.InvalidPath, GitHubNames.InvalidReference);
        await client.DidNotReceiveWithAnyArgs().GetFileContentAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public async Task A_Non_Positive_Number_Is_Refused_Before_Any_Request()
    {
        var client = Client();
        var plugin = new GitHubPlugin(client);

        (await plugin.GetPullRequestAsync("octocat", "hello", 0, CancellationToken.None)).ShouldBe("The pull request number must be positive.");
        (await plugin.GetIssueAsync("octocat", "hello", -1, CancellationToken.None)).ShouldBe("The issue number must be positive.");
        await client.DidNotReceiveWithAnyArgs().GetPullRequestAsync(default!, default!, default, default);
        await client.DidNotReceiveWithAnyArgs().GetIssueAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task A_Failing_Call_Becomes_A_Clean_Line_The_Model_Can_Relay()
    {
        var client = Client();
        client.GetPullRequestAsync("octocat", "hello", 42, Arg.Any<CancellationToken>())
            .Returns<PullRequestDetails?>(_ => throw new GitHubApiException("GitHub is currently unavailable. Try again later."));
        client.GetIssueAsync("octocat", "hello", 13, Arg.Any<CancellationToken>())
            .Returns<IssueDetails?>(_ => throw new TaskCanceledException());
        client.GetFileContentAsync("octocat", "hello", "a.cs", "", Arg.Any<CancellationToken>())
            .Returns<RepositoryFile?>(_ => throw new HttpRequestException("reset"));
        var plugin = new GitHubPlugin(client);

        (await plugin.GetPullRequestAsync("octocat", "hello", 42, CancellationToken.None)).ShouldBe("GitHub is currently unavailable. Try again later.");
        (await plugin.GetIssueAsync("octocat", "hello", 13, CancellationToken.None)).ShouldBe("GitHub request timed out. Try again later.");
        (await plugin.GetFileContentsAsync("octocat", "hello", "a.cs", "", CancellationToken.None)).ShouldBe("Could not reach GitHub. Try again later.");
    }

    [Fact]
    public async Task The_Existing_List_Functions_Also_Refuse_An_Invalid_Repository_Name()
    {
        var client = Client();
        var plugin = new GitHubPlugin(client);

        (await plugin.ListPullRequestsAsync("octo cat", "hello", CancellationToken.None)).ShouldBe(GitHubNames.InvalidRepository);
        (await plugin.ListIssuesAsync("octocat", "../x", CancellationToken.None)).ShouldBe(GitHubNames.InvalidRepository);
        (await plugin.GetRepositoryAsync("", "hello", CancellationToken.None)).ShouldBe(GitHubNames.InvalidRepository);
        await client.DidNotReceiveWithAnyArgs().ListOpenPullRequestsAsync(default!, default!, default);
    }

    [Fact]
    public async Task ListRepositoryFiles_Counts_By_Folder_And_Extension_And_Lists_The_Paths_The_Filters_Keep()
    {
        var client = Client();
        client.ListRepositoryFilesAsync("octocat", "hello", "", Arg.Any<CancellationToken>())
            .Returns(new RepositoryTree("main", false, [
                new RepositoryEntry("src/A.cs", 10), new RepositoryEntry("src/B.cs", 20), new RepositoryEntry("src/ui/app.ts", 30),
                new RepositoryEntry("tests/A.Tests.cs", 40), new RepositoryEntry("README.md", 5), new RepositoryEntry("Makefile", 1),
            ]));
        var plugin = new GitHubPlugin(client);

        var all = await plugin.ListRepositoryFilesAsync("octocat", "hello", "", "", "", CancellationToken.None);
        var filtered = await plugin.ListRepositoryFilesAsync("octocat", "hello", "", "src/", "cs", CancellationToken.None);
        var byFolder = await plugin.ListRepositoryFilesAsync("octocat", "hello", "", "src/", "", CancellationToken.None);

        all.ShouldStartWith("octocat/hello at main: 6 file(s)\nBy top-level folder: src/ 3, (root) 2, tests/ 1\nBy extension: .cs 3, (none) 1, .md 1, .ts 1\nFiles: 6");
        all.ShouldContain("\n  Makefile (1 bytes)");
        filtered.ShouldContain("Files matching prefix \"src/\" and extensions \".cs\": 2\n  src/A.cs (10 bytes)\n  src/B.cs (20 bytes)");
        filtered.ShouldNotContain("app.ts");
        byFolder.ShouldContain("Files matching prefix \"src/\": 3");
        byFolder.ShouldNotContain("extensions");
    }

    [Fact]
    public async Task ListRepositoryFiles_Cuts_The_Listing_At_The_Cap_And_Says_So()
    {
        var client = Client();
        var entries = Enumerable.Range(1, GitHubPlugin.ListedPathsCap + 25).Select(i => new RepositoryEntry($"src/f{i:D4}.cs", i)).ToList();
        client.ListRepositoryFilesAsync("octocat", "hello", "", Arg.Any<CancellationToken>()).Returns(new RepositoryTree("main", true, entries));

        var result = await new GitHubPlugin(client).ListRepositoryFilesAsync("octocat", "hello", "", "", "", CancellationToken.None);

        result.ShouldContain("GitHub cut the listing, so this is a prefix of the repository");
        result.ShouldEndWith($"... 25 more; narrow with prefix or extensions");
        result.Split('\n').Count(l => l.StartsWith("  src/f", StringComparison.Ordinal)).ShouldBe(GitHubPlugin.ListedPathsCap);
    }

    [Fact]
    public async Task SearchCode_Formats_The_Hits_With_Their_Fragments_And_Says_When_Nothing_Matches()
    {
        var client = Client();
        client.SearchCodeAsync("octocat", "hello", "catch (Exception)", "", "", Arg.Any<CancellationToken>())
            .Returns(new CodeSearchResult(41, [new CodeSearchHit("src/Checkout.cs", ["  catch (Exception)\n  {\n  }", "second", "third"]), new CodeSearchHit("src/Old.cs", [])]));
        client.SearchCodeAsync("octocat", "hello", "nothing", "", "", Arg.Any<CancellationToken>())
            .Returns(new CodeSearchResult(0, []));
        var plugin = new GitHubPlugin(client);

        var hits = await plugin.SearchCodeAsync("octocat", "hello", "catch (Exception)", "", "", CancellationToken.None);
        var none = await plugin.SearchCodeAsync("octocat", "hello", "nothing", "", "", CancellationToken.None);

        hits.ShouldBe("41 file(s) in octocat/hello match \"catch (Exception)\" (default branch; first 2 shown)\n--- src/Checkout.cs\ncatch (Exception)\n  {\n  }\nsecond\n--- src/Old.cs");
        none.ShouldBe("No file in octocat/hello matches \"nothing\" (default branch).");
    }

    [Theory]
    [InlineData("", "", "")]
    [InlineData("secret repo:evil/other", "", "")]
    [InlineData("ORG:evil token", "", "")]
    [InlineData("token", "../x", "")]
    [InlineData("token", "", "cs ts")]
    [InlineData("token", "", ".")]
    public async Task SearchCode_Refuses_A_Query_That_Could_Leave_The_Repository_Before_Any_Request(string query, string path, string extension)
    {
        var client = Client();

        var result = await new GitHubPlugin(client).SearchCodeAsync("octocat", "hello", query, path, extension, CancellationToken.None);

        result.ShouldBeOneOf(GitHubNames.InvalidQuery, GitHubNames.InvalidPath, GitHubNames.InvalidExtension);
        await client.DidNotReceiveWithAnyArgs().SearchCodeAsync(default!, default!, default!, default, default, default);
    }
}
