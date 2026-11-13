using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Llm.Abstractions;
using Scout.Llm.Guard;

namespace Scout.Llm.Tests.Guard;

public sealed class ActivityFilterTests
{
    private static Kernel Kernel(bool fails = false)
    {
        var builder = Microsoft.SemanticKernel.Kernel.CreateBuilder();
        builder.Services.AddSingleton<IFunctionInvocationFilter>(new ActivityFilter());
        builder.Plugins.Add(KernelPluginFactory.CreateFromFunctions("GitHub",
        [
            KernelFunctionFactory.CreateFromMethod(
                (string owner, string repo, int number, string path, string reference) => fails ? throw new InvalidOperationException("boom") : "diff",
                "get_pull_request_file"),
        ]));
        return builder.Build();
    }

    private static KernelArguments File() => new()
    {
        ["owner"] = "octocat",
        ["repo"] = "hello",
        ["number"] = 42,
        ["path"] = "src/Checkout.cs",
        ["reference"] = "",
    };

    [Fact]
    public async Task A_Function_The_Model_Calls_Is_Announced_To_The_Turn_Before_It_Runs()
    {
        var reported = new List<string>();
        using var scope = TurnProgress.Enter((activity, _) => { reported.Add(activity); return Task.CompletedTask; });

        var result = await Kernel().InvokeAsync("GitHub", "get_pull_request_file", File());

        result.GetValue<string>().ShouldBe("diff");
        reported.ShouldBe(["Get pull request file: octocat hello 42 src/Checkout.cs"]);
    }

    [Fact]
    public async Task Outside_A_Turn_The_Function_Runs_And_Nothing_Is_Reported()
    {
        TurnProgress.IsEntered.ShouldBeFalse();

        (await Kernel().InvokeAsync("GitHub", "get_pull_request_file", File())).GetValue<string>().ShouldBe("diff");
    }

    [Fact]
    public async Task A_Chat_That_Cannot_Show_Progress_Never_Costs_The_Function()
    {
        using var scope = TurnProgress.Enter((_, _) => throw new IOException("pipe closed"));

        (await Kernel().InvokeAsync("GitHub", "get_pull_request_file", File())).GetValue<string>().ShouldBe("diff");
    }

    [Fact]
    public async Task The_Scope_Ends_With_The_Turn()
    {
        var reported = new List<string>();
        using (TurnProgress.Enter((activity, _) => { reported.Add(activity); return Task.CompletedTask; }))
        {
            await TurnProgress.ReportAsync("inside", CancellationToken.None);
        }

        await TurnProgress.ReportAsync("outside", CancellationToken.None);

        reported.ShouldBe(["inside"]);
        TurnProgress.IsEntered.ShouldBeFalse();
    }

    [Theory]
    [InlineData("get_pull_request", "octocat", "Get pull request: octocat")]
    [InlineData("search_code", "catch (Exception)", "Search code: catch (Exception)")]
    [InlineData("list_repositories", null, "List repositories")]
    public void The_Function_Name_Reads_As_A_Sentence_With_Its_Short_Arguments(string function, string? argument, string expected)
    {
        var arguments = new KernelArguments();
        if (argument is not null)
        {
            arguments["query"] = argument;
        }

        ActivityFilter.Describe(function, arguments).ShouldBe(expected);
    }

    [Fact]
    public void Only_An_Allow_Listed_Short_Single_Line_Argument_Reaches_The_Line_Never_A_Body_A_Token_Or_An_Unknown_Name()
    {
        var arguments = new KernelArguments
        {
            ["owner"] = "octocat",
            ["body"] = "## Review\nlong text",
            ["comments"] = "[{\"path\":\"a.cs\"}]",
            ["token"] = "ghp_secret",
            ["bearerToken"] = "ya29.secret",
            ["path"] = new string('x', ActivityFilter.MaxArgumentLength + 1),
            ["query"] = "a\nb",
            ["flag"] = true,
        };

        ActivityFilter.Describe("submit_pull_request_review", arguments).ShouldBe("Submit pull request review: octocat");
        ActivityFilter.Describe("x", new KernelArguments { ["path"] = new string('y', ActivityFilter.MaxArgumentLength) }).Length.ShouldBeLessThanOrEqualTo(ActivityFilter.MaxLength);
    }

    [Fact]
    public void Arguments_Handed_As_Json_Elements_Read_The_Same_As_Typed_Ones()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{"owner":"octocat","number":42,"path":"src/Checkout.cs","nested":{"a":1},"body":"long"}""");
        var arguments = new KernelArguments();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            arguments[property.Name] = property.Value.Clone();
        }

        ActivityFilter.Describe("get_pull_request_file", arguments).ShouldBe("Get pull request file: octocat 42 src/Checkout.cs");
    }

    [Fact]
    public async Task A_Turn_Entered_Inside_Another_Leaves_The_Outer_Routing_Intact()
    {
        var outer = new List<string>();
        var inner = new List<string>();
        using (TurnProgress.Enter((activity, _) => { outer.Add(activity); return Task.CompletedTask; }))
        {
            using (TurnProgress.Enter((activity, _) => { inner.Add(activity); return Task.CompletedTask; }))
            {
                await TurnProgress.ReportAsync("inner", CancellationToken.None);
            }

            await TurnProgress.ReportAsync("outer again", CancellationToken.None);
        }

        inner.ShouldBe(["inner"]);
        outer.ShouldBe(["outer again"]);
        TurnProgress.IsEntered.ShouldBeFalse();
    }
}
