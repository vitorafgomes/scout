using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Scout.Audit.Tests;

public sealed class AuditServiceCollectionExtensionsTests
{
    private static ServiceProvider Host(string? turnTimeout)
    {
        var settings = new Dictionary<string, string?>();
        if (turnTimeout is not null)
        {
            settings["Scout:Audit:TurnTimeout"] = turnTimeout;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection().AddAudit(configuration).BuildServiceProvider();
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:05")]
    [InlineData("-00:00:00.001")]
    public void A_turn_time_limit_that_is_not_above_zero_is_rejected_when_the_host_starts(string turnTimeout)
    {
        using var host = Host(turnTimeout);

        var failure = Should.Throw<OptionsValidationException>(() => host.GetRequiredService<IStartupValidator>().Validate());

        failure.Message.ShouldBe("Scout:Audit:TurnTimeout must be above zero; every batch turn would end before the model answers.");
    }

    [Theory]
    [InlineData(null, "00:10:00")]
    [InlineData("00:00:01", "00:00:01")]
    public void A_turn_time_limit_above_zero_or_the_default_starts(string? turnTimeout, string limit)
    {
        using var host = Host(turnTimeout);

        Should.NotThrow(() => host.GetRequiredService<IStartupValidator>().Validate());

        host.GetRequiredService<IOptions<AuditOptions>>().Value.TurnTimeout.ShouldBe(TimeSpan.Parse(limit, CultureInfo.InvariantCulture));
    }
}
