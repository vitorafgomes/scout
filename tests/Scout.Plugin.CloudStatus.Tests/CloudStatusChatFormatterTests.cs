using Scout.Plugin.CloudStatus.Domain;

namespace Scout.Plugin.CloudStatus.Tests;

public class CloudStatusChatFormatterTests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 4, 21, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Format_Empty_List_Returns_Hint_Message()
    {
        var text = CloudStatusChatFormatter.Format(Array.Empty<StatusReport>());

        text.Should().Be("No cloud providers are configured.");
    }

    [Fact]
    public void Format_List_Produces_One_Bullet_Per_Report()
    {
        var reports = new StatusReport[]
        {
            new("Azure", "sub-1", HealthLevel.Healthy, Array.Empty<ServiceStatus>(), FixedTime),
            new("AWS",   "acc-1", HealthLevel.Degraded, Array.Empty<ServiceStatus>(), FixedTime,
                Message: "elevated latency in eu-west-1"),
        };

        var text = CloudStatusChatFormatter.Format(reports);

        text.Should().Contain("Cloud health:");
        text.Should().Contain("- Azure (sub-1): Healthy");
        text.Should().Contain("- AWS (acc-1): Degraded — elevated latency in eu-west-1");
    }

    [Fact]
    public void Format_Single_Report_Matches_Line_Produced_In_List()
    {
        var report = new StatusReport(
            "OracleCloud", "eu-london-1", HealthLevel.Unknown,
            Array.Empty<ServiceStatus>(), FixedTime,
            Message: "missing credential");

        var text = CloudStatusChatFormatter.Format(report);

        text.Should().Be("- OracleCloud (eu-london-1): Unknown — missing credential");
    }

    [Fact]
    public void Format_Null_List_Throws()
    {
        Action act = () => CloudStatusChatFormatter.Format((IReadOnlyList<StatusReport>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Format_Null_Report_Throws()
    {
        Action act = () => CloudStatusChatFormatter.Format((StatusReport)null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
