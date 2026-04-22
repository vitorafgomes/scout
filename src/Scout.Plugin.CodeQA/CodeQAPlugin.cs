using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Core.Plugins;
using Scout.Plugin.CodeQA.Functions;

namespace Scout.Plugin.CodeQA;

/// <summary>
/// Scout feature plugin that answers natural-language questions about a
/// configured GitHub repository using retrieval-augmented generation.
/// Exposes two Semantic Kernel functions — <c>ask_code</c> and
/// <c>search_code</c> — both read-only.
/// </summary>
public sealed class CodeQAPlugin : IScoutPlugin
{
    public const string PluginName = "CodeQA";

    public string Name => PluginName;

    public PluginCapabilities Capabilities => PluginCapabilities.ReadOnly;

    public KernelPlugin Build(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var functions = services.GetRequiredService<CodeQAFunctions>();
        return KernelPluginFactory.CreateFromObject(functions, PluginName);
    }
}
