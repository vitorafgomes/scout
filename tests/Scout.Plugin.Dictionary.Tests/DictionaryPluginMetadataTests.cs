using Microsoft.SemanticKernel;
using Scout.Plugin.Dictionary;
using Scout.Plugin.Dictionary.Abstractions;

namespace Scout.Plugin.Dictionary.Tests;

/// <summary>
/// Verifies the plugin exposes Semantic Kernel function metadata the LLM relies on
/// for function-calling: stable snake_case function names plus non-empty
/// descriptions on the functions and every parameter. Each description must state
/// the English-only constraint so the model routes non-English words to Wikipedia.
/// </summary>
public sealed class DictionaryPluginMetadataTests
{
    private static KernelPlugin BuildPlugin()
    {
        var definitions = Substitute.For<IDefinitionsClient>();
        var datamuse = Substitute.For<IDatamuseClient>();
        return KernelPluginFactory.CreateFromObject(new DictionaryPlugin(definitions, datamuse), "Dictionary");
    }

    [Theory]
    [InlineData("define_word")]
    [InlineData("get_synonyms")]
    [InlineData("check_spelling")]
    public void Exposes_Expected_Function(string functionName)
    {
        var plugin = BuildPlugin();

        plugin.TryGetFunction(functionName, out _).ShouldBeTrue();
    }

    [Fact]
    public void Exposes_Exactly_The_Expected_Functions()
    {
        var plugin = BuildPlugin();

        plugin.Select(f => f.Name).OrderBy(n => n).ShouldBe(
            new[] { "check_spelling", "define_word", "get_synonyms" });
    }

    [Theory]
    [InlineData("define_word")]
    [InlineData("get_synonyms")]
    [InlineData("check_spelling")]
    public void Function_Has_NonEmpty_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("define_word")]
    [InlineData("get_synonyms")]
    [InlineData("check_spelling")]
    public void Function_Description_States_English_Only(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldContain("English words only");
    }

    [Theory]
    [InlineData("define_word")]
    [InlineData("get_synonyms")]
    [InlineData("check_spelling")]
    public void Every_Parameter_Has_NonEmpty_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        foreach (var parameter in function!.Metadata.Parameters)
        {
            parameter.Description.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Theory]
    [InlineData("define_word")]
    [InlineData("get_synonyms")]
    [InlineData("check_spelling")]
    public void Each_Function_Exposes_A_Single_Word_Parameter(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        var names = function!.Metadata.Parameters.Select(p => p.Name).ToList();
        names.ShouldContain("word");
    }
}
