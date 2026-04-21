using Scout.Plugin.CloudStatus.Domain;

namespace Scout.Plugin.CloudStatus.Tests.Domain;

public class StatusReportTests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 4, 21, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Ctor_Rejects_Empty_Provider()
    {
        Action create = () => new StatusReport(
            Provider: "",
            Scope: "scope",
            Overall: HealthLevel.Healthy,
            Services: Array.Empty<ServiceStatus>(),
            CheckedAt: FixedTime);

        create.Should().Throw<ArgumentException>()
            .WithParameterName(nameof(StatusReport.Provider));
    }

    [Fact]
    public void Ctor_Rejects_Empty_Scope()
    {
        Action create = () => new StatusReport(
            Provider: "Azure",
            Scope: "   ",
            Overall: HealthLevel.Healthy,
            Services: Array.Empty<ServiceStatus>(),
            CheckedAt: FixedTime);

        create.Should().Throw<ArgumentException>()
            .WithParameterName(nameof(StatusReport.Scope));
    }

    [Fact]
    public void Ctor_Rejects_Null_Services_List()
    {
        Action create = () => new StatusReport(
            Provider: "Azure",
            Scope: "sub-1",
            Overall: HealthLevel.Healthy,
            Services: null!,
            CheckedAt: FixedTime);

        create.Should().Throw<ArgumentNullException>()
            .WithParameterName(nameof(StatusReport.Services));
    }

    [Fact]
    public void Services_Empty_When_No_Items_Provided()
    {
        var report = new StatusReport(
            "AWS", "account-1", HealthLevel.Unknown,
            Array.Empty<ServiceStatus>(), FixedTime);

        report.Services.Should().BeEmpty();
    }

    [Fact]
    public void Unknown_Factory_Builds_Unknown_Report_With_Given_Provider_And_Scope()
    {
        var report = StatusReport.Unknown("OracleCloud", "eu-london-1", FixedTime, "missing-cred");

        report.Provider.Should().Be("OracleCloud");
        report.Scope.Should().Be("eu-london-1");
        report.Overall.Should().Be(HealthLevel.Unknown);
        report.Services.Should().BeEmpty();
        report.Message.Should().Be("missing-cred");
        report.CheckedAt.Should().Be(FixedTime);
    }
}

public class ServiceStatusTests
{
    [Fact]
    public void Ctor_Rejects_Empty_Service()
    {
        Action create = () => new ServiceStatus(
            Service: "",
            Region: "eu-west",
            Level: HealthLevel.Healthy,
            Message: null);

        create.Should().Throw<ArgumentException>()
            .WithParameterName(nameof(ServiceStatus.Service));
    }

    [Fact]
    public void Ctor_Rejects_Empty_Region()
    {
        Action create = () => new ServiceStatus(
            Service: "SQL",
            Region: "",
            Level: HealthLevel.Healthy,
            Message: null);

        create.Should().Throw<ArgumentException>()
            .WithParameterName(nameof(ServiceStatus.Region));
    }

    [Fact]
    public void Message_Is_Optional()
    {
        var status = new ServiceStatus("SQL", "eu-west", HealthLevel.Healthy, null);

        status.Message.Should().BeNull();
    }
}
