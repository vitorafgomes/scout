using Microsoft.SemanticKernel;
using Scout.Plugin.Currency;
using Scout.Plugin.Currency.Abstractions;

namespace Scout.Plugin.Currency.Tests;

/// <summary>
/// Verifies the plugin exposes Semantic Kernel function metadata the LLM relies
/// on for function-calling: stable function names plus non-empty descriptions on
/// the functions and their parameters.
/// </summary>
public sealed class CurrencyPluginMetadataTests
{
    private static KernelPlugin BuildPlugin()
    {
        var client = Substitute.For<ICurrencyClient>();
        return KernelPluginFactory.CreateFromObject(new CurrencyPlugin(client), "Currency");
    }

    [Theory]
    [InlineData("get_exchange_rate")]
    [InlineData("convert_currency")]
    [InlineData("get_rate_trend")]
    public void Exposes_Expected_Function(string functionName)
    {
        BuildPlugin().TryGetFunction(functionName, out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("get_exchange_rate")]
    [InlineData("convert_currency")]
    [InlineData("get_rate_trend")]
    public void Function_Has_NonEmpty_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("get_exchange_rate")]
    [InlineData("convert_currency")]
    [InlineData("get_rate_trend")]
    public void Every_Parameter_Has_A_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        foreach (var parameter in function!.Metadata.Parameters)
        {
            parameter.Description.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Theory]
    [InlineData("get_exchange_rate")]
    [InlineData("convert_currency")]
    [InlineData("get_rate_trend")]
    public void CurrencyScoped_Function_Exposes_From_And_To(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        var from = function!.Metadata.Parameters.SingleOrDefault(p => p.Name == "from");
        var to = function.Metadata.Parameters.SingleOrDefault(p => p.Name == "to");

        from.ShouldNotBeNull();
        to.ShouldNotBeNull();
        from!.Description.ShouldNotBeNullOrWhiteSpace();
        to!.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Convert_Exposes_Amount_Parameter_With_Description()
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction("convert_currency", out var function).ShouldBeTrue();

        var amount = function!.Metadata.Parameters.SingleOrDefault(p => p.Name == "amount");

        amount.ShouldNotBeNull();
        amount!.Description.ShouldNotBeNullOrWhiteSpace();
    }
}
