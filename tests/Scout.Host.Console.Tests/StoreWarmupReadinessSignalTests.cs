using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Scout.Conversation.Abstractions;
using Scout.Meetings;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

public sealed class StoreWarmupReadinessSignalTests
{
    private sealed class ListLogger : ILogger<StoreWarmupReadinessSignal>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task Ready_is_signalled_first_and_then_the_meetings_store_is_warmed_with_one_list_through_its_interface()
    {
        var inner = Substitute.For<IReadinessSignal>();
        var meetings = Substitute.For<IMeetingStore>();
        var signal = new StoreWarmupReadinessSignal(inner, meetings, NullLogger<StoreWarmupReadinessSignal>.Instance);

        signal.Signal();
        await signal.Warmed;

        Received.InOrder(() =>
        {
            inner.Signal();
            meetings.ListAsync(1, CancellationToken.None);
        });
        meetings.ReceivedCalls().Count().ShouldBe(1, "the warm-up is one cheap read and nothing else");
    }

    [Fact]
    public async Task A_warm_up_the_store_fails_is_logged_at_debug_with_the_type_only_and_ready_is_still_signalled()
    {
        var inner = Substitute.For<IReadinessSignal>();
        var meetings = Substitute.For<IMeetingStore>();
        meetings.ListAsync(1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new MeetingStoreException("Scout could not list the meetings database (SQLite error 26).", new IOException("Salary review")));
        var logger = new ListLogger();
        var signal = new StoreWarmupReadinessSignal(inner, meetings, logger);

        signal.Signal();
        await signal.Warmed;

        inner.Received(1).Signal();
        var (level, message) = logger.Entries.ShouldHaveSingleItem();
        level.ShouldBe(LogLevel.Debug);
        message.ShouldContain(nameof(MeetingStoreException));
        message.ShouldNotContain("Salary review", Case.Sensitive);
    }

    [Fact]
    public async Task Without_a_meetings_store_only_ready_is_signalled()
    {
        var inner = Substitute.For<IReadinessSignal>();
        var signal = new StoreWarmupReadinessSignal(inner, null, NullLogger<StoreWarmupReadinessSignal>.Instance);

        signal.Signal();
        await signal.Warmed;

        inner.Received(1).Signal();
        signal.Warmed.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task After_the_host_reports_ready_the_meetings_database_is_migrated_before_any_meetings_request()
    {
        var folder = Directory.CreateTempSubdirectory("scout-host-warmup-").FullName;
        var dbPath = Path.Combine(folder, "meetings.db");
        try
        {
            using var host = new HostBuilder()
                .ConfigureServices((_, services) => services.AddLogging().AddScoutHost(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Scout:Llm:Provider"] = "None",
                        ["Scout:Adapters:Local:Enabled"] = "true",
                        ["Scout:Meetings:DbPath"] = dbPath,
                    })
                    .Build()))
                .Build();
            var signal = host.Services.GetRequiredService<IReadinessSignal>().ShouldBeOfType<StoreWarmupReadinessSignal>();
            File.Exists(dbPath).ShouldBeFalse("composing the host opens no database");

            signal.Signal();
            await signal.Warmed;

            using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                connection.Open();
                using var history = connection.CreateCommand();
                history.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory;";
                Convert.ToInt64(history.ExecuteScalar(), CultureInfo.InvariantCulture)
                    .ShouldBe(1, "the warm-up ran the store's schema step, so the first meetings request does not pay for it");
            }

            (await host.Services.GetRequiredService<IMeetingStore>().ListAsync(10, CancellationToken.None)).ShouldBeEmpty();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }
}
