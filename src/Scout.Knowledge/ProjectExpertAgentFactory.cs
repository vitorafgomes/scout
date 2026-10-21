using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Scout.Knowledge;

/// <summary>Builds the single Project Expert chat agent over the shared kernel.</summary>
public static class ProjectExpertAgentFactory
{
    /// <summary>Base instructions kept here so tests can assert grounding behavior.</summary>
    public const string Instructions =
        "You are Scout's Project Expert. Use the search_project tool ONLY for questions about the " +
        "user's software projects (architecture, business rules, and how things are implemented). " +
        "When it grounds an answer in retrieved context, cite the source file(s); if that context " +
        "does not cover the question, say so plainly. Answer general or conversational questions " +
        "directly, without calling the tool.";

    /// <summary>
    /// Builds the agent persona, naming the configured projects so the model grounds on
    /// real project names instead of any example in the tool schema.
    /// </summary>
    /// <param name="configuredProjects">
    /// The configured project names. Empty yields the base <see cref="Instructions"/>.
    /// </param>
    /// <returns>The base instructions, extended with the configured project names when any.</returns>
    public static string BuildInstructions(IReadOnlyList<string> configuredProjects)
    {
        ArgumentNullException.ThrowIfNull(configuredProjects);

        var names = configuredProjects
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();
        if (names.Length == 0)
        {
            return Instructions;
        }

        return Instructions +
            " The projects you can search are: " + string.Join(", ", names) +
            ". When the user does not name a project, default to the configured project(s); " +
            "never invent a project name.";
    }

    /// <summary>Creates a Project Expert agent bound to the supplied kernel.</summary>
    /// <param name="kernel">The shared kernel carrying the search_project plugin.</param>
    /// <returns>A configured chat agent with automatic function-calling.</returns>
    public static ChatCompletionAgent Create(Kernel kernel)
        => Create(kernel, Array.Empty<string>());

    /// <summary>Creates a Project Expert agent grounded on the configured projects.</summary>
    /// <param name="kernel">The shared kernel carrying the search_project plugin.</param>
    /// <param name="configuredProjects">
    /// The configured project names baked into the default persona. Empty yields the
    /// base <see cref="Instructions"/>.
    /// </param>
    /// <returns>A configured chat agent with automatic function-calling.</returns>
    public static ChatCompletionAgent Create(Kernel kernel, IReadOnlyList<string> configuredProjects)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(configuredProjects);

        // Templated instruction so a per-turn persona can override the default via
        // KernelArguments["persona"]. The default persona is the grounded instruction set,
        // so with no per-turn override the rendered instruction names the configured projects.
        // The persona variable is rendered verbatim: SK HTML-encodes template inputs by
        // default, which would corrupt characters such as the apostrophe in Instructions.
        var config = new PromptTemplateConfig
        {
            Template = "{{$persona}}",
            TemplateFormat = PromptTemplateConfig.SemanticKernelTemplateFormat,
            Name = "Project Expert",
            InputVariables =
            {
                new InputVariable { Name = "persona", AllowDangerouslySetContent = true },
            },
        };

        return new ChatCompletionAgent(config, new KernelPromptTemplateFactory())
        {
            Name = "Project Expert",
            Kernel = kernel,
            Arguments = new KernelArguments(new OpenAIPromptExecutionSettings
            {
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
            })
            {
                ["persona"] = BuildInstructions(configuredProjects),
            },
        };
    }
}
