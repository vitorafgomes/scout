using Microsoft.SemanticKernel;

namespace Scout.Llm.Tests;

/// <summary>
/// The per-turn copy of the shared kernel holds only the declared functions and keeps the shared kernel's invocation
/// filters, and a function the model names counts as declared in its dashed, underscored, dotted or bare form.
/// </summary>
public sealed class KernelScopeTests
{
    private static readonly string[] Declared = ["GitHubActions.create_issue", "GitHub.get_issue"];

    private static Kernel Shared()
    {
        var builder = Kernel.CreateBuilder();
        builder.Plugins.AddFromFunctions("GitHub",
        [
            KernelFunctionFactory.CreateFromMethod(() => "issue", "get_issue"),
            KernelFunctionFactory.CreateFromMethod(() => "repos", "list_repositories"),
        ]);
        builder.Plugins.AddFromFunctions("GitHubActions", [KernelFunctionFactory.CreateFromMethod(() => "opened", "create_issue")]);
        var kernel = builder.Build();
        kernel.FunctionInvocationFilters.Add(new CountingFilter());
        return kernel;
    }

    [Fact]
    public void The_Copy_Holds_Only_The_Declared_Functions_That_Exist_And_Leaves_The_Shared_Kernel_Whole()
    {
        var shared = Shared();

        var turn = KernelScope.Only(shared, ["GitHub.get_issue", "GitHubActions.create_issue", "GitHub.missing", "not-qualified"]);

        turn.Plugins.GetFunctionsMetadata().Select(f => $"{f.PluginName}.{f.Name}").Order(StringComparer.Ordinal)
            .ShouldBe(["GitHub.get_issue", "GitHubActions.create_issue"]);
        shared.Plugins.GetFunctionsMetadata().Count.ShouldBe(3);
    }

    [Fact]
    public void An_Empty_Set_Gives_A_Copy_With_No_Function()
    {
        KernelScope.Only(Shared(), []).Plugins.GetFunctionsMetadata().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_Copy_Keeps_The_Shared_Kernels_Invocation_Filters()
    {
        var shared = Shared();
        var filter = shared.FunctionInvocationFilters.OfType<CountingFilter>().Single();

        await KernelScope.Only(shared, ["GitHub.get_issue"]).InvokeAsync("GitHub", "get_issue");

        filter.Calls.ShouldBe(["GitHub.get_issue"]);
    }

    [Theory]
    [InlineData("GitHubActions-create_issue", true)]
    [InlineData("GitHubActions_create_issue", true)]
    [InlineData("GitHubActions.create_issue", true)]
    [InlineData("create_issue", true)]
    [InlineData("CREATE_ISSUE", true)]
    [InlineData("GitHub-get_issue", true)]
    [InlineData("GitHubActions-submit_pull_request_review", false)]
    [InlineData("submit_pull_request_review", false)]
    [InlineData("GitHub-create_issue", false)]
    [InlineData("issue", false)]
    [InlineData("", false)]
    public void A_Called_Name_Counts_As_Declared_Only_When_It_Names_A_Declared_Function(string called, bool declared)
    {
        KernelScope.IsDeclared(called, Declared).ShouldBe(declared);
    }

    private sealed class CountingFilter : IFunctionInvocationFilter
    {
        public List<string> Calls { get; } = [];

        public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
        {
            Calls.Add($"{context.Function.PluginName}.{context.Function.Name}");
            await next(context);
        }
    }
}
