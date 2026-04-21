using Scout.Plugin.CloudStatus.Domain;
using Scout.Plugin.CloudStatus.Providers;

namespace Scout.Plugin.CloudStatus.Tests;

public class CloudStatusFunctionsTests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 4, 21, 10, 0, 0, TimeSpan.Zero);

    private static StatusReport Report(string provider, HealthLevel level) =>
        new(provider, $"scope-{provider}", level, Array.Empty<ServiceStatus>(), FixedTime);

    [Fact]
    public async Task GetAll_Returns_Empty_When_No_Providers_Registered()
    {
        var sut = new CloudStatusFunctions(Array.Empty<ICloudHealthProvider>());

        var reports = await sut.GetAllAsync(CancellationToken.None);

        reports.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAll_Aggregates_Reports_From_Every_Provider()
    {
        var azure = new InMemoryCloudHealthProvider(Report("Azure", HealthLevel.Healthy));
        var aws = new InMemoryCloudHealthProvider(Report("AWS", HealthLevel.Degraded));
        var oci = new InMemoryCloudHealthProvider(Report("OracleCloud", HealthLevel.Unhealthy));

        var sut = new CloudStatusFunctions([azure, aws, oci]);

        var reports = await sut.GetAllAsync(CancellationToken.None);

        reports.Select(r => r.Provider)
               .ShouldBe(["Azure", "AWS", "OracleCloud"]);
    }

    [Fact]
    public async Task GetAll_Converts_Provider_Exception_To_Unknown_Report()
    {
        var throwing = Substitute.For<ICloudHealthProvider>();
        throwing.Name.Returns("Azure");
        throwing
            .GetHealthAsync(Arg.Any<CancellationToken>())
            .Returns<Task<StatusReport>>(_ => throw new InvalidOperationException("simulated"));

        var healthy = new InMemoryCloudHealthProvider(Report("AWS", HealthLevel.Healthy));

        var sut = new CloudStatusFunctions([throwing, healthy]);

        var reports = await sut.GetAllAsync(CancellationToken.None);

        reports.Count().ShouldBe(2);
        var azure = reports.Single(r => r.Provider == "Azure");
        azure.Overall.ShouldBe(HealthLevel.Unknown);
        azure.Message!.ShouldBe("simulated");

        var aws = reports.Single(r => r.Provider == "AWS");
        aws.Overall.ShouldBe(HealthLevel.Healthy);
    }

    [Fact]
    public async Task GetAll_Propagates_Cancellation()
    {
        var cancellable = Substitute.For<ICloudHealthProvider>();
        cancellable.Name.Returns("Azure");
        cancellable
            .GetHealthAsync(Arg.Any<CancellationToken>())
            .Returns<Task<StatusReport>>(ci =>
            {
                var ct = ci.Arg<CancellationToken>();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(Report("Azure", HealthLevel.Healthy));
            });

        var sut = new CloudStatusFunctions([cancellable]);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => sut.GetAllAsync(cts.Token);

        await act.ShouldThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Get_Returns_Requested_Provider_Report()
    {
        var azure = new InMemoryCloudHealthProvider(Report("Azure", HealthLevel.Healthy));
        var aws = new InMemoryCloudHealthProvider(Report("AWS", HealthLevel.Unhealthy));

        var sut = new CloudStatusFunctions([azure, aws]);

        var report = await sut.GetAsync("AWS", CancellationToken.None);

        report.Provider.ShouldBe("AWS");
        report.Overall.ShouldBe(HealthLevel.Unhealthy);
    }

    [Fact]
    public async Task Get_Is_Case_Insensitive()
    {
        var azure = new InMemoryCloudHealthProvider(Report("Azure", HealthLevel.Healthy));

        var sut = new CloudStatusFunctions([azure]);

        var report = await sut.GetAsync("aZuRe", CancellationToken.None);

        report.Provider.ShouldBe("Azure");
    }

    [Fact]
    public async Task Get_Returns_Unknown_When_Provider_Not_Registered()
    {
        var sut = new CloudStatusFunctions(Array.Empty<ICloudHealthProvider>());

        var report = await sut.GetAsync("Azure", CancellationToken.None);

        report.Provider.ShouldBe("Azure");
        report.Overall.ShouldBe(HealthLevel.Unknown);
        report.Message!.ShouldContain("No 'Azure' provider is registered");
    }

    [Fact]
    public async Task Get_Throws_When_Provider_Name_Empty()
    {
        var sut = new CloudStatusFunctions(Array.Empty<ICloudHealthProvider>());

        Func<Task> act = () => sut.GetAsync("", CancellationToken.None);

        await act.ShouldThrowAsync<ArgumentException>();
    }
}
