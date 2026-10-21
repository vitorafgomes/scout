using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common.Abstractions;
using Scout.Plugin.Ledger;
using Scout.Plugin.Ledger.Tests.Fakes;

namespace Scout.Plugin.Ledger.Tests;

/// <summary>
/// Verifies the plugin exposes stable Semantic Kernel function metadata the LLM
/// relies on for function-calling: the exact snake_case names plus non-empty
/// descriptions on the functions and their parameters.
/// </summary>
public sealed class LedgerPluginMetadataTests
{
    private static KernelPlugin BuildPlugin()
    {
        var store = Substitute.For<IScoutLocalStore>();
        var clock = new FixedTimeProvider(DateTimeOffset.UnixEpoch);
        var options = Options.Create(new LedgerOptions());
        return KernelPluginFactory.CreateFromObject(new LedgerPlugin(store, clock, options), "Ledger");
    }

    [Theory]
    [InlineData("record_expense")]
    [InlineData("record_income")]
    [InlineData("show_month_summary")]
    [InlineData("list_recent_entries")]
    public void Exposes_Expected_Function(string functionName)
    {
        BuildPlugin().TryGetFunction(functionName, out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("record_expense")]
    [InlineData("record_income")]
    [InlineData("show_month_summary")]
    [InlineData("list_recent_entries")]
    public void Function_Has_NonEmpty_Description(string functionName)
    {
        BuildPlugin().TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("record_expense", "amount")]
    [InlineData("record_expense", "category")]
    [InlineData("record_expense", "note")]
    [InlineData("record_income", "amount")]
    [InlineData("record_income", "source")]
    [InlineData("show_month_summary", "month")]
    [InlineData("list_recent_entries", "count")]
    public void Parameter_Has_NonEmpty_Description(string functionName, string parameterName)
    {
        BuildPlugin().TryGetFunction(functionName, out var function).ShouldBeTrue();

        var parameter = function!.Metadata.Parameters.SingleOrDefault(p => p.Name == parameterName);

        parameter.ShouldNotBeNull();
        parameter!.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Exposes_Exactly_Four_Functions()
    {
        BuildPlugin().Count().ShouldBe(4);
    }
}
