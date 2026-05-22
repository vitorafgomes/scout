using Scout.Core.Chat;

namespace Scout.Core.Tests.Chat;

public class CitationTests
{
    [Fact]
    public void Ctor_RejectsEmptyLabel()
    {
        Action create = () => new Citation(string.Empty);

        create.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void Ctor_RejectsNullLabel()
    {
        Action create = () => new Citation(null!);

        create.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void ToString_UsesPathWithLine_WhenBothPresent()
    {
        var citation = new Citation("fallback")
        {
            Path = "src/Foo.cs",
            Line = 42,
        };

        citation.ToString().ShouldBe("src/Foo.cs:42");
    }

    [Fact]
    public void ToString_UsesPathOnly_WhenLineMissing()
    {
        var citation = new Citation("fallback")
        {
            Path = "src/Foo.cs",
        };

        citation.ToString().ShouldBe("src/Foo.cs");
    }

    [Fact]
    public void ToString_FallsBackToLabel_WhenPathMissing()
    {
        var citation = new Citation("just a label");

        citation.ToString().ShouldBe("just a label");
    }

    [Fact]
    public void Record_ValueEquality_OnAllFields()
    {
        var a = new Citation("L")
        {
            Path = "p.cs",
            Line = 10,
            Url = "https://example.com",
            Snippet = "snippet",
        };
        var b = new Citation("L")
        {
            Path = "p.cs",
            Line = 10,
            Url = "https://example.com",
            Snippet = "snippet",
        };
        var different = new Citation("L")
        {
            Path = "p.cs",
            Line = 11,
            Url = "https://example.com",
            Snippet = "snippet",
        };

        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
        a.ShouldNotBe(different);
    }
}
