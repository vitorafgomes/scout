using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Scout.Audit.Tests;

public sealed class AuditServiceCollectionExtensionsTests
{
    private static ServiceProvider Host(string? turnTimeout, string? provider = null, string? parallel = null)
    {
        var settings = new Dictionary<string, string?>();
        if (turnTimeout is not null)
        {
            settings["Scout:Audit:TurnTimeout"] = turnTimeout;
        }

        if (provider is not null)
        {
            settings["Scout:Llm:Provider"] = provider;
        }

        if (parallel is not null)
        {
            settings["Scout:Audit:ReviewParallelBatches"] = parallel;
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

    [Theory]
    [InlineData(null, null, 1)]
    [InlineData("Ollama", null, 1)]
    [InlineData("ollama", null, 1)]
    [InlineData("OpenAI", null, 4)]
    [InlineData("Ollama", "3", 3)]
    [InlineData("OpenAI", "8", 8)]
    [InlineData("OpenAI", "1", 1)]
    public void Review_batches_run_four_at_a_time_and_one_at_a_time_on_the_local_provider_unless_set(string? provider, string? parallel, int expected)
    {
        using var host = Host(null, provider, parallel);

        Should.NotThrow(() => host.GetRequiredService<IStartupValidator>().Validate());

        host.GetRequiredService<IOptions<AuditOptions>>().Value.ReviewParallelBatches.ShouldBe(
            expected, $"provider {provider ?? "(not set)"}, Scout:Audit:ReviewParallelBatches {parallel ?? "(not set)"}");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("9")]
    [InlineData("-1")]
    public void Review_batches_at_once_outside_1_to_8_are_rejected_when_the_host_starts(string parallel)
    {
        using var host = Host(null, "OpenAI", parallel);

        var failure = Should.Throw<OptionsValidationException>(() => host.GetRequiredService<IStartupValidator>().Validate());

        failure.Message.ShouldBe("Scout:Audit:ReviewParallelBatches must be 1 to 8.", customMessage: $"{parallel} batches at once is refused when the host starts");
    }
}
