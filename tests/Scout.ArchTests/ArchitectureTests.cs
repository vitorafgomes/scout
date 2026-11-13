using System.Reflection;
using NetArchTest.Rules;
using Shouldly;
using Xunit;

namespace Scout.ArchTests;

/// <summary>
/// The dependency graph of the project map, as assembly rules: leaves reference no other Scout
/// assembly, adapters and plugins reference neither each other nor the hosts, and the conversation core reaches only
/// Llm and Skills. A reference that ProjectReference allows but these rules forbid is a layering change, recorded in
/// the project map in the same change.
/// </summary>
public partial class ArchitectureTests
{
    private static readonly Assembly[] Adapters =
    [
        typeof(Scout.Adapter.Discord.DiscordChatAdapter).Assembly,
        typeof(Scout.Adapter.Slack.SlackChatAdapter).Assembly,
        typeof(Scout.Adapter.Teams.InboundTeamsMessage).Assembly,
        typeof(Scout.Adapter.Local.LocalChatAdapter).Assembly,
    ];

    private static readonly Assembly[] Plugins =
    [
        typeof(Scout.Plugin.GitHub.GitHubOptions).Assembly,
        typeof(Scout.Plugin.Kubernetes.KubernetesOptions).Assembly,
        typeof(Scout.Plugin.YouTube.YouTubeOptions).Assembly,
    ];

    /// <summary>Projects the map lists with "Depends on: none".</summary>
    private static readonly Assembly[] Leaves =
    [
        typeof(Scout.Llm.LlmServiceCollectionExtensions).Assembly,
        typeof(Scout.Skills.SkillSelector).Assembly,
        typeof(Scout.Knowledge.ProjectKnowledgeService).Assembly,
        typeof(Scout.Plugin.Kubernetes.KubernetesOptions).Assembly,
        typeof(Scout.Plugin.YouTube.YouTubeOptions).Assembly,
        typeof(Scout.ControlPlane.Features.FeatureCatalog).Assembly,
        typeof(Scout.Sandbox.DockerSandboxRunner).Assembly,
    ];

    private static readonly Assembly Conversation = typeof(Scout.Conversation.OriginNotifier).Assembly;

    private static readonly Assembly Operator = typeof(Scout.Operator.OperatorComments).Assembly;

    private static readonly Assembly Audit = typeof(Scout.Audit.IRepositoryAudits).Assembly;

    [Fact]
    public void Dependency_scanning_detects_a_dependency_that_is_known_to_exist()
    {
        var dependents = Types.InAssembly(Conversation).That().HaveDependencyOn("Scout.Llm").GetTypes();

        dependents.ShouldNotBeEmpty("Conversation references Llm, so the scan must see at least one type that uses it");
    }

    [Fact]
    public void Leaves_reference_no_other_scout_assembly()
    {
        foreach (var assembly in Leaves)
        {
            var self = assembly.GetName().Name!;
            var forbidden = AllScoutPrefixes().Where(prefix => prefix != self).ToArray();

            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();

            result.IsSuccessful.ShouldBeTrue(Explain(assembly, result));
        }
    }

    [Fact]
    public void Adapters_reference_neither_another_adapter_nor_a_plugin_nor_a_host()
    {
        foreach (var assembly in Adapters)
        {
            var self = assembly.GetName().Name!;
            var forbidden = Adapters.Select(Name).Where(name => name != self)
                .Concat(Plugins.Select(Name))
                .Concat(["Scout.Host", "Scout.Operator", "Scout.Knowledge", "Scout.ControlPlane"])
                .ToArray();

            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();

            result.IsSuccessful.ShouldBeTrue(Explain(assembly, result));
        }
    }

    [Fact]
    public void Plugins_reference_neither_another_plugin_nor_an_adapter_nor_a_host()
    {
        foreach (var assembly in Plugins)
        {
            var self = assembly.GetName().Name!;
            var forbidden = Plugins.Select(Name).Where(name => name != self)
                .Concat(Adapters.Select(Name))
                .Concat(["Scout.Host", "Scout.Operator", "Scout.Knowledge", "Scout.ControlPlane"])
                .ToArray();

            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();

            result.IsSuccessful.ShouldBeTrue(Explain(assembly, result));
        }
    }

    [Fact]
    public void Conversation_reaches_only_llm_and_skills()
    {
        var forbidden = AllScoutPrefixes().Where(prefix => prefix is not ("Scout.Conversation" or "Scout.Llm" or "Scout.Skills")).ToArray();

        var result = Types.InAssembly(Conversation).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();

        result.IsSuccessful.ShouldBeTrue(Explain(Conversation, result));
    }

    [Fact]
    public void Operator_reaches_the_control_plane_only_through_its_own_abstraction()
    {
        var result = Types.InAssembly(Operator)
            .ShouldNot().HaveDependencyOnAny("Scout.ControlPlane", "Scout.Host", "Grpc", "Scout.Adapter", "Scout.Plugin.GitHub", "Scout.Plugin.Kubernetes", "Scout.Knowledge")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Explain(Operator, result));
    }

    [Fact]
    public void Audit_reaches_github_and_the_model_only_through_the_kernel()
    {
        var result = Types.InAssembly(Audit)
            .ShouldNot().HaveDependencyOnAny("Scout.ControlPlane", "Scout.Host", "Grpc", "Scout.Adapter", "Scout.Plugin", "Scout.Operator")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Explain(Audit, result));
    }

    [Fact]
    public void No_library_references_a_host()
    {
        foreach (var assembly in Adapters.Concat(Plugins).Concat(Leaves).Concat([Conversation, Operator, Audit]).Distinct())
        {
            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOn("Scout.Host").GetResult();

            result.IsSuccessful.ShouldBeTrue(Explain(assembly, result));
        }
    }

    [Fact]
    public void No_Anthropic_type_is_referenced_outside_the_anthropic_provider_folder()
    {
        string[] anthropic =
        [
            "Anthropic",
            "Microsoft.Extensions.AI.AnthropicClientExtensions",
            "Microsoft.Extensions.AI.AnthropicBetaClientExtensions",
            "Microsoft.Extensions.AI.AnthropicThinkingMode",
        ];

        foreach (var assembly in Adapters.Concat(Plugins).Concat(Leaves).Concat([Conversation, Operator, Audit]).Distinct())
        {
            var result = Types.InAssembly(assembly)
                .That().DoNotResideInNamespace("Scout.Llm.Providers.Anthropic")
                .ShouldNot().HaveDependencyOnAny(anthropic)
                .GetResult();

            result.IsSuccessful.ShouldBeTrue(Explain(assembly, result));
        }
    }

    [Fact]
    public void The_Gateway_Names_No_Connector_Type_Outside_The_Provider_Folders()
    {
        var scanned = SourceText.CSharpUnder("src/Scout.Llm")
            .Where(file => !SourceText.RepoPath(file).StartsWith("src/Scout.Llm/Providers/", StringComparison.Ordinal))
            .ToList();

        var named =
            from file in scanned
            let lines = SourceText.CodeOnly(File.ReadAllText(file)).Split('\n')
            from index in Enumerable.Range(0, lines.Length)
            from match in ConnectorType().Matches(lines[index])
            select $"{SourceText.RepoPath(file)}:{index + 1} {match.Value}";

        scanned.Select(SourceText.RepoPath).ShouldContain("src/Scout.Llm/ChatGateway.cs");
        named.ToList().ShouldBeEmpty(
            "a connector's types are named only in src/Scout.Llm/Providers/, so the gateway and the rest of Scout.Llm know only Scout and Semantic Kernel types");
    }

    [Fact]
    public void No_OllamaSharp_type_is_referenced_outside_the_ollama_provider_folder()
    {
        foreach (var assembly in Adapters.Concat(Plugins).Concat(Leaves).Concat([Conversation, Operator, Audit]).Distinct())
        {
            var result = Types.InAssembly(assembly)
                .That().DoNotResideInNamespace("Scout.Llm.Providers.Ollama")
                .ShouldNot().HaveDependencyOn("OllamaSharp")
                .GetResult();

            result.IsSuccessful.ShouldBeTrue(Explain(assembly, result));
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(?:ChatTokenUsage|UsageContent|GeminiMetadata|ClientResultException|StreamingResponse\w*Update|\w+PromptExecutionSettings)\b|\bSystem\.ClientModel\b|\bMicrosoft\.SemanticKernel\.Connectors\.|(?<![\w.])(?:OpenAI|Azure)\.")]
    private static partial System.Text.RegularExpressions.Regex ConnectorType();

    public static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && File.Exists(Path.Combine(directory.FullName, "Scout.slnx")) is false)
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Scout.slnx not found above the test binary");
    }

    private static string Name(Assembly assembly) => assembly.GetName().Name!;

    private static IEnumerable<string> AllScoutPrefixes() =>
        Adapters.Concat(Plugins).Concat(Leaves).Concat([Conversation, Operator, Audit]).Select(Name).Append("Scout.Host").Distinct();

    private static string Explain(Assembly assembly, TestResult result) =>
        $"{assembly.GetName().Name} has forbidden dependencies in: {string.Join(", ", result.FailingTypeNames ?? [])}";
}
