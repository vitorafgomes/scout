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
}
