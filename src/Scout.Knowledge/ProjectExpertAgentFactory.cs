using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Scout.Knowledge;

/// <summary>Builds the single Project Expert chat agent over the shared kernel.</summary>
public static class ProjectExpertAgentFactory
{
    /// <summary>Instructions kept here so tests can assert grounding behavior.</summary>
    public const string Instructions =
        "You are Scout's Project Expert. Use the search_project tool ONLY for questions about the " +
        "user's software projects (architecture, business rules, and how things are implemented). " +
        "When it grounds an answer in retrieved context, cite the source file(s); if that context " +
        "does not cover the question, say so plainly. Answer general or conversational questions " +
        "directly, without calling the tool.";

    /// <summary>Creates a Project Expert agent bound to the supplied kernel.</summary>
    /// <param name="kernel">The shared kernel carrying the search_project plugin.</param>
    /// <returns>A configured chat agent with automatic function-calling.</returns>
    public static ChatCompletionAgent Create(Kernel kernel)
    {
        ArgumentNullException.ThrowIfNull(kernel);

        // Templated instruction so a per-turn persona can override the default via
        // KernelArguments["persona"]. The default persona is Instructions, so with no
        // override the rendered instruction is byte-identical to the prior agent.
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
                ["persona"] = Instructions,
            },
        };
    }
}
