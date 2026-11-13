using System.Net;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scout.Llm.Abstractions;
using Scout.Plugin.GitHub;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

public sealed class GitHubActionExecutorSecretsTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string OwnerToken = "ghu_owner_secret_not_real";
    private const string BotToken = "ghs_bot_secret_not_real";

    private readonly string _dir = Directory.CreateTempSubdirectory("scout-executor-secrets-").FullName;
    private readonly GitHubActionExecutorTests.RoutingHandler _github = new();
    private readonly ServiceProvider _provider;

    public GitHubActionExecutorSecretsTests()
    {
        var owner = Path.Combine(_dir, "owner.json");
        var bot = Path.Combine(_dir, "bot.json");
        File.WriteAllText(owner, $$"""{"access_token":"{{OwnerToken}}"}""");
        File.WriteAllText(bot, $$"""{"access_token":"{{BotToken}}"}""");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Scout:Plugins:GitHub:TokenFile"] = owner,
            ["Scout:Plugins:GitHub:BotTokenFile"] = bot,
            ["Scout:Plugins:GitHub:BotWrites"] = "verdict",
            ["Scout:Plugins:GitHub:BotRepos"] = "octocat/hello",
            ["Scout:Plugins:GitHub:BotLogin"] = "scout-octocat[bot]",
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddGitHubPlugin(configuration);
        foreach (var name in new[] { "IGitHubClient", "IGitHubWriteClient", "GitHubBotWrites" })
        {
            services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => _github);
        }

        _provider = services.BuildServiceProvider();
        _github.On("GET", "repos/octocat/hello/pulls/42", HttpStatusCode.OK, $$$"""{"number":42,"user":{"login":"alice"},"head":{"sha":"{{{Sha}}}"}}""");
        _github.On("GET", "repos/octocat/hello/pulls/42/files?per_page=100&page=1", HttpStatusCode.OK, "[]");
        _github.On("GET", "repos/octocat/hello/pulls/42/reviews?per_page=100&page=1", HttpStatusCode.OK, "[]");
        _github.On("POST", "repos/octocat/hello/pulls/42/reviews", HttpStatusCode.OK, """{"id":88,"html_url":"https://github.com/octocat/hello/pull/42#pullrequestreview-88"}""");
        _github.On("POST", "repos/octocat/hello/issues/42/comments", HttpStatusCode.Forbidden, "");
    }

    public void Dispose()
    {
        _provider.Dispose();
        _github.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Neither_Token_Appears_In_What_The_Executor_Logs_Or_Records_When_GitHub_And_The_Control_Plane_Fail()
    {
        var gate = new Gate();
        var logger = new Recorder();
        var executor = new GitHubActionExecutor(gate, gate, _provider.GetRequiredService<GitHubVerdictRunner>(), _provider.GetRequiredService<GitHubWriteRunner>(), TimeProvider.System, logger);
        const string payload = $$"""{"owner":"octocat","repo":"hello","number":42,"verdict":"approve","body":"Ready.","comments":[],"commit":"{{Sha}}","testsLine":"Tests: 3 passed."}""";
        gate.Claims.Enqueue(new ApprovedAction("act-1", "github", "Pull request verdicts", "octocat/hello#42", "s") { Payload = payload });
        gate.Claims.Enqueue(new ApprovedAction("act-2", "github", "Pull request verdicts", "octocat/hello#42", "s") { Payload = payload.Replace(Sha, "fedcba9", StringComparison.Ordinal) });
        gate.Claims.Enqueue(new ApprovedAction("act-3", "github", "Pull request verdicts", "octocat/hello#42", "s") { Payload = payload });

        await executor.RunOnceAsync(CancellationToken.None);
        await executor.RunOnceAsync(CancellationToken.None);
        _github.On("POST", "repos/octocat/hello/pulls/42/reviews", HttpStatusCode.Unauthorized, "");
        gate.RecordFailure = new RpcException(new Status(StatusCode.Unavailable, "down"));
        await executor.RunOnceAsync(CancellationToken.None);

        logger.Messages.Count.ShouldBeGreaterThanOrEqualTo(2, "the tests line refused and the record lost are both logged");
        gate.Records.Select(r => r.Outcome).ShouldBe(["succeeded", "failed: refused", "failed: github 401"]);
        foreach (var text in logger.Messages.Concat(gate.Records.SelectMany(r => new[] { r.Outcome, r.Result ?? string.Empty })))
        {
            text.ShouldNotContain(OwnerToken);
            text.ShouldNotContain(BotToken);
        }
    }

    private sealed class Gate : IWriteGate, IApprovedActions
    {
        public Queue<ApprovedAction?> Claims { get; } = new();

        public Exception? RecordFailure { get; set; }

        public List<(string Outcome, string? Result)> Records { get; } = [];

        public Task<ApprovedAction?> ClaimNextAsync(string featureId, CancellationToken cancellationToken) =>
            Task.FromResult(Claims.Count == 0 ? null : Claims.Dequeue());

        public Task<bool> CheckGrantAsync(ApprovedAction action, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<WriteGateDecision> RequestAsync(string featureId, string scope, string target, string summary, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The executor never asks for a write.");

        public Task RecordAsync(string featureId, string scope, string target, string outcome, string? actionId, CancellationToken cancellationToken) =>
            RecordAsync(featureId, scope, target, outcome, actionId, null, cancellationToken);

        public Task RecordAsync(string featureId, string scope, string target, string outcome, string? actionId, string? result, CancellationToken cancellationToken)
        {
            Records.Add((outcome, result));
            return RecordFailure is null ? Task.CompletedTask : throw RecordFailure;
        }
    }

    private sealed class Recorder : ILogger<GitHubActionExecutor>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}
