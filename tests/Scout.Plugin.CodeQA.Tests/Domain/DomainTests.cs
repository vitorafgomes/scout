using Scout.Plugin.CodeQA.Domain;

namespace Scout.Plugin.CodeQA.Tests.Domain;

public class CodeChunkTests
{
    private static readonly ReadOnlyMemory<float> SampleEmbedding = new float[] { 0.1f, 0.2f, 0.3f };

    [Fact]
    public void Ctor_Happy_Path()
    {
        var chunk = new CodeChunk(
            Id: "id-1", FilePath: "src/Foo.cs", StartLine: 10, EndLine: 20,
            Content: "public class Foo {}", Embedding: SampleEmbedding);

        chunk.Id.ShouldBe("id-1");
        chunk.FilePath.ShouldBe("src/Foo.cs");
        chunk.StartLine.ShouldBe(10);
        chunk.EndLine.ShouldBe(20);
        chunk.Content.ShouldBe("public class Foo {}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_Rejects_Empty_Id(string id)
    {
        Action act = () => new CodeChunk(id, "p", 1, 1, "c", SampleEmbedding);
        act.ShouldThrow<ArgumentException>().ParamName.ShouldBe(nameof(CodeChunk.Id));
    }

    [Fact]
    public void Ctor_Rejects_StartLine_Below_One()
    {
        Action act = () => new CodeChunk("id", "p", 0, 10, "c", SampleEmbedding);
        act.ShouldThrow<ArgumentOutOfRangeException>()
            .ParamName.ShouldBe(nameof(CodeChunk.StartLine));
    }

    [Fact]
    public void Ctor_Rejects_EndLine_Before_StartLine()
    {
        Action act = () => new CodeChunk("id", "p", 10, 5, "c", SampleEmbedding);
        act.ShouldThrow<ArgumentOutOfRangeException>()
            .ParamName.ShouldBe(nameof(CodeChunk.EndLine));
    }

    [Fact]
    public void Ctor_Rejects_Empty_Content()
    {
        Action act = () => new CodeChunk("id", "p", 1, 1, "", SampleEmbedding);
        act.ShouldThrow<ArgumentException>().ParamName.ShouldBe(nameof(CodeChunk.Content));
    }
}

public class CitationTests
{
    [Fact]
    public void Happy_Path_And_ToString_Formats_As_File_Colon_Range()
    {
        var cit = new Citation("src/Foo.cs", 10, 20, 0.85);

        cit.ToString().ShouldBe("src/Foo.cs:10-20");
        cit.RelevanceScore.ShouldBe(0.85);
    }

    [Theory]
    [InlineData(-1.01)]
    [InlineData(1.01)]
    public void Rejects_Score_Outside_Cosine_Range(double score)
    {
        Action act = () => new Citation("p", 1, 1, score);
        act.ShouldThrow<ArgumentOutOfRangeException>()
            .ParamName.ShouldBe(nameof(Citation.RelevanceScore));
    }
}

public class CodeAnswerTests
{
    [Fact]
    public void Happy_Path()
    {
        var answer = new CodeAnswer(
            "Checkout lives in CheckoutService.cs",
            [new Citation("src/Checkout/CheckoutService.cs", 10, 40, 0.9)],
            "llama3.1",
            TimeSpan.FromMilliseconds(250));

        answer.Citations.Count().ShouldBe(1);
        answer.ModelUsed.ShouldBe("llama3.1");
    }

    [Fact]
    public void Rejects_Empty_Text()
    {
        Action act = () => new CodeAnswer("", [], "m", TimeSpan.Zero);
        act.ShouldThrow<ArgumentException>().ParamName.ShouldBe(nameof(CodeAnswer.Text));
    }

    [Fact]
    public void Rejects_Null_Citations()
    {
        Action act = () => new CodeAnswer("t", null!, "m", TimeSpan.Zero);
        act.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void Rejects_Negative_Latency()
    {
        Action act = () => new CodeAnswer("t", [], "m", TimeSpan.FromMilliseconds(-1));
        act.ShouldThrow<ArgumentOutOfRangeException>()
            .ParamName.ShouldBe(nameof(CodeAnswer.Latency));
    }
}
