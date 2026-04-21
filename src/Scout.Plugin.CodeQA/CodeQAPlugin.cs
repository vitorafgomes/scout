using Microsoft.SemanticKernel;
using Scout.Core.Plugins;

namespace Scout.Plugin.CodeQA;

/// <summary>
/// Scout feature plugin that will answer natural-language questions about a
/// configured GitHub repository using retrieval-augmented generation. This
/// scaffold ships the <see cref="IScoutPlugin"/> surface, the read-only
/// capability declaration, and an empty <see cref="KernelPlugin"/> body.
/// The SK functions (<c>ask_code</c>, <c>search_code</c>) land in a later
/// PR of Plan 4 once the retrieval pipeline is in place.
/// </summary>
public sealed class CodeQAPlugin : IScoutPlugin
{
    public const string PluginName = "CodeQA";

    public string Name => PluginName;

    public PluginCapabilities Capabilities => PluginCapabilities.ReadOnly;

    public KernelPlugin Build(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // Functions arrive in PR 5 of Plan 4 via CodeQAFunctions.
        return KernelPluginFactory.CreateFromFunctions(
            pluginName: PluginName,
            description: "Answers questions about the configured GitHub repository using RAG.",
            functions: []);
    }
}
