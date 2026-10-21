using Scout.Plugin.CodeQA.Composing;
using Scout.Plugin.CodeQA.Domain;
using Scout.Plugin.CodeQA.Storage;

namespace Scout.Plugin.CodeQA.Tests.Composing;

public class CodeQAPromptTemplateTests
{
    private static VectorSearchResult MakeHit(
        string path = "src/A.cs",
        int start = 1,
        int end = 10,
        string content = "var x = 1;",
        double score = 0.9)
    {
        var embedding = new ReadOnlyMemory<float>(new[] { 0.1f, 0.2f });
        var chunk = new CodeChunk("id-" + path, path, start, end, content, embedding);
        return new VectorSearchResult(chunk, score);
    }

    [Fact]
    public void SystemPrompt_Mentions_Untrusted_Context()
    {
        var sys = CodeQAPromptTemplate.SystemPrompt;

        sys.ShouldNotBeNullOrWhiteSpace();
        sys.ShouldContain("UNTRUSTED", Case.Sensitive);
        sys.ShouldContain("<context>");
    }

    [Fact]
    public void SystemPrompt_Mentions_Insufficient_Context_Escape_Hatch()
    {
        CodeQAPromptTemplate.SystemPrompt.ShouldContain("I don't have enough context.");
    }

    [Fact]
    public void BuildUserPrompt_Wraps_Each_Chunk_With_Path_And_Lines()
    {
        var hits = new[]
        {
            MakeHit("src/A.cs", 1, 5, "class A {}"),
            MakeHit("src/B.cs", 20, 25, "class B {}"),
        };

        var prompt = CodeQAPromptTemplate.BuildUserPrompt("What is A?", hits);

        prompt.ShouldContain("<chunk path=\"src/A.cs\" lines=\"1-5\">");
        prompt.ShouldContain("class A {}");
        prompt.ShouldContain("<chunk path=\"src/B.cs\" lines=\"20-25\">");
        prompt.ShouldContain("class B {}");
        prompt.ShouldContain("</context>");
        prompt.ShouldEndWith("QUESTION: What is A?");
    }

    [Fact]
    public void BuildUserPrompt_Preserves_Question_Text_Verbatim()
    {
        var hits = new[] { MakeHit() };

        var prompt = CodeQAPromptTemplate.BuildUserPrompt("Explain TopKRetriever briefly.", hits);

        prompt.ShouldContain("QUESTION: Explain TopKRetriever briefly.");
    }

    [Fact]
    public void BuildUserPrompt_Emits_Empty_Context_Block_When_No_Hits()
    {
        var prompt = CodeQAPromptTemplate.BuildUserPrompt("Anything?", Array.Empty<VectorSearchResult>());

        prompt.ShouldContain("<context></context>");
        prompt.ShouldContain("QUESTION: Anything?");
    }

    [Fact]
    public void BuildUserPrompt_Throws_On_Empty_Question()
    {
        Should.Throw<ArgumentException>(() =>
            CodeQAPromptTemplate.BuildUserPrompt(" ", Array.Empty<VectorSearchResult>()));
    }

    [Fact]
    public void BuildUserPrompt_Throws_On_Null_Context()
    {
        Should.Throw<ArgumentNullException>(() =>
            CodeQAPromptTemplate.BuildUserPrompt("Hi", null!));
    }
}
