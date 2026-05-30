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

        return new ChatCompletionAgent
        {
            Name = "Project Expert",
            Instructions = Instructions,
            Kernel = kernel,
            Arguments = new KernelArguments(new OpenAIPromptExecutionSettings
            {
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
            }),
        };
    }
}
