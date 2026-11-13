using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Scout.Audit;
using Scout.Conversation.Routing;
using Scout.Llm.Abstractions;
using Scout.Skills;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

/// <summary>
/// Every function on the host's kernel is declared by the chat's general set, a skill or a job, every declared name
/// resolves to a kernel function, the general set holds no write, and only the skills that act declare writes.
/// </summary>
public sealed class FunctionDeclarationTests : IDisposable
{
    private static readonly string[] ActingSkills = ["code-audit", "code-review", "github-issues", "project-improvements", "youtube-operator"];

    private readonly ServiceProvider _provider;

    public FunctionDeclarationTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Plugins:GitHub:Token"] = "github-dummy-not-real",
                ["Scout:Plugins:Kubernetes:KubeconfigPath"] = "/nonexistent/scout-test/kubeconfig",
                ["Scout:Plugins:Kubernetes:Contexts:0:Name"] = "dev",
                ["Scout:Plugins:YouTube:ApiKey"] = "youtube-dummy-not-real",
                ["Scout:WorkerGate:SocketPath"] = "/run/scout/cp.sock",
                ["Scout:WorkerGate:Token"] = "t0ken",
            })
            .Build();
        _provider = new ServiceCollection().AddLogging().AddScoutHost(configuration).BuildServiceProvider();
    }

    public void Dispose() => _provider.Dispose();

    private List<string> KernelFunctions() =>
        [.. _provider.GetRequiredService<Kernel>().Plugins.GetFunctionsMetadata().Select(f => $"{f.PluginName}.{f.Name}")];

    private HashSet<string> Writes() =>
        [.. _provider.GetServices<WriteFunctionDeclaration>().Select(d => $"{d.PluginName}.{d.FunctionName}")];

    private static IReadOnlyList<Skill> BundledSkills() =>
        new SkillFileLoader(NullLogger<SkillFileLoader>.Instance).Load(Path.Combine(AppContext.BaseDirectory, "skills"));

    private static IEnumerable<string> AuditJob() => AuditBatchRunner.Offered.Select(o => $"{o.Plugin}.{o.Function}");

    [Fact]
    public void The_Host_Kernel_Holds_All_58_Functions_And_21_Of_Them_Are_Declared_Writes()
    {
        var functions = KernelFunctions();

        functions.Count.ShouldBe(58);
        functions.ShouldBeUnique();
        Writes().Count.ShouldBe(21);
        Writes().ShouldBeSubsetOf(functions);
    }

    [Fact]
    public void Every_Kernel_Function_Is_Declared_By_The_General_Set_A_Skill_Or_A_Job()
    {
        var declared = ChatFunctions.General.Concat(BundledSkills().SelectMany(s => s.Functions)).Concat(AuditJob()).ToHashSet(StringComparer.Ordinal);

        KernelFunctions().Where(f => !declared.Contains(f)).ShouldBeEmpty();
    }

    [Fact]
    public void Every_Name_The_General_Set_A_Skill_Or_A_Job_Declares_Is_A_Kernel_Function()
    {
        var functions = KernelFunctions().ToHashSet(StringComparer.Ordinal);

        ChatFunctions.General.Where(f => !functions.Contains(f)).ShouldBeEmpty();
        AuditJob().Where(f => !functions.Contains(f)).ShouldBeEmpty();
        BundledSkills()
            .SelectMany(s => s.Functions.Where(f => !functions.Contains(f)).Select(f => $"{s.Name}: {f}"))
            .ShouldBeEmpty();
    }

    [Fact]
    public void The_General_Set_Is_Every_Kernel_Function_That_Is_Not_A_Write()
    {
        var writes = Writes();

        ChatFunctions.General.Where(writes.Contains).ShouldBeEmpty();
        ChatFunctions.General.ShouldBe(KernelFunctions().Where(f => !writes.Contains(f)), ignoreOrder: true);
    }

    [Fact]
    public void Only_The_Skills_That_Act_Declare_Writes_And_Every_Write_Is_Declared_By_One()
    {
        var writes = Writes();
        var skills = BundledSkills();

        skills.Where(s => s.Functions.Any(writes.Contains)).Select(s => s.Name).Order(StringComparer.Ordinal).ShouldBe(ActingSkills);
        writes.Where(w => !skills.Any(s => s.Functions.Contains(w))).ShouldBeEmpty();
        AuditJob().Where(writes.Contains).ShouldBeEmpty();
    }

    [Fact]
    public void Every_Function_A_Skill_Body_Names_Is_On_Its_Functions_Line_Or_In_The_General_Set_When_It_Has_None()
    {
        var functions = KernelFunctions();
        var missing = new List<string>();
        foreach (var skill in BundledSkills())
        {
            foreach (var function in functions.Where(f => Regex.IsMatch(skill.Body, $@"\b{Regex.Escape(f.Split('.')[1])}\b")))
            {
                var allowed = skill.Functions.Count > 0 ? skill.Functions : ChatFunctions.General;
                if (!allowed.Contains(function))
                {
                    missing.Add($"{skill.Name}: {function}");
                }
            }
        }

        missing.ShouldBeEmpty();
    }
}
