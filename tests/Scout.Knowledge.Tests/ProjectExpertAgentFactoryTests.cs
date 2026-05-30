using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Scout.Knowledge;

namespace Scout.Knowledge.Tests;

/// <summary>
/// Verifies the Project Expert agent factory builds an agent bound to the supplied
/// kernel, named for routing, with grounding instructions and automatic
/// function-calling so the search_project tool is offered to the model.
/// </summary>
public sealed class ProjectExpertAgentFactoryTests
{
    private static Kernel BuildKernel() => Kernel.CreateBuilder().Build();

    [Fact]
    public void Create_Names_The_Agent_Project_Expert()
    {
        var agent = ProjectExpertAgentFactory.Create(BuildKernel());

        agent.Name.ShouldBe("Project Expert");
    }

    [Fact]
    public void Create_Sets_NonEmpty_Instructions()
    {
        var agent = ProjectExpertAgentFactory.Create(BuildKernel());

        agent.Instructions.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Instructions_Scope_The_Tool_To_Project_Questions_And_Answer_General_Directly()
    {
        ProjectExpertAgentFactory.Instructions.ShouldContain("search_project");
        ProjectExpertAgentFactory.Instructions.ShouldContain("general", Case.Insensitive);
        ProjectExpertAgentFactory.Instructions.ShouldContain("directly", Case.Insensitive);
    }

    [Fact]
    public void Create_Binds_The_Supplied_Kernel()
    {
        var kernel = BuildKernel();

        var agent = ProjectExpertAgentFactory.Create(kernel);

        agent.Kernel.ShouldBeSameAs(kernel);
    }

    [Fact]
    public void Create_Enables_Automatic_Function_Calling()
    {
        var agent = ProjectExpertAgentFactory.Create(BuildKernel());

        agent.Arguments.ShouldNotBeNull();
        var settings = agent.Arguments!.ExecutionSettings!.Values
            .OfType<OpenAIPromptExecutionSettings>()
            .Single();
        settings.FunctionChoiceBehavior.ShouldNotBeNull();
    }

    [Fact]
    public void Create_Default_Persona_Argument_Equals_Instructions()
    {
        var agent = ProjectExpertAgentFactory.Create(BuildKernel());

        agent.Arguments.ShouldNotBeNull();
        agent.Arguments!["persona"].ShouldBe(ProjectExpertAgentFactory.Instructions);
    }

    [Fact]
    public async Task Create_Default_Persona_Renders_To_Instructions_Verbatim()
    {
        var agent = ProjectExpertAgentFactory.Create(BuildKernel());

        // The templated agent carries "{{$persona}}" as its instruction template and
        // the default persona argument is Instructions, so rendering the template with
        // the agent's own Arguments yields Instructions byte-for-byte: the default
        // persona is behavior-neutral versus the pre-template factory. The persona input
        // is marked safe so SK does not HTML-encode the apostrophe in Instructions.
        var config = new PromptTemplateConfig
        {
            Template = "{{$persona}}",
            TemplateFormat = PromptTemplateConfig.SemanticKernelTemplateFormat,
            InputVariables =
            {
                new InputVariable { Name = "persona", AllowDangerouslySetContent = true },
            },
        };
        var template = new KernelPromptTemplateFactory().Create(config);

        var rendered = await template.RenderAsync(BuildKernel(), agent.Arguments);

        rendered.ShouldBe(ProjectExpertAgentFactory.Instructions);
    }

    [Fact]
    public void BuildInstructions_Names_The_Single_Configured_Project()
    {
        var instructions = ProjectExpertAgentFactory.BuildInstructions(new[] { "messaging-explorer" });

        instructions.ShouldContain("messaging-explorer");
        instructions.ShouldNotContain("cegos-buddy");
    }

    [Fact]
    public void BuildInstructions_Names_All_Configured_Projects()
    {
        var instructions = ProjectExpertAgentFactory.BuildInstructions(
            new[] { "messaging-explorer", "scout" });

        instructions.ShouldContain("messaging-explorer");
        instructions.ShouldContain("scout");
    }

    [Fact]
    public void BuildInstructions_Empty_Falls_Back_To_Base_Without_Throwing()
    {
        var instructions = ProjectExpertAgentFactory.BuildInstructions(Array.Empty<string>());

        instructions.ShouldBe(ProjectExpertAgentFactory.Instructions);
    }

    [Fact]
    public void BuildInstructions_Begins_With_The_Base_Instructions()
    {
        var instructions = ProjectExpertAgentFactory.BuildInstructions(new[] { "messaging-explorer" });

        instructions.ShouldStartWith(ProjectExpertAgentFactory.Instructions);
    }

    [Fact]
    public void Create_With_Projects_Sets_Persona_To_Grounded_Instructions()
    {
        var agent = ProjectExpertAgentFactory.Create(BuildKernel(), new[] { "messaging-explorer" });

        agent.Arguments.ShouldNotBeNull();
        agent.Arguments!["persona"].ShouldBe(
            ProjectExpertAgentFactory.BuildInstructions(new[] { "messaging-explorer" }));
    }

    [Fact]
    public void Create_With_Empty_Projects_Falls_Back_To_Base_Persona()
    {
        var agent = ProjectExpertAgentFactory.Create(BuildKernel(), Array.Empty<string>());

        agent.Arguments.ShouldNotBeNull();
        agent.Arguments!["persona"].ShouldBe(ProjectExpertAgentFactory.Instructions);
    }
}
