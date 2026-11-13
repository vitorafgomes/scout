using Scout.Conversation.Abstractions;

namespace Scout.Conversation.Tests.Abstractions;

public sealed class CitationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ctor_Throws_When_Label_Null_Or_Whitespace(string? label)
    {
        Should.Throw<ArgumentException>(() => new Citation(label!))
            .Message.ShouldContain("Label");
    }

    [Fact]
    public void ToString_Uses_Path_And_Line_When_Present()
    {
        var citation = new Citation("label") { Path = "src/A.cs", Line = 42 };

        citation.ToString().ShouldBe("src/A.cs:42");
    }

    [Fact]
    public void ToString_Uses_Path_Without_Line()
    {
        var citation = new Citation("label") { Path = "src/A.cs" };

        citation.ToString().ShouldBe("src/A.cs");
    }

    [Fact]
    public void ToString_Falls_Back_To_Label()
    {
        new Citation("just-a-label").ToString().ShouldBe("just-a-label");
    }
}
