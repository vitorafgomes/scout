using Microsoft.SemanticKernel;
using Scout.Plugin.CodeReview.Abstractions;

namespace Scout.Plugin.CodeReview.Tests;

/// <summary>
/// Verifies the plugin exposes Semantic Kernel function metadata the LLM relies
/// on for function-calling: stable function names plus non-empty descriptions on
/// the functions and their key parameters.
/// </summary>
public sealed class CodeReviewPluginMetadataTests
{
    private static KernelPlugin BuildPlugin()
    {
        var reviewer = Substitute.For<ICodeReviewer>();
        return KernelPluginFactory.CreateFromObject(new CodeReviewPlugin(reviewer), "CodeReview");
    }

    [Theory]
    [InlineData("analyze_code_quality")]
    [InlineData("detect_vulnerabilities")]
    [InlineData("suggest_improvements")]
    [InlineData("generate_tests")]
    [InlineData("explain_code")]
    [InlineData("review_pull_request")]
    [InlineData("analyze_repository")]
    public void Exposes_Expected_Function(string functionName)
    {
        BuildPlugin().TryGetFunction(functionName, out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("analyze_code_quality")]
    [InlineData("detect_vulnerabilities")]
    [InlineData("suggest_improvements")]
    [InlineData("generate_tests")]
    [InlineData("explain_code")]
    [InlineData("review_pull_request")]
    [InlineData("analyze_repository")]
    public void Function_Has_NonEmpty_Description(string functionName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        function!.Metadata.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("analyze_code_quality", "code")]
    [InlineData("detect_vulnerabilities", "code")]
    [InlineData("suggest_improvements", "code")]
    [InlineData("generate_tests", "code")]
    [InlineData("explain_code", "code")]
    [InlineData("review_pull_request", "diff")]
    [InlineData("analyze_repository", "repositoryUrl")]
    public void Key_Parameter_Has_Description(string functionName, string parameterName)
    {
        var plugin = BuildPlugin();
        plugin.TryGetFunction(functionName, out var function).ShouldBeTrue();

        var parameter = function!.Metadata.Parameters
            .SingleOrDefault(p => p.Name == parameterName);

        parameter.ShouldNotBeNull();
        parameter!.Description.ShouldNotBeNullOrWhiteSpace();
    }
}
