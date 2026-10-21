using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;
using Scout.Core.Features;
using Scout.Plugin.GitHub.Functions;
using Scout.Plugin.GitHub.Internal;
using Scout.Plugin.GitHub.Tests.Internal;

namespace Scout.Plugin.GitHub.Tests;

public class CreateIssueLoggingTests
{
    [Fact]
    public async Task CreateIssue_Log_Contains_Sha256_First8_And_Never_Contains_Title_Or_Body()
    {
        const string secretTitle = "found vulnerability XYZ-42 in module Foo";
        const string secretBody = "POC: curl -X POST ...";

        var client = Substitute.For<IGitHubClient>();
        client.Issue.Create("o", "r", Arg.Any<NewIssue>())
            .Returns(Task.FromResult(OctokitFakes.Issue(number: 5)));

        var sink = new CapturingLogger<GitHubFunctions>();
        var factory = Substitute.For<IGitHubClientFactory>();
        factory.CreateAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(client));

        var settings = Substitute.For<IFeatureSettingsReader>();
        settings.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var sut = new GitHubFunctions(factory, sink, Options.Create(new GitHubPluginOptions()), settings);

        var result = await sut.CreateIssueAsync("o", "r", secretTitle, secretBody);

        result.Success.ShouldBeTrue();
        sink.Lines.ShouldNotBeEmpty();

        var auditLine = sink.Lines.ShouldHaveSingleItem();
        auditLine.ShouldContain(GitHubFunctions.Sha256First8(secretTitle));
        auditLine.ShouldNotContain(secretTitle);
        auditLine.ShouldNotContain(secretBody);
    }

    /// <summary>
    /// Captures rendered log message strings so we can assert their
    /// contents (and absences) without coupling to MSEL internals.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
