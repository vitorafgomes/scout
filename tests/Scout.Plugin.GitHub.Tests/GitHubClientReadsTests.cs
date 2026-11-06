using System.Net;
using Scout.Plugin.GitHub;
using Scout.Plugin.GitHub.Tests.Fakes;

namespace Scout.Plugin.GitHub.Tests;

public sealed class GitHubClientReadsTests
{
    private static (GitHubClient client, StubHttpMessageHandler handler) Build()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        return (new GitHubClient(http), handler);
    }

    [Fact]
    public async Task GetPullRequest_Reads_The_Pull_Request_Then_Its_Files_With_Their_Diffs()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """
            {"number":42,"title":"Add checkout","body":"Sums the order.","state":"open","draft":false,
             "user":{"login":"alice"},"base":{"ref":"main"},"head":{"ref":"feat/checkout","sha":"0123456789abcdef"},
             "additions":10,"deletions":2,"changed_files":2,"html_url":"https://github.com/octocat/hello/pull/42"}
            """);
        handler.EnqueueJson(HttpStatusCode.OK, """
            [{"filename":"src/Checkout.cs","status":"modified","additions":9,"deletions":2,"patch":"@@ -1,2 +1,9 @@\n-old\n+new"},
             {"filename":"img/logo.png","status":"added","additions":0,"deletions":0}]
            """);

        var pr = await client.GetPullRequestAsync("octocat", "hello", 42, CancellationToken.None);

        pr.ShouldNotBeNull();
        pr.Title.ShouldBe("Add checkout");
        pr.Body.ShouldBe("Sums the order.");
        pr.AuthorLogin.ShouldBe("alice");
        pr.BaseRef.ShouldBe("main");
        pr.HeadRef.ShouldBe("feat/checkout");
        pr.HeadSha.ShouldBe("0123456789abcdef");
        pr.Additions.ShouldBe(10);
        pr.Files.Count.ShouldBe(2);
        pr.Files[0].Patch.ShouldBe("@@ -1,2 +1,9 @@\n-old\n+new");
        pr.Files[1].Patch.ShouldBeNull();
        pr.ChangedFileCount.ShouldBe(2);
        pr.FilesTruncated.ShouldBeFalse();
        handler.LastRequest!.RequestUri!.ToString().ShouldEndWith("repos/octocat/hello/pulls/42/files?per_page=100");
    }

    [Fact]
    public async Task GetPullRequest_Knows_When_GitHub_Lists_Fewer_Files_Than_It_Counts()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """{"number":7,"title":"Big","user":{"login":"a"},"changed_files":140}""");
        handler.EnqueueJson(HttpStatusCode.OK, """[{"filename":"a.cs","status":"modified","additions":1,"deletions":0}]""");

        var pr = await client.GetPullRequestAsync("octocat", "hello", 7, CancellationToken.None);

        pr.ShouldNotBeNull();
        pr.ChangedFileCount.ShouldBe(140);
        pr.FilesTruncated.ShouldBeTrue();
    }

    [Fact]
    public async Task ListPullRequestFiles_Answers_Null_On_404_And_Reads_Only_The_Files_Endpoint()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.NotFound, """{"message":"Not Found"}""");

        var files = await client.ListPullRequestFilesAsync("octocat", "hello", 9999, CancellationToken.None);

        files.ShouldBeNull();
        handler.LastRequest!.RequestUri!.ToString().ShouldEndWith("repos/octocat/hello/pulls/9999/files?per_page=100");
    }

    [Fact]
    public async Task ListPullRequestComments_Answers_Null_When_The_Pull_Request_Is_Missing()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.NotFound, """{"message":"Not Found"}""");
        handler.EnqueueJson(HttpStatusCode.NotFound, """{"message":"Not Found"}""");

        var comments = await client.ListPullRequestCommentsAsync("octocat", "hello", 9999, CancellationToken.None);

        comments.ShouldBeNull();
    }

    [Fact]
    public async Task GetPullRequest_Answers_Null_On_404_And_Reads_No_Files()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.NotFound, """{"message":"Not Found"}""");

        var pr = await client.GetPullRequestAsync("octocat", "hello", 404, CancellationToken.None);

        pr.ShouldBeNull();
        handler.LastRequest!.RequestUri!.ToString().ShouldEndWith("repos/octocat/hello/pulls/404");
    }

    [Fact]
    public async Task GetPullRequest_Maps_A_Server_Error_To_The_Unavailable_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.BadGateway, "");

        var ex = await Should.ThrowAsync<GitHubApiException>(
            () => client.GetPullRequestAsync("octocat", "hello", 42, CancellationToken.None));

        ex.Message.ShouldBe("GitHub is currently unavailable. Try again later.");
    }

    [Fact]
    public async Task GetPullRequest_Lets_A_Timeout_Bubble()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<TaskCanceledException>(
            () => client.GetPullRequestAsync("octocat", "hello", 42, CancellationToken.None));
    }

    [Fact]
    public async Task ListPullRequestComments_Merges_Review_And_Conversation_Comments_Oldest_First()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """
            [{"user":{"login":"bob"},"body":"Null here","path":"src/Checkout.cs","line":88,"original_line":80,"created_at":"2026-11-06T10:00:00Z"},
             {"user":{"login":"bob"},"body":"Line gone","path":"src/Old.cs","line":null,"original_line":5,"created_at":"2026-11-06T11:00:00Z"}]
            """);
        handler.EnqueueJson(HttpStatusCode.OK, """
            [{"user":{"login":"alice"},"body":"Thanks, fixing.","created_at":"2026-11-06T10:30:00Z"}]
            """);

        var comments = await client.ListPullRequestCommentsAsync("octocat", "hello", 42, CancellationToken.None);

        comments.ShouldNotBeNull().Select(c => (c.AuthorLogin, c.Path, c.Line)).ShouldBe([
            ("bob", "src/Checkout.cs", 88),
            ("alice", null, null),
            ("bob", "src/Old.cs", 5),
        ]);
    }

    [Fact]
    public async Task GetIssue_Reads_Labels_And_Flags_A_Pull_Request_Number()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """
            {"number":13,"title":"Crash on start","body":"Stack trace attached","state":"open","user":{"login":"carol"},
             "labels":[{"name":"bug"},{"name":"p1"}],"comments":3,"html_url":"https://github.com/octocat/hello/issues/13"}
            """);
        handler.EnqueueJson(HttpStatusCode.OK, """
            {"number":42,"title":"Add checkout","state":"open","user":{"login":"alice"},"labels":[],"comments":0,
             "html_url":"https://github.com/octocat/hello/pull/42","pull_request":{"url":"https://api.github.com/repos/octocat/hello/pulls/42"}}
            """);

        var issue = await client.GetIssueAsync("octocat", "hello", 13, CancellationToken.None);
        var pr = await client.GetIssueAsync("octocat", "hello", 42, CancellationToken.None);

        issue.ShouldNotBeNull();
        issue.Labels.ShouldBe(["bug", "p1"]);
        issue.CommentCount.ShouldBe(3);
        issue.IsPullRequest.ShouldBeFalse();
        pr.ShouldNotBeNull().IsPullRequest.ShouldBeTrue();
    }

    [Fact]
    public async Task GetFileContent_Decodes_A_Base64_File_And_Escapes_The_Path_And_Reference()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """
            {"type":"file","encoding":"base64","size":11,"content":"aGVsbG8g\nd29ybGQ=\n"}
            """);

        var file = await client.GetFileContentAsync("octocat", "hello", "src/a b/Program.cs", "feat/x", CancellationToken.None);

        file.ShouldNotBeNull();
        file.IsDirectory.ShouldBeFalse();
        file.Text.ShouldBe("hello world");
        file.Size.ShouldBe(11);
        handler.LastRequest!.RequestUri!.AbsoluteUri
            .ShouldEndWith("repos/octocat/hello/contents/src/a%20b/Program.cs?ref=feat%2Fx");
    }

    [Fact]
    public async Task GetFileContent_Lists_A_Directory_And_Answers_Null_On_404()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """
            [{"name":"Program.cs","type":"file"},{"name":"Orders","type":"dir"}]
            """);
        handler.EnqueueJson(HttpStatusCode.NotFound, """{"message":"Not Found"}""");

        var directory = await client.GetFileContentAsync("octocat", "hello", "src", null, CancellationToken.None);
        var missing = await client.GetFileContentAsync("octocat", "hello", "nope.cs", null, CancellationToken.None);

        directory.ShouldNotBeNull();
        directory.IsDirectory.ShouldBeTrue();
        directory.Entries.ShouldBe(["Program.cs (file)", "Orders (dir)"]);
        missing.ShouldBeNull();
    }

    [Fact]
    public async Task GetFileContent_Without_Inline_Content_Has_No_Text()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """{"type":"file","encoding":"none","size":5000000,"content":""}""");

        var file = await client.GetFileContentAsync("octocat", "hello", "big.bin", "", CancellationToken.None);

        file.ShouldNotBeNull().Text.ShouldBeNull();
        file.Kind.ShouldBe("file");
        handler.LastRequest!.RequestUri!.ToString().ShouldEndWith("repos/octocat/hello/contents/big.bin");
    }

    [Fact]
    public async Task GetFileContent_Names_A_Submodule_Or_Symlink_Instead_Of_Calling_It_Binary()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """{"type":"submodule","size":0,"submodule_git_url":"https://github.com/x/y.git"}""");

        var file = await client.GetFileContentAsync("octocat", "hello", "vendor/lib", "", CancellationToken.None);

        file.ShouldNotBeNull();
        file.Kind.ShouldBe("submodule");
        file.IsDirectory.ShouldBeFalse();
        file.Text.ShouldBeNull();
    }
}
