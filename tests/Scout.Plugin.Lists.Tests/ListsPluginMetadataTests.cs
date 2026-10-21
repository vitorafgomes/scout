using Microsoft.SemanticKernel;
using Scout.Plugin.Common.Abstractions;
using Scout.Plugin.Lists;

namespace Scout.Plugin.Lists.Tests;

/// <summary>
/// Verifies the plugin exposes Semantic Kernel function metadata the LLM relies
/// on for function-calling: the exact snake_case function names plus non-empty
/// descriptions on the functions and their parameters.
/// </summary>
public sealed class ListsPluginMetadataTests
{
    private static KernelPlugin BuildPlugin()
    {
        var store = Substitute.For<IScoutLocalStore>();
        return KernelPluginFactory.CreateFromObject(new ListsPlugin(store), "Lists");
    }

    [Theory]
    [InlineData("add_to_list")]
    [InlineData("show_list")]
    [InlineData("remove_from_list")]
    [InlineData("clear_list")]
    public void Exposes_Expected_Function(string functionName)
    {
        var plugin = BuildPlugin();

        plugin.TryGetFunction(functionName, out _).ShouldBeTrue();
    }

    [Fact]
    public void Exposes_Exactly_The_Four_Expected_Functions()
    {
        var plugin = BuildPlugin();

        plugin.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ShouldBe(
            ["add_to_list", "clear_list", "remove_from_list", "show_list"]);
    }

    [Theory]
    [InlineData("add_to_list")]
    [InlineData("show_list")]
    [InlineData("remove_from_list")]
    [InlineData("clear_list")]
    public void Function_Has_NonEmpty_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("add_to_list")]
    [InlineData("show_list")]
    [InlineData("remove_from_list")]
    [InlineData("clear_list")]
    public void Every_Parameter_Has_NonEmpty_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        foreach (var parameter in function!.Metadata.Parameters)
        {
            parameter.Description.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void ListName_And_Item_Parameters_Are_Exposed_With_Descriptions()
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction("add_to_list", out var function).ShouldBeTrue();

        var listName = function!.Metadata.Parameters.SingleOrDefault(p => p.Name == "listName");
        var item = function.Metadata.Parameters.SingleOrDefault(p => p.Name == "item");

        listName.ShouldNotBeNull();
        item.ShouldNotBeNull();
        listName!.Description.ShouldNotBeNullOrWhiteSpace();
        item!.Description.ShouldNotBeNullOrWhiteSpace();
    }
}
