using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Scout.Plugin.CloudStatus.Domain;
using Scout.Plugin.CloudStatus.Providers;
using Scout.Plugin.CloudStatus.Providers.Oci;

namespace Scout.Plugin.CloudStatus.Tests.Providers;

public class OciCloudHealthProviderTests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 4, 21, 10, 0, 0, TimeSpan.Zero);

    private static OciCloudHealthProvider Sut(IOciHealthSource source) =>
        new(source, NullLogger<OciCloudHealthProvider>.Instance,
            new FixedTimeProvider(FixedTime));

    [Fact]
    public async Task Returns_Healthy_When_No_Active_Incidents()
    {
        var report = await Sut(new StubSource(Array.Empty<OciHealthIncident>()))
            .GetHealthAsync(CancellationToken.None);

        report.Provider.Should().Be("OracleCloud");
        report.Overall.Should().Be(HealthLevel.Healthy);
        report.Services.Should().BeEmpty();
        report.CheckedAt.Should().Be(FixedTime);
    }

    [Theory]
    [InlineData("critical", HealthLevel.Unhealthy)]
    [InlineData("major", HealthLevel.Unhealthy)]
    [InlineData("minor", HealthLevel.Degraded)]
    [InlineData("SomeFutureImpact", HealthLevel.Degraded)]
    public async Task Maps_Impact_To_Expected_HealthLevel(string impact, HealthLevel expected)
    {
        var source = new StubSource([
            new OciHealthIncident("inc-1", "Compute", "eu-london-1", impact, "Capacity issue"),
        ]);

        var report = await Sut(source).GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(expected);
    }

    [Fact]
    public async Task Aggregates_Overall_As_Max_Of_Individual_Levels()
    {
        var source = new StubSource([
            new OciHealthIncident("inc-1", "Compute", "us-phoenix-1", "minor", "Latency"),
            new OciHealthIncident("inc-2", "Object Storage", "eu-london-1", "critical", "Outage"),
        ]);

        var report = await Sut(source).GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(HealthLevel.Unhealthy);
        report.Services.Should().HaveCount(2);
    }

    [Fact]
    public async Task HttpRequestException_With_Status_Maps_To_Unknown_With_Code()
    {
        var source = new ThrowingSource(
            new HttpRequestException("Server Error", inner: null, statusCode: HttpStatusCode.InternalServerError));

        var report = await Sut(source).GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(HealthLevel.Unknown);
        report.Message.Should().Contain("HTTP 500");
    }

    [Fact]
    public async Task HttpRequestException_Without_Status_Maps_To_Unknown_Unreachable()
    {
        var source = new ThrowingSource(new HttpRequestException("connection refused"));

        var report = await Sut(source).GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(HealthLevel.Unknown);
        report.Message.Should().Contain("unreachable");
    }

    [Fact]
    public async Task TaskCanceledException_Without_Caller_Cancellation_Maps_To_Timeout_Unknown()
    {
        // HttpClient raises TaskCanceledException when its own Timeout fires.
        // Caller token is not cancelled in this scenario.
        var source = new ThrowingSource(new TaskCanceledException("timed out"));

        var report = await Sut(source).GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(HealthLevel.Unknown);
        report.Message.Should().Contain("timed out");
    }

    [Fact]
    public async Task Unexpected_Exception_Maps_To_Unknown()
    {
        var report = await Sut(new ThrowingSource(new InvalidOperationException("boom")))
            .GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(HealthLevel.Unknown);
        report.Message.Should().Be("boom");
    }

    [Fact]
    public async Task Propagates_Caller_Cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => Sut(new CancellableSource()).GetHealthAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Ctor_Throws_When_Source_Null()
    {
        Action create = () => new OciCloudHealthProvider(
            source: null!,
            NullLogger<OciCloudHealthProvider>.Instance);

        create.Should().Throw<ArgumentNullException>();
    }

    private sealed class StubSource(IReadOnlyList<OciHealthIncident> incidents) : IOciHealthSource
    {
        public Task<IReadOnlyList<OciHealthIncident>> GetActiveIncidentsAsync(CancellationToken ct) =>
            Task.FromResult(incidents);
    }

    private sealed class ThrowingSource(Exception exception) : IOciHealthSource
    {
        public Task<IReadOnlyList<OciHealthIncident>> GetActiveIncidentsAsync(CancellationToken ct) =>
            throw exception;
    }

    private sealed class CancellableSource : IOciHealthSource
    {
        public Task<IReadOnlyList<OciHealthIncident>> GetActiveIncidentsAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<OciHealthIncident>>(Array.Empty<OciHealthIncident>());
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
