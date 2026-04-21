using Azure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.Plugin.CloudStatus.Domain;
using Scout.Plugin.CloudStatus.Providers;
using Scout.Plugin.CloudStatus.Providers.Azure;

namespace Scout.Plugin.CloudStatus.Tests.Providers;

public class AzureCloudHealthProviderTests
{
    private const string SubscriptionId = "00000000-0000-0000-0000-000000000001";
    private static readonly DateTimeOffset FixedTime =
        new(2026, 4, 21, 10, 0, 0, TimeSpan.Zero);

    private static AzureCloudHealthProvider Sut(IAzureHealthSource source) =>
        new(
            Options.Create(new AzureCloudHealthOptions { SubscriptionId = SubscriptionId }),
            source,
            NullLogger<AzureCloudHealthProvider>.Instance,
            new FixedTimeProvider(FixedTime));

    [Fact]
    public async Task Returns_Healthy_When_No_Active_Events()
    {
        var source = new StubHealthSource(Array.Empty<AzureHealthEvent>());

        var report = await Sut(source).GetHealthAsync(CancellationToken.None);

        report.Provider.Should().Be("Azure");
        report.Scope.Should().Be(SubscriptionId);
        report.Overall.Should().Be(HealthLevel.Healthy);
        report.Services.Should().BeEmpty();
        report.CheckedAt.Should().Be(FixedTime);
    }

    [Fact]
    public async Task Maps_Critical_Event_To_Unhealthy()
    {
        var source = new StubHealthSource([
            new AzureHealthEvent("ev-1", "Regional outage", "Critical", "Azure SQL", "westeurope"),
            new AzureHealthEvent("ev-2", "Latency", "Warning", "App Service", "northeurope"),
        ]);

        var report = await Sut(source).GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(HealthLevel.Unhealthy);
        report.Services.Should().HaveCount(2);
        report.Services.Should().Contain(s =>
            s.Service == "Azure SQL" && s.Region == "westeurope" && s.Level == HealthLevel.Unhealthy);
        report.Services.Should().Contain(s =>
            s.Service == "App Service" && s.Region == "northeurope" && s.Level == HealthLevel.Degraded);
    }

    [Fact]
    public async Task Maps_Warning_Only_To_Degraded()
    {
        var source = new StubHealthSource([
            new AzureHealthEvent("ev-1", "Intermittent errors", "Warning", "Cosmos DB", "eastus"),
        ]);

        var report = await Sut(source).GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(HealthLevel.Degraded);
    }

    [Fact]
    public async Task Treats_Unknown_Level_String_As_Degraded()
    {
        // Guards the mapper from leaking Azure-specific severity strings into
        // Healthy by mistake. Anything unrecognized is treated as Degraded.
        var source = new StubHealthSource([
            new AzureHealthEvent("ev-1", "Something", "SomeFutureSeverity", "Monitor", "global"),
        ]);

        var report = await Sut(source).GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(HealthLevel.Degraded);
    }

    [Fact]
    public async Task Returns_Unknown_When_Azure_Returns_403()
    {
        var source = new ThrowingHealthSource(
            new RequestFailedException(status: 403, message: "Forbidden"));

        var report = await Sut(source).GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(HealthLevel.Unknown);
        report.Message.Should().Contain("Access denied");
    }

    [Fact]
    public async Task Returns_Unknown_When_Azure_Returns_401()
    {
        var source = new ThrowingHealthSource(
            new RequestFailedException(status: 401, message: "Unauthorized"));

        var report = await Sut(source).GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(HealthLevel.Unknown);
        report.Message.Should().Contain("Access denied");
    }

    [Fact]
    public async Task Returns_Unknown_When_Azure_Returns_503()
    {
        var source = new ThrowingHealthSource(
            new RequestFailedException(status: 503, message: "Service Unavailable", errorCode: "ServiceUnavailable", innerException: null));

        var report = await Sut(source).GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(HealthLevel.Unknown);
        report.Message.Should().Contain("HTTP 503");
    }

    [Fact]
    public async Task Returns_Unknown_On_Unexpected_Exception()
    {
        var source = new ThrowingHealthSource(new InvalidOperationException("boom"));

        var report = await Sut(source).GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(HealthLevel.Unknown);
        report.Message.Should().Be("boom");
    }

    [Fact]
    public async Task Propagates_Cancellation()
    {
        var source = new CancellableHealthSource();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => Sut(source).GetHealthAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Ctor_Throws_When_SubscriptionId_Empty()
    {
        Action create = () => new AzureCloudHealthProvider(
            Options.Create(new AzureCloudHealthOptions { SubscriptionId = "" }),
            new StubHealthSource(Array.Empty<AzureHealthEvent>()),
            NullLogger<AzureCloudHealthProvider>.Instance);

        create.Should().Throw<ArgumentException>()
            .WithMessage("*SubscriptionId*");
    }

    [Fact]
    public void Ctor_Throws_When_Source_Null()
    {
        Action create = () => new AzureCloudHealthProvider(
            Options.Create(new AzureCloudHealthOptions { SubscriptionId = SubscriptionId }),
            source: null!,
            NullLogger<AzureCloudHealthProvider>.Instance);

        create.Should().Throw<ArgumentNullException>();
    }

    private sealed class StubHealthSource(IReadOnlyList<AzureHealthEvent> events) : IAzureHealthSource
    {
        public Task<IReadOnlyList<AzureHealthEvent>> GetActiveEventsAsync(CancellationToken ct) =>
            Task.FromResult(events);
    }

    private sealed class ThrowingHealthSource(Exception exception) : IAzureHealthSource
    {
        public Task<IReadOnlyList<AzureHealthEvent>> GetActiveEventsAsync(CancellationToken ct) =>
            throw exception;
    }

    private sealed class CancellableHealthSource : IAzureHealthSource
    {
        public Task<IReadOnlyList<AzureHealthEvent>> GetActiveEventsAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<AzureHealthEvent>>(Array.Empty<AzureHealthEvent>());
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
