using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Scout.Plugin.GitHub.Abstractions;
using Scout.Plugin.GitHub.Tests.Fakes;

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
        result.ShouldContain("\n1 file(s) GitHub sent no diff for (binary or too large), no section under Diffs: img/logo.png\nDiffs:\n");
        result.ShouldNotContain("--- img/logo.png");
    }

    [Fact]
    public async Task A_Pull_Request_Whose_Diffs_All_Fit_Reads_Exactly_As_Before()
    {
        var client = Client();
        client.GetPullRequestAsync("octocat", "hello", 42, Arg.Any<CancellationToken>())
            .Returns(PullRequest(
                new ChangedFile("src/Checkout.cs", "modified", 9, 2, "@@ -1 +1 @@\n-old\n+new"),
                new ChangedFile("ui/app.ts", "added", 1, 0, "@@ -0,0 +1 @@\n+export {}"),
                new ChangedFile("img/logo.png", "added", 0, 0, null)));

        var result = await new GitHubPlugin(client).GetPullRequestAsync("octocat", "hello", 42, CancellationToken.None);

        result.ShouldBe("""
            #42 Add checkout (open, by alice)
            Branches: feat/checkout -> main at 0123456
            URL: https://github.com/octocat/hello/pull/42
            Changes: +10 -2 in 3 file(s)
            Extensions: .cs, .png, .ts
            Description:
            Sums the order.
            Files:
              src/Checkout.cs (modified, +9 -2)
              ui/app.ts (added, +1 -0)
              img/logo.png (added, +0 -0)
            1 file(s) GitHub sent no diff for (binary or too large), no section under Diffs: img/logo.png
            Diffs:
            --- src/Checkout.cs
            @@ -1 +1 @@
            -old
            +new
            --- ui/app.ts
            @@ -0,0 +1 @@
            +export {}
            """.ReplaceLineEndings("\n"));
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
        result.ShouldNotContain("--- b.cs");
        result.ShouldContain("--- c.cs\ntiny");
        result.ShouldContain("\n1 of 3 file(s) had their diff left out to fit the budget and have no section under Diffs; to review the whole pull request, start a review job with start_pull_request_review; to read one file, use get_pull_request_file.\nDiffs:\n", customMessage: "a pull request whose diffs were left out names start_pull_request_review, and get_pull_request_file for one file");
    }

    [Fact]
    public async Task Files_With_No_Diff_From_GitHub_Get_One_Line_Naming_At_Most_A_Hundred_And_No_Section()
    {
        var client = Client();
        client.GetPullRequestAsync("octocat", "hello", 42, Arg.Any<CancellationToken>())
            .Returns(PullRequest([.. Enumerable.Range(1, 150).Select(i => new ChangedFile($"img/i{i}.png", "added", 0, 0, null))]));

        var result = await new GitHubPlugin(client).GetPullRequestAsync("octocat", "hello", 42, CancellationToken.None);

        result.ShouldContain("\n150 file(s) GitHub sent no diff for (binary or too large), no section under Diffs: "
            + string.Join(", ", Enumerable.Range(1, 100).Select(i => $"img/i{i}.png")) + ", and 50 more\nDiffs:");
        result.ShouldNotContain("--- ");
        result.ShouldEndWith("\nDiffs:");
    }

    [Fact]
    public async Task GetPullRequestFile_Names_At_Most_A_Hundred_Files_When_The_Path_Is_Absent()
    {
        var client = Client();
        client.ListPullRequestFilesAsync("octocat", "hello", 42, Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, 150).Select(i => new ChangedFile($"f{i}.cs", "modified", 1, 0, "@@")).ToList());

        var absent = await new GitHubPlugin(client).GetPullRequestFileAsync("octocat", "hello", 42, "src/Other.cs", CancellationToken.None);

        absent.ShouldBe("Pull request #42 does not change src/Other.cs. Files: "
            + string.Join(", ", Enumerable.Range(1, 100).Select(i => $"f{i}.cs")) + ", and 50 more");
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
        var listed = Enumerable.Range(1, 3000).Select(i => new ChangedFile($"f{i}.cs", "modified", 1, 0, "@@")).ToList();
        client.ListPullRequestFilesAsync("octocat", "hello", 42, Arg.Any<CancellationToken>()).Returns(listed);
        client.GetPullRequestAsync("octocat", "hello", 42, Arg.Any<CancellationToken>())
            .Returns(PullRequest([.. listed]) with { ChangedFileCount = 3200 });
        var plugin = new GitHubPlugin(client);

        var file = await plugin.GetPullRequestFileAsync("octocat", "hello", 42, "f3100.cs", CancellationToken.None);
        var overview = await plugin.GetPullRequestAsync("octocat", "hello", 42, CancellationToken.None);

        file.ShouldBe("f3100.cs is not among the 3000 files GitHub lists for pull request #42, the most it lists for one pull request; the pull request may change more files than GitHub can list.");
        overview.ShouldContain("in 3200 file(s); only the first 3000 are listed below, so this review covers part of the pull request");
    }

    private static (GitHubPlugin Plugin, StubHttpMessageHandler Handler) OverHttp()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        return (new GitHubPlugin(new GitHubClient(http)), handler);
    }

    private static string PullRequestJson(int changedFiles) =>
        $$"""{"number":42,"title":"Big","user":{"login":"alice"},"base":{"ref":"main"},"head":{"ref":"feat/big","sha":"0123456789abcdef"},"changed_files":{{changedFiles}}}""";

    [Fact]
    public async Task A_File_Past_The_First_Hundred_Can_Be_Read()
    {
        var (plugin, handler) = OverHttp();
        handler.EnqueueJson(System.Net.HttpStatusCode.OK, GitHubClientReadsTests.FilesPage(1, 100));
        handler.EnqueueJson(System.Net.HttpStatusCode.OK, GitHubClientReadsTests.FilesPage(101, 27));

        var file = await plugin.GetPullRequestFileAsync("octocat", "hello", 42, "src/f120.cs", CancellationToken.None);

        file.ShouldBe("src/f120.cs (modified, +1 -0)\n@@ f120");
    }

    [Fact]
    public async Task GetPullRequest_Names_Every_File_And_Says_How_Many_Diffs_Were_Left_Out()
    {
        var (plugin, handler) = OverHttp();
        static string Padded(int first, int count) => "[" + string.Join(",", Enumerable.Range(first, count).Select(i =>
            $$"""{"filename":"src/f{{i}}.cs","status":"modified","additions":1,"deletions":0,"patch":"{{new string('x', 200)}}"}""")) + "]";
        handler.EnqueueJson(System.Net.HttpStatusCode.OK, PullRequestJson(127));
        handler.EnqueueJson(System.Net.HttpStatusCode.OK, Padded(1, 100));
        handler.EnqueueJson(System.Net.HttpStatusCode.OK, Padded(101, 27));

        var result = await plugin.GetPullRequestAsync("octocat", "hello", 42, CancellationToken.None);

        foreach (var i in Enumerable.Range(1, 127))
        {
            result.ShouldContain($"\n  src/f{i}.cs (modified, +1 -0)\n");
        }

        result.ShouldContain("in 127 file(s)\nExtensions: .cs\n");
        result.ShouldContain("\n67 of 127 file(s) had their diff left out to fit the budget and have no section under Diffs; to review the whole pull request, start a review job with start_pull_request_review; to read one file, use get_pull_request_file.\nDiffs:\n");
        result.Split("\n--- ").Length.ShouldBe(61);
        result.ShouldContain("\n--- src/f60.cs\n");
        result.ShouldNotContain("--- src/f61.cs");
    }

    [Fact]
    public async Task The_Files_Section_Stops_At_Its_Cap_And_A_File_Beyond_It_Is_Still_Read()
    {
        var (plugin, handler) = OverHttp();
        handler.EnqueueJson(System.Net.HttpStatusCode.OK, PullRequestJson(3000));
        foreach (var round in Enumerable.Range(0, 2))
        {
            foreach (var page in Enumerable.Range(0, 30))
            {
                handler.EnqueueJson(System.Net.HttpStatusCode.OK, GitHubClientReadsTests.FilesPage((page * 100) + 1, 100));
            }
        }

        var result = await plugin.GetPullRequestAsync("octocat", "hello", 42, CancellationToken.None);
        var file = await plugin.GetPullRequestFileAsync("octocat", "hello", 42, "src/f2500.cs", CancellationToken.None);

        var files = result[result.IndexOf("\nFiles:", StringComparison.Ordinal)..result.IndexOf("\nDiffs:", StringComparison.Ordinal)];
        files.ShouldContain("\n  src/f1851.cs (modified, +1 -0)\n1149 more files not listed here; read one with get_pull_request_file by its path\n");
        files.ShouldNotContain("src/f1852.cs");
        files.Length.ShouldBeLessThan(GitHubPlugin.FilesTextCap + 300);
        file.ShouldBe("src/f2500.cs (modified, +1 -0)\n@@ f2500");
    }

    [Fact]
    public async Task GetPullRequest_Answers_Only_The_Reason_When_A_Later_Page_Of_Files_Fails()
    {
        var (plugin, handler) = OverHttp();
        handler.EnqueueJson(System.Net.HttpStatusCode.OK, PullRequestJson(127));
        handler.EnqueueJson(System.Net.HttpStatusCode.OK, GitHubClientReadsTests.FilesPage(1, 100));
        handler.EnqueueJson(System.Net.HttpStatusCode.BadGateway, "");

        var result = await plugin.GetPullRequestAsync("octocat", "hello", 42, CancellationToken.None);

        result.ShouldBe("GitHub is currently unavailable. Try again later.");
    }

    [Fact]
    public async Task A_Listing_That_Never_Ends_Stops_At_The_Bound_And_Says_The_List_Was_Cut()
    {
        var (plugin, handler) = OverHttp();
        handler.EnqueueJson(System.Net.HttpStatusCode.OK, PullRequestJson(3500));
        foreach (var page in Enumerable.Range(0, 40))
        {
            handler.EnqueueJson(System.Net.HttpStatusCode.OK, GitHubClientReadsTests.FilesPage((page * 100) + 1, 100));
        }

        var result = await plugin.GetPullRequestAsync("octocat", "hello", 42, CancellationToken.None);

        handler.Requests.Count.ShouldBe(31);
        handler.Requests[^1].Query.ShouldBe("?per_page=100&page=30");
        result.ShouldContain("in 3500 file(s); only the first 3000 are listed below, so this review covers part of the pull request");
        result.ShouldContain("\n1149 more files not listed here; read one with get_pull_request_file by its path\n");
        result.ShouldNotContain("src/f3001.cs");
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

        (await plugin.GetFileContentsAsync("octocat", "hello", "src/Program.cs", "main", ct: CancellationToken.None))
            .ShouldBe("src/Program.cs (20 bytes) at main, lines 1 to 1 of 1; the file ends there:\nConsole.WriteLine();");
        (await plugin.GetFileContentsAsync("octocat", "hello", "src", "", ct: CancellationToken.None))
            .ShouldBe("src is a directory:\n  - Program.cs (file)");
        (await plugin.GetFileContentsAsync("octocat", "hello", "big.bin", "", ct: CancellationToken.None))
            .ShouldBe("big.bin (5000000 bytes) has no text GitHub returns inline; it is binary or over 1 MB.");
        (await plugin.GetFileContentsAsync("octocat", "hello", "vendor/lib", "", ct: CancellationToken.None))
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

        var result = await new GitHubPlugin(client).GetFileContentsAsync(owner, repo, path, reference, ct: CancellationToken.None);

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
        (await plugin.GetFileContentsAsync("octocat", "hello", "a.cs", "", ct: CancellationToken.None)).ShouldBe("Could not reach GitHub. Try again later.");
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
        var entries = Enumerable.Range(1, GitHubPlugin.ListedPathsCap + 25).Select(i => new RepositoryEntry($"src/f{i.ToString("D4", CultureInfo.InvariantCulture)}.cs", i)).ToList();
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

        var hits = await plugin.SearchCodeAsync("octocat", "hello", "catch (Exception)", "", "", ct: CancellationToken.None);
        var none = await plugin.SearchCodeAsync("octocat", "hello", "nothing", "", "", ct: CancellationToken.None);

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

        var result = await new GitHubPlugin(client).SearchCodeAsync("octocat", "hello", query, path, extension, ct: CancellationToken.None);

        result.ShouldBeOneOf(GitHubNames.InvalidQuery, GitHubNames.InvalidPath, GitHubNames.InvalidExtension);
        await client.DidNotReceiveWithAnyArgs().SearchCodeAsync(default!, default!, default!, default, default, default);
    }

    private static async Task<RepositorySnapshotStore> SnapshotOfHello(string dir)
    {
        var store = new RepositorySnapshotStore(dir, 0, new FakeTimeProvider());
        await store.ExtractAsync("octocat", "hello", new RepositoryCommit("main", "abc1234def5678", true), new MemoryStream(TarGzFixture.Build(
            TarGzFixture.Text("src/Checkout.cs", "class Checkout { decimal Total() => 0; }\n"),
            TarGzFixture.Text("README.md", "# Hello\n"))), CancellationToken.None);
        return store;
    }

    [Fact]
    public async Task With_A_Snapshot_The_File_The_Listing_And_The_Search_Are_Served_Locally_And_Say_So_While_Another_Reference_Still_Asks_GitHub()
    {
        var dir = Directory.CreateTempSubdirectory("scout-reads-snapshot-").FullName;
        try
        {
            var client = Client();
            client.GetFileContentAsync("octocat", "hello", "src/Checkout.cs", "feat/x", Arg.Any<CancellationToken>())
                .Returns(new RepositoryFile("src/Checkout.cs", "file", "from GitHub", 11, []));
            var plugin = new GitHubPlugin(client, await SnapshotOfHello(dir));

            var file = await plugin.GetFileContentsAsync("octocat", "hello", "src/Checkout.cs", "", ct: CancellationToken.None);
            var folder = await plugin.GetFileContentsAsync("octocat", "hello", "src", "main", ct: CancellationToken.None);
            var listing = await plugin.ListRepositoryFilesAsync("octocat", "hello", "", "", "cs", CancellationToken.None);
            var search = await plugin.SearchCodeAsync("octocat", "hello", "Total()", "", "", ct: CancellationToken.None);
            var other = await plugin.GetFileContentsAsync("octocat", "hello", "src/Checkout.cs", "feat/x", ct: CancellationToken.None);

            file.ShouldBe("src/Checkout.cs (41 bytes) at main (local snapshot at abc1234), lines 1 to 1 of 1; the file ends there:\nclass Checkout { decimal Total() => 0; }\n");
            folder.ShouldBe("src is a directory (local snapshot at abc1234):\n  - Checkout.cs");
            listing.ShouldContain("octocat/hello at main: 2 file(s)");
            listing.ShouldContain("  src/Checkout.cs (41 bytes)");
            listing.ShouldEndWith("(from the local snapshot at abc1234)");
            search.ShouldStartWith("1 file(s) in octocat/hello match \"Total()\"");
            search.ShouldContain("--- src/Checkout.cs\nclass Checkout { decimal Total() => 0; }");
            search.ShouldStartWith("1 file(s) in octocat/hello match \"Total()\" (the local snapshot of main at abc1234, every file searched; first 1 shown)", Case.Sensitive, "the first line says which snapshot was searched");
            other.ShouldContain("from GitHub");
            await client.DidNotReceive().ListRepositoryFilesAsync("octocat", "hello", Arg.Any<string?>(), Arg.Any<CancellationToken>());
            await client.DidNotReceiveWithAnyArgs().SearchCodeAsync(default!, default!, default!, default, default, default);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static readonly string LongFile = string.Concat(Enumerable.Range(1, 3_000).Select(i => string.Create(CultureInfo.InvariantCulture, $"    var line{i:D4} = Compute({i});\n")));

    private static GitHubPlugin Reading(IGitHubClient client, string text)
    {
        client.GetFileContentAsync("octocat", "hello", "src/Long.cs", "main", Arg.Any<CancellationToken>())
            .Returns(new RepositoryFile("src/Long.cs", "file", text, text.Length, []));
        return new GitHubPlugin(client);
    }

    private static async Task<List<string>> ReadToTheEnd(GitHubPlugin plugin, string path, string reference)
    {
        var answers = new List<string>();
        var firstLine = 1;
        while (answers.Count < 100)
        {
            var answer = await plugin.GetFileContentsAsync("octocat", "hello", path, reference, firstLine, ct: CancellationToken.None);
            answers.Add(answer);
            var header = answer[..answer.IndexOf('\n', StringComparison.Ordinal)];
            const string Next = "call again with firstLine ";
            var at = header.IndexOf(Next, StringComparison.Ordinal);
            if (at < 0)
            {
                break;
            }

            firstLine = int.Parse(header[(at + Next.Length)..^1], CultureInfo.InvariantCulture);
        }

        return answers;
    }

    private static string Body(string answer) => answer[(answer.IndexOf('\n', StringComparison.Ordinal) + 1)..];

    [Fact]
    public async Task R1_A_File_Longer_Than_One_Answer_Is_Read_To_Its_End_By_Following_Each_Answer_And_The_Parts_Joined_Are_The_File()
    {
        var answers = await ReadToTheEnd(Reading(Client(), LongFile), "src/Long.cs", "main");

        answers.Count.ShouldBeGreaterThan(1, "a file longer than one answer comes in parts");
        answers.ShouldAllBe(a => Body(a).Length <= GitHubPlugin.PatchCap, "each part fits the bound of one answer");
        answers[0].ShouldStartWith("src/Long.cs (100893 bytes) at main, lines 1 to 609 of 3,000; to read on, call again with firstLine 610:\n", Case.Sensitive, "the header says which lines, how many and the next call");
        answers[^1].ShouldStartWith("src/Long.cs (100893 bytes) at main, lines 2,973 to 3,000 of 3,000; the file ends there:\n", Case.Sensitive, "the last part says the file ends");
        string.Concat(answers.Select(Body)).ShouldBe(LongFile, "the parts joined are the file");
    }

    [Fact]
    public async Task R1_A_Snapshot_File_Longer_Than_One_Answer_Is_Read_To_Its_End_The_Same_Way()
    {
        var dir = Directory.CreateTempSubdirectory("scout-reads-lines-").FullName;
        try
        {
            var store = new RepositorySnapshotStore(dir, 0, new FakeTimeProvider());
            await store.ExtractAsync("octocat", "hello", new RepositoryCommit("main", "abc1234def5678", true), new MemoryStream(TarGzFixture.Build(
                TarGzFixture.Text("src/Long.cs", LongFile))), CancellationToken.None);
            var client = Client();

            var answers = await ReadToTheEnd(new GitHubPlugin(client, store), "src/Long.cs", "");

            answers[0].ShouldStartWith("src/Long.cs (100893 bytes) at main (local snapshot at abc1234), lines 1 to 609 of 3,000; to read on, call again with firstLine 610:\n", Case.Sensitive, "the snapshot's header stays in front");
            string.Concat(answers.Select(Body)).ShouldBe(LongFile, "the parts joined are the file");
            client.ReceivedCalls().ShouldBeEmpty("a snapshot read asks GitHub nothing");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task R2_The_Lines_Around_A_Given_Line_Can_Be_Asked_For_Directly()
    {
        var answer = await Reading(Client(), LongFile).GetFileContentsAsync("octocat", "hello", "src/Long.cs", "main", 1_200, 5, CancellationToken.None);

        answer.ShouldBe(
            "src/Long.cs (100893 bytes) at main, lines 1,200 to 1,204 of 3,000; to read on, call again with firstLine 1205:\n"
            + string.Concat(Enumerable.Range(1_200, 5).Select(i => string.Create(CultureInfo.InvariantCulture, $"    var line{i:D4} = Compute({i});\n"))),
            "the lines asked for, and no more");
    }

    [Fact]
    public async Task R3_A_Read_Of_Any_Lines_Reaches_The_Same_Repository_Reference_And_Path_As_A_Whole_Read_And_A_Refused_Name_Stays_Refused()
    {
        var client = Client();
        var plugin = Reading(client, LongFile);

        await plugin.GetFileContentsAsync("octocat", "hello", "src/Long.cs", "main", 2_000, 10, CancellationToken.None);
        var refused = await plugin.GetFileContentsAsync("octocat", "hello", "src/../../x", "main", 2_000, 10, CancellationToken.None);

        client.ReceivedCalls().Select(c => (c.GetMethodInfo().Name, string.Join(",", c.GetArguments().Take(4))))
            .ShouldBe([("GetFileContentAsync", "octocat,hello,src/Long.cs,main")], "the slice is cut from the same file; nothing else is asked");
        refused.ShouldBe(GitHubNames.InvalidPath, "a path that could leave the repository is refused before any request, with lines asked or not");
    }

    [Theory]
    [InlineData(0, 0, GitHubPlugin.FirstLineBelowOne)]
    [InlineData(-3, 0, GitHubPlugin.FirstLineBelowOne)]
    [InlineData(1, -1, GitHubPlugin.LineCountBelowZero)]
    [InlineData(3_001, 0, "src/Long.cs has 3,000 lines; firstLine 3001 is past its end.")]
    public async Task A_First_Line_Below_One_A_Negative_Count_Or_A_First_Line_Past_The_End_Is_Answered_With_Its_Sentence(int firstLine, int lineCount, string sentence)
    {
        var client = Client();

        var answer = await Reading(client, LongFile).GetFileContentsAsync("octocat", "hello", "src/Long.cs", "main", firstLine, lineCount, CancellationToken.None);

        answer.ShouldBe(sentence, "each out-of-range value has its own answer");
    }

    [Fact]
    public async Task A_Line_Longer_Than_One_Answer_Is_Cut_At_The_Bound_And_Says_So_And_An_Empty_File_Says_It_Is_Empty()
    {
        var text = new string('x', 25_000) + "\nnext\n";
        var client = Client();
        client.GetFileContentAsync("octocat", "hello", "empty.txt", "main", Arg.Any<CancellationToken>()).Returns(new RepositoryFile("empty.txt", "file", "", 0, []));

        var cut = await Reading(client, text).GetFileContentsAsync("octocat", "hello", "src/Long.cs", "main", ct: CancellationToken.None);
        var empty = await new GitHubPlugin(client).GetFileContentsAsync("octocat", "hello", "empty.txt", "main", ct: CancellationToken.None);

        cut.ShouldBe("src/Long.cs (25006 bytes) at main, line 1 of 2, cut at 20,000 characters because the line is longer; to read on, call again with firstLine 2:\n" + new string('x', 20_000), "a line longer than the bound is cut there, with a note");
        empty.ShouldBe("empty.txt (0 bytes) at main: the file is empty.", "an empty file says so");
    }

    [Fact]
    public async Task When_The_Read_Fails_With_Lines_Asked_The_Api_Read_Answers_Its_Clean_Line_And_The_Snapshot_Read_Says_Not_Found()
    {
        var dir = Directory.CreateTempSubdirectory("scout-reads-lines-").FullName;
        try
        {
            var client = Client();
            client.GetFileContentAsync("octocat", "hello", "a.cs", "feat/x", Arg.Any<CancellationToken>())
                .Returns<RepositoryFile?>(_ => throw new HttpRequestException("reset"));
            var plugin = new GitHubPlugin(client, await SnapshotOfHello(dir));

            var api = await plugin.GetFileContentsAsync("octocat", "hello", "a.cs", "feat/x", 40, 10, CancellationToken.None);
            var local = await plugin.GetFileContentsAsync("octocat", "hello", "missing.cs", "", 40, 10, CancellationToken.None);

            api.ShouldBe("Could not reach GitHub. Try again later.", "a failed API read answers the same clean line with lines asked");
            local.ShouldBe("missing.cs not found in octocat/hello at main (abc1234).", "a snapshot read that finds nothing says so with lines asked");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task C3_A_search_at_a_reference_reads_that_snapshot_and_asks_GitHub_nothing_and_one_at_a_reference_with_no_snapshot_searches_nothing()
    {
        var dir = Directory.CreateTempSubdirectory("scout-reads-search-").FullName;
        try
        {
            var store = await SnapshotOfHello(dir);
            await store.ExtractAsync("octocat", "hello", new RepositoryCommit("feat/x", "fff9999eee8888", false), new MemoryStream(TarGzFixture.Build(
                TarGzFixture.Text("src/Checkout.cs", "class Checkout { decimal Total() => 1; }\n"))), CancellationToken.None);
            var client = Client();
            var plugin = new GitHubPlugin(client, store);

            var head = await plugin.SearchCodeAsync("octocat", "hello", "Total()", reference: "abc1234def5678", ct: CancellationToken.None);
            var none = await plugin.SearchCodeAsync("octocat", "hello", "Total()", reference: "0123456789abcdef", ct: CancellationToken.None);
            var other = await plugin.SearchCodeAsync("someone", "else", "Total()", reference: "abc1234def5678", ct: CancellationToken.None);

            head.ShouldStartWith("1 file(s) in octocat/hello match \"Total()\" (the local snapshot of main at abc1234, every file searched; first 1 shown)", Case.Sensitive, "the search reads the snapshot at the reference named, not the newest");
            head.ShouldContain("Total() => 0", Case.Sensitive, "the text is that commit's");
            none.ShouldBe("Scout has no local snapshot of octocat/hello at 0123456789abcdef, and GitHub searches only the default branch, so nothing was searched; read the files at 0123456789abcdef with get_file_contents.", "a reference with no snapshot is never searched on GitHub's default branch instead");
            other.ShouldBe("Scout has no local snapshot of someone/else at abc1234def5678, and GitHub searches only the default branch, so nothing was searched; read the files at abc1234def5678 with get_file_contents.", "a snapshot of one repository is never read for another");
            client.ReceivedCalls().ShouldBeEmpty("a search at a reference asks GitHub nothing");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task When_The_Snapshot_Searched_Cannot_Be_Read_The_Search_Says_So_And_Asks_GitHub_Nothing()
    {
        var dir = Directory.CreateTempSubdirectory("scout-reads-search-").FullName;
        try
        {
            var store = await SnapshotOfHello(dir);
            var client = Client();
            var plugin = new GitHubPlugin(client, store);
            var snapshot = store.Find("octocat", "hello", "main").ShouldNotBeNull();
            File.CreateSymbolicLink(Path.Combine(snapshot.Root, "src", "Gone.cs"), Path.Combine(dir, "no-such-file"));

            var answer = await plugin.SearchCodeAsync("octocat", "hello", "Total()", reference: "main", ct: CancellationToken.None);

            answer.ShouldBe("The local snapshot of octocat/hello at abc1234 could not be read, so nothing was searched; read the files with get_file_contents.", "a snapshot that cannot be read is said, never searched on GitHub instead");
            client.ReceivedCalls().ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
