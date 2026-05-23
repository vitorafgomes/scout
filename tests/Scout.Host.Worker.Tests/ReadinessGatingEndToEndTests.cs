using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Scout.Host.Worker.Providers;
using Shouldly;

namespace Scout.Host.Worker.Tests;

public sealed class ReadinessGatingEndToEndTests
{
    [Fact]
    public async Task BootWithUnreachableOllama_DoesNotEmitConnectionRefusedAtError_AndReportsUnreachable()
    {
        var capturedEntries = new ConcurrentQueue<LogEntry>();
        var loggerProvider = new RecordingLoggerProvider(capturedEntries);

        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<OllamaTags?>(null));

        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("/usr/local/bin/ollama"));

        var store = Substitute.For<ILocalRuntimeConfigStore>();
        store.GetAutostartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var launcher = Substitute.For<IProcessLauncher>();
        var fakeProc = Substitute.For<ILaunchedProcess>();
        fakeProc.HasExited.Returns(false);
        fakeProc.StopAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        launcher.Launch(Arg.Any<ProcessLaunchRequest>()).Returns(fakeProc);

        using var factory = new ScoutWorkerWebApplicationFactory
        {
            ConfigureServicesOverride = services =>
            {
                services.AddSingleton(locator);
                services.AddSingleton(api);
                services.AddSingleton<IProcessLauncher>(launcher);
                services.AddSingleton<ILocalRuntimeRegistry>(new InMemoryLocalRuntimeRegistry());
                services.AddSingleton(store);

                // Re-register the real probe (the factory strips it because we
                // supplied an override) and re-add the autostart lifecycle
                // service that the factory strips by namespace prefix. The
                // e2e scenario depends on both being wired.
                services.AddSingleton<OllamaReadinessProbe>();
                services.AddSingleton<IOllamaReadinessProbe>(sp =>
                    sp.GetRequiredService<OllamaReadinessProbe>());
                services.AddHostedService<LocalRuntimeAutostartService>();

                services.AddLogging(b =>
                {
                    b.SetMinimumLevel(LogLevel.Trace);
                    b.AddProvider(loggerProvider);
                });
            },
        };
        factory
            .WithConfiguration("Scout:Providers:Ollama:WarmupBudget", "00:00:00.300")
            .WithConfiguration("Scout:Providers:Ollama:StartTimeout", "00:00:00.300")
            .WithConfiguration("Scout:Providers:Ollama:PingTimeout", "00:00:00.100");

        using var client = factory.CreateClient();

        var listResponse = await client.GetAsync("/providers");
        listResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var providers = await listResponse.Content.ReadFromJsonAsync<List<JsonElement>>();
        providers.ShouldNotBeNull();
        var ollama = providers!.First(p => p.GetProperty("id").GetString() == "ollama");
        ollama.GetProperty("running").GetBoolean().ShouldBeFalse();
        ollama.GetProperty("lastError").GetProperty("code").GetString().ShouldBe("providers/unreachable");

        var startResponse = await client.PostAsync("/providers/ollama/start", content: null);
        startResponse.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var startBody = await startResponse.Content.ReadAsStringAsync();
        startBody.ShouldContain("providers/not-ready");

        var noisyErrors = capturedEntries
            .Where(e => e.Level == LogLevel.Error)
            .Where(e => e.Message.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("ECONNREFUSED", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("System.Net.Sockets.SocketException", StringComparison.OrdinalIgnoreCase))
            .ToList();
        noisyErrors.ShouldBeEmpty();
    }

    private sealed record LogEntry(LogLevel Level, string Category, string Message, Exception? Exception);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> _entries;

        public RecordingLoggerProvider(ConcurrentQueue<LogEntry> entries) => _entries = entries;

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, _entries);

        public void Dispose() { }

        private sealed class RecordingLogger : ILogger
        {
            private readonly string _category;
            private readonly ConcurrentQueue<LogEntry> _entries;

            public RecordingLogger(string category, ConcurrentQueue<LogEntry> entries)
            {
                _category = category;
                _entries = entries;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                if (exception is not null)
                {
                    message = $"{message} :: {exception}";
                }
                _entries.Enqueue(new LogEntry(logLevel, _category, message, exception));
            }
        }
    }
}
