using Microsoft.SemanticKernel;
using Scout.Plugin.Common.Abstractions;
using Scout.Plugin.Reminders;
using Scout.Plugin.Reminders.Tests.Fakes;

namespace Scout.Plugin.Reminders.Tests;

/// <summary>
/// Verifies the plugin exposes stable Semantic Kernel function metadata the LLM
/// relies on for function-calling: the exact snake_case names plus non-empty
/// descriptions on the functions and their parameters.
/// </summary>
public sealed class RemindersPluginMetadataTests
{
    private static KernelPlugin BuildPlugin()
    {
        var store = Substitute.For<IScoutLocalStore>();
        var clock = new FixedTimeProvider(DateTimeOffset.UnixEpoch);
        return KernelPluginFactory.CreateFromObject(
            new RemindersPlugin(store, clock, new RemindersOptions()), "Reminders");
    }

    [Theory]
    [InlineData("add_reminder")]
    [InlineData("list_reminders")]
    [InlineData("complete_reminder")]
    [InlineData("delete_reminder")]
    public void Exposes_Expected_Function(string functionName)
    {
        BuildPlugin().TryGetFunction(functionName, out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("add_reminder")]
    [InlineData("list_reminders")]
    [InlineData("complete_reminder")]
    [InlineData("delete_reminder")]
    public void Function_Has_NonEmpty_Description(string functionName)
    {
        BuildPlugin().TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("add_reminder", "text")]
    [InlineData("add_reminder", "when")]
    [InlineData("list_reminders", "filter")]
    [InlineData("complete_reminder", "id")]
    [InlineData("delete_reminder", "id")]
    public void Parameter_Has_NonEmpty_Description(string functionName, string parameterName)
    {
        BuildPlugin().TryGetFunction(functionName, out var function).ShouldBeTrue();

        var parameter = function!.Metadata.Parameters.SingleOrDefault(p => p.Name == parameterName);

        parameter.ShouldNotBeNull();
        parameter!.Description.ShouldNotBeNullOrWhiteSpace();
    }
}
