using Microsoft.SemanticKernel;
using Scout.Plugin.Common.Abstractions;
using Scout.Plugin.Notes;
using Scout.Plugin.Notes.Tests.Fakes;

namespace Scout.Plugin.Notes.Tests;

/// <summary>
/// Verifies the plugin exposes stable Semantic Kernel function metadata the LLM
/// relies on for function-calling: the exact snake_case names plus non-empty
/// descriptions on the functions and their parameters.
/// </summary>
public sealed class NotesPluginMetadataTests
{
    private static KernelPlugin BuildPlugin()
    {
        var store = Substitute.For<IScoutLocalStore>();
        var clock = new FixedTimeProvider(DateTimeOffset.UnixEpoch);
        return KernelPluginFactory.CreateFromObject(new NotesPlugin(store, clock), "Notes");
    }

    [Theory]
    [InlineData("add_note")]
    [InlineData("search_notes")]
    [InlineData("list_recent_notes")]
    [InlineData("delete_note")]
    public void Exposes_Expected_Function(string functionName)
    {
        BuildPlugin().TryGetFunction(functionName, out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("add_note")]
    [InlineData("search_notes")]
    [InlineData("list_recent_notes")]
    [InlineData("delete_note")]
    public void Function_Has_NonEmpty_Description(string functionName)
    {
        BuildPlugin().TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("add_note", "text")]
    [InlineData("add_note", "tags")]
    [InlineData("search_notes", "query")]
    [InlineData("list_recent_notes", "count")]
    [InlineData("delete_note", "id")]
    public void Parameter_Has_NonEmpty_Description(string functionName, string parameterName)
    {
        BuildPlugin().TryGetFunction(functionName, out var function).ShouldBeTrue();

        var parameter = function!.Metadata.Parameters.SingleOrDefault(p => p.Name == parameterName);

        parameter.ShouldNotBeNull();
        parameter!.Description.ShouldNotBeNullOrWhiteSpace();
    }
}
