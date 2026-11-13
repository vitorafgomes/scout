using Microsoft.SemanticKernel;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Scout.Skills.Tests;

/// <summary>
/// Verifies the plugin exposes Semantic Kernel function metadata the LLM relies
/// on for function-calling: stable function names, non-empty descriptions, and a
/// described parameter on <c>load_skill</c>.
/// </summary>
public sealed class SkillsPluginMetadataTests
{
    private static KernelPlugin BuildPlugin()
    {
        var catalog = Substitute.For<ISkillCatalog>();
        catalog.All().Returns([]);
        return KernelPluginFactory.CreateFromObject(new SkillsPlugin(catalog), "Skills");
    }

    [Theory]
    [InlineData("list_skills")]
    [InlineData("load_skill")]
    public void Exposes_Expected_Function(string functionName)
    {
        var plugin = BuildPlugin();

        plugin.TryGetFunction(functionName, out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("list_skills")]
    [InlineData("load_skill")]
    public void Function_Has_NonEmpty_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void LoadSkill_Exposes_Name_Parameter_With_Description()
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction("load_skill", out var function).ShouldBeTrue();

        var name = function!.Metadata.Parameters.SingleOrDefault(p => p.Name == "name");

        name.ShouldNotBeNull();
        name!.Description.ShouldNotBeNullOrWhiteSpace();
    }
}
