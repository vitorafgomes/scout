using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.Llm.Abstractions;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub.Tests;

public sealed class GitHubBotIdentityTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string OwnerToken = "ghu_owner_token_not_real";
    private const string BotToken = "ghs_bot_token_not_real";

    private readonly string _dir = Directory.CreateTempSubdirectory("scout-github-bot-").FullName;
    private readonly List<(string Method, string Path, string? Authorization)> _requests = [];
    private Func<HttpRequestMessage, HttpResponseMessage> _respond = Answer;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File(string name, string token)
    {
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllText(path, $$"""{"access_token":"{{token}}"}""");
        return path;
    }

    private static HttpResponseMessage Answer(HttpRequestMessage request)
    {
        var path = request.RequestUri!.PathAndQuery;
        var json = (request.Method.Method, path) switch
        {
            ("GET", "/repos/octocat/hello/pulls/42") => $$$"""{"number":42,"user":{"login":"octocat"},"head":{"ref":"feat/x","sha":"{{{Sha}}}"}}""",
            ("GET", "/user") => """{"login":"octocat"}""",
            ("GET", "/repos/octocat/hello/pulls/42/files?per_page=100&page=1") => "[]",
            ("GET", "/repos/octocat/hello/pulls/42/reviews?per_page=100&page=1") => "[]",
            ("POST", "/repos/octocat/hello/pulls/42/reviews") or ("POST", "/repos/octocat/other/pulls/7/reviews") => """{"id":1,"html_url":"https://github.com/r/1"}""",
            ("POST", _) when path.EndsWith("/comments", StringComparison.Ordinal) => """{"id":2,"html_url":"https://github.com/c/2"}""",
            ("POST", _) when path.EndsWith("/issues", StringComparison.Ordinal) => """{"number":3,"html_url":"https://github.com/i/3"}""",
            _ => null,
        };
        return json is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
    }

    private ServiceProvider Compose(bool withBot, string botWrites = "verdict,comment", string botRepos = "octocat/hello", string botLogin = "scout-octocat[bot]")
    {
        var settings = new Dictionary<string, string?> { ["Scout:Plugins:GitHub:TokenFile"] = File("owner.json", OwnerToken) };
        if (withBot)
        {
            settings["Scout:Plugins:GitHub:BotTokenFile"] = File("bot.json", BotToken);
            settings["Scout:Plugins:GitHub:BotWrites"] = botWrites;
            settings["Scout:Plugins:GitHub:BotRepos"] = botRepos;
            settings["Scout:Plugins:GitHub:BotLogin"] = botLogin;
        }

        var services = new ServiceCollection();
        services.AddGitHubPlugin(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        foreach (var name in new[] { nameof(IGitHubClient), nameof(IGitHubWriteClient), GitHubServiceCollectionExtensions.BotWritesClient })
        {
            services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new Recording(_requests, r => _respond(r)));
        }

        return services.BuildServiceProvider();
    }

    private static string? AuthorizationOf(IEnumerable<(string Method, string Path, string? Authorization)> requests, string method, string path) =>
        requests.Last(r => r.Method == method && r.Path == path).Authorization;

    private static async Task WriteEveryKindAsync(GitHubActionsPlugin plugin, string repo = "hello", int number = 42)
    {
        await plugin.SubmitPullRequestReviewAsync("octocat", repo, number, "Looks fine.", "[]", CancellationToken.None);
        await plugin.CommentOnPullRequestAsync("octocat", repo, number, "Thanks.", CancellationToken.None);
        await plugin.CreateIssueAsync("octocat", repo, "A title", "A body.", "", CancellationToken.None);
    }

    [Fact]
    public async Task With_No_Bot_File_Every_Call_Uses_The_Owner_s_Token()
    {
        using var provider = Compose(withBot: false);
        var plugin = provider.GetRequiredService<GitHubActionsPlugin>();

        await WriteEveryKindAsync(plugin);
        await plugin.GivePullRequestVerdictAsync("octocat", "hello", 42, "request_changes", "Fix it.", "[]", Sha, CancellationToken.None);

        _requests.ShouldNotBeEmpty();
        _requests.Select(r => r.Authorization).Distinct().ShouldBe(["Bearer " + OwnerToken]);
        provider.GetRequiredService<IGitHubWriteClient>().SignerFor(GitHubWriteKind.Verdict, "octocat", "hello").ShouldBe(new GitHubSigner(false, null, false));
    }

    [Fact]
    public async Task A_Write_Switched_To_The_Bot_Uses_The_Bot_s_Token_And_Every_Read_And_Other_Write_The_Owner_s()
    {
        using var provider = Compose(withBot: true);
        var plugin = provider.GetRequiredService<GitHubActionsPlugin>();

        await WriteEveryKindAsync(plugin);
        await plugin.GivePullRequestVerdictAsync("octocat", "hello", 42, "approve", "Ready.", "[]", Sha, CancellationToken.None);

        AuthorizationOf(_requests, "POST", "/repos/octocat/hello/issues/42/comments").ShouldBe("Bearer " + BotToken);
        AuthorizationOf(_requests, "POST", "/repos/octocat/hello/issues").ShouldBe("Bearer " + OwnerToken);
        _requests.Where(r => r.Method == "POST" && r.Path == "/repos/octocat/hello/pulls/42/reviews").Select(r => r.Authorization)
            .ShouldBe(["Bearer " + OwnerToken, "Bearer " + BotToken], "the commenting review stays the owner's, the verdict is the bot's");
        _requests.Where(r => r.Method == "GET").Select(r => r.Authorization).Distinct().ShouldBe(["Bearer " + OwnerToken]);
    }

    [Fact]
    public async Task The_Bot_May_Approve_The_Owner_s_Own_Pull_Request_But_Not_One_The_Bot_Opened()
    {
        using var provider = Compose(withBot: true);
        var plugin = provider.GetRequiredService<GitHubActionsPlugin>();

        var ownersPullRequest = await plugin.GivePullRequestVerdictAsync("octocat", "hello", 42, "approve", "Ready.", "[]", Sha, CancellationToken.None);
        _respond = r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/repos/octocat/hello/pulls/42"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$$"""{"number":42,"user":{"login":"scout-octocat[bot]"},"head":{"sha":"{{{Sha}}}"}}""") }
            : Answer(r);
        var botsPullRequest = await Should.ThrowAsync<WriteFailedException>(() =>
            plugin.GivePullRequestVerdictAsync("octocat", "hello", 42, "approve", "Ready.", "[]", Sha, CancellationToken.None));

        ownersPullRequest.ShouldStartWith("Approved octocat/hello#42 with 0 comment(s)");
        botsPullRequest.Message.ShouldBe(GitHubActionsPlugin.SelfApproval);
        _requests.ShouldNotContain(r => r.Path == "/user", "the bot's login comes from the configuration, never from GitHub");
    }

    [Fact]
    public async Task A_Write_On_A_Repository_The_Bot_Is_Not_Installed_On_Is_Signed_As_The_Owner_And_Says_So()
    {
        using var provider = Compose(withBot: true, botRepos: "octocat/other");
        var plugin = provider.GetRequiredService<GitHubActionsPlugin>();

        var answer = await plugin.CommentOnPullRequestAsync("octocat", "hello", 42, "Thanks.", CancellationToken.None);

        answer.ShouldBe("Commented on pull request octocat/hello#42: https://github.com/c/2 Signed as you: the Scout bot is not installed on octocat/hello.");
        AuthorizationOf(_requests, "POST", "/repos/octocat/hello/issues/42/comments").ShouldBe("Bearer " + OwnerToken);
    }

    [Fact]
    public async Task The_Tests_Line_Is_Signed_By_The_Identity_That_Gave_The_Verdict()
    {
        using var provider = Compose(withBot: true, botWrites: "verdict");
        var runner = provider.GetRequiredService<GitHubVerdictRunner>();

        await runner.PostTestsLineAsync(new GivenVerdict("octocat", "hello", 42, "https://github.com/r/1", "Tests: 3 checks passed."), CancellationToken.None);
        await provider.GetRequiredService<GitHubActionsPlugin>().CommentOnPullRequestAsync("octocat", "hello", 42, "Thanks.", CancellationToken.None);

        _requests.Where(r => r.Path == "/repos/octocat/hello/issues/42/comments").Select(r => r.Authorization)
            .ShouldBe(["Bearer " + BotToken, "Bearer " + OwnerToken]);
    }

    [Fact]
    public async Task The_Bot_s_Token_Never_Appears_In_What_A_Failed_Write_Says()
    {
        using var provider = Compose(withBot: true);
        var plugin = provider.GetRequiredService<GitHubActionsPlugin>();
        _respond = r => r.Method == HttpMethod.Post ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Answer(r);

        var refused = await Should.ThrowAsync<WriteFailedException>(() => plugin.CommentOnPullRequestAsync("octocat", "hello", 42, "Thanks.", CancellationToken.None));
        System.IO.File.Delete(Path.Combine(_dir, "bot.json"));
        var noFile = await Should.ThrowAsync<WriteFailedException>(() => plugin.CommentOnPullRequestAsync("octocat", "hello", 42, "Thanks.", CancellationToken.None));

        refused.Message.ShouldBe("GitHub denied the request (insufficient token permissions).");
        noFile.Message.ShouldBe("GitHub is not connected. Connect it on the Features screen.");
        foreach (var text in new[] { refused.Message, refused.Outcome, noFile.Message, noFile.Outcome })
        {
            text.ShouldNotContain(BotToken);
            text.ShouldNotContain(OwnerToken);
        }
    }

    [Fact]
    public async Task A_Verdict_The_Bot_Would_Sign_Without_A_Known_Login_Is_Refused_Before_Any_Call()
    {
        using var provider = Compose(withBot: true, botLogin: "");
        var plugin = provider.GetRequiredService<GitHubActionsPlugin>();

        var ex = await Should.ThrowAsync<WriteFailedException>(() =>
            plugin.GivePullRequestVerdictAsync("octocat", "hello", 42, "approve", "Ready.", "[]", Sha, CancellationToken.None));

        (ex.Message, ex.Outcome).ShouldBe(("The Scout bot's login is unknown; give the verdict as yourself or recreate the bot.", "refused"));
        _requests.ShouldNotContain(r => r.Method == "POST");
    }

    private sealed class Recording(
        List<(string Method, string Path, string? Authorization)> requests,
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString()));
            return Task.FromResult(respond(request));
        }
    }
}
