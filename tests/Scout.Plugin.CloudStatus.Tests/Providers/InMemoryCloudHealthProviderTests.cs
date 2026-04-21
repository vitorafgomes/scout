using Scout.Plugin.CloudStatus.Domain;
using Scout.Plugin.CloudStatus.Providers;

namespace Scout.Plugin.CloudStatus.Tests.Providers;

public class InMemoryCloudHealthProviderTests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 4, 21, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Name_Matches_Underlying_Report_Provider()
    {
        var report = new StatusReport(
            "Azure", "sub-1", HealthLevel.Healthy,
            Array.Empty<ServiceStatus>(), FixedTime);

        var sut = new InMemoryCloudHealthProvider(report);

        sut.Name.ShouldBe("Azure");
    }

    [Fact]
    public async Task GetHealthAsync_Returns_Configured_Report()
    {
        var expected = new StatusReport(
            "AWS", "account-1", HealthLevel.Degraded,
            [new ServiceStatus("RDS", "eu-west-1", HealthLevel.Degraded, "slow")],
            FixedTime);

        var actual = await new InMemoryCloudHealthProvider(expected)
            .GetHealthAsync(CancellationToken.None);

        actual.ShouldBeSameAs(expected);
    }

    [Fact]
    public void Ctor_Throws_When_Report_Null()
    {
        Action create = () => new InMemoryCloudHealthProvider(null!);

        create.ShouldThrow<ArgumentNullException>();
    }
}
