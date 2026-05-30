using System.ComponentModel;
using Microsoft.SemanticKernel;

namespace Scout.Knowledge;

/// <summary>SK plugin exposing project code/doc retrieval to the LLM.</summary>
public sealed class ProjectKnowledgePlugin
{
    private readonly ProjectKnowledgeService service;

    /// <summary>Creates the plugin over the retrieval service.</summary>
    /// <param name="service">The retrieval service. Must be non-null.</param>
    public ProjectKnowledgePlugin(ProjectKnowledgeService service)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
    }

    /// <summary>Search a configured project's code and docs for context.</summary>
    /// <param name="project">Configured project name.</param>
    /// <param name="question">The user's question about the project.</param>
    /// <param name="ct">Cancels the underlying retrieval.</param>
    /// <returns>A citation-tagged context string, or a clean status message.</returns>
    [KernelFunction("search_project")]
    [Description("Search a software project's code and documentation for context to answer a question about its architecture, business rules, or implementation.")]
    public Task<string> SearchAsync(
        [Description("Configured project name, e.g. cegos-buddy.")] string project,
        [Description("The user's question about the project.")] string question,
        CancellationToken ct)
        => this.service.SearchAsync(project, question, ct);

    /// <summary>Find a class, method, interface, or member by name in a configured project.</summary>
    /// <param name="project">Configured project name.</param>
    /// <param name="symbol">The symbol name to look up.</param>
    /// <param name="kind">Optional coarse kind filter ("code" or "doc").</param>
    /// <param name="top">Optional maximum number of results.</param>
    /// <param name="ct">Cancels the underlying lookup.</param>
    /// <returns>A citation-tagged context string, or a clean status message.</returns>
    [KernelFunction("find_symbol")]
    [Description("Find a class, method, interface, or member by NAME in an indexed project. Use for 'where is X' / 'find the method X' questions — exact and partial name matches, no semantic search. Prefer this over search_project when the user names a specific symbol.")]
    public Task<string> FindSymbolAsync(
        [Description("Configured project name, e.g. cegos-buddy.")] string project,
        [Description("The exact or partial symbol name to find, e.g. SearchAsync or ProjectKnowledgeService.")] string symbol,
        [Description("Optional coarse kind filter: 'code' or 'doc'. Leave empty to match any.")] string? kind,
        [Description("Optional maximum number of results; leave at 0 for the default.")] int top,
        CancellationToken ct)
        => this.service.FindSymbolAsync(project, symbol, kind, ct, top);
}
