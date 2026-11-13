namespace Scout.Knowledge.Tests;

public sealed class FileChunkerTests
{
    [Fact]
    public void CSharp_file_chunks_each_method_and_type()
    {
        var src = "public class Foo {\n  public int Bar() { return 1; }\n}\n";
        var chunks = new FileChunker().Chunk("Foo.cs", src);
        chunks.ShouldContain(c => c.Symbol == "Foo" && c.Kind == "class");
        chunks.ShouldContain(c => c.Symbol == "Bar" && c.Kind == "method");
    }

    [Theory]
    [InlineData("public struct Money { }", "Money", "struct", "public struct Money { }")]
    [InlineData("public enum Colour { Red }", "Colour", "enum", "public enum Colour { Red }")]
    [InlineData("public class Basket { public Basket(int count) { } }", "Basket", "constructor", "public Basket(int count) { }")]
    [InlineData("public class Basket { public int Total { get; set; } }", "Total", "property", "public int Total { get; set; }")]
    [InlineData("public class Basket { public event System.EventHandler Filled { add { } remove { } } }", "Filled", "event", "public event System.EventHandler Filled { add { } remove { } }")]
    [InlineData("public class Basket { public event System.EventHandler Emptied; }", "Emptied", "event", "public event System.EventHandler Emptied;")]
    [InlineData("public delegate int Combine(int left, int right);", "Combine", "delegate", "public delegate int Combine(int left, int right);")]
    [InlineData("public class Basket { private readonly int _count = 1; }", "_count", "field", "private readonly int _count = 1;")]
    [InlineData("public class Basket { private const int Limit = 3; }", "Limit", "field", "private const int Limit = 3;")]
    public void CSharp_file_chunks_every_member_kind_by_its_name(string source, string symbol, string kind, string text)
    {
        var chunks = new FileChunker().Chunk("Basket.cs", "namespace Shop;\n\n" + source + "\n");

        var chunk = chunks.Where(c => c.Kind == kind).ShouldHaveSingleItem();
        chunk.Symbol.ShouldBe(symbol);
        chunk.Text.ShouldBe(text);
        chunk.StartLine.ShouldBe(3);
        chunk.EndLine.ShouldBe(3);
    }

    [Fact]
    public void A_field_declaration_of_two_names_yields_one_chunk_per_name_each_with_the_whole_declaration()
    {
        var chunks = new FileChunker().Chunk("Point.cs", "public class Point\n{\n    private int _x, _y;\n}\n");

        var fields = chunks.Where(c => c.Kind == "field").ToList();
        fields.Select(c => c.Symbol).ShouldBe(["_x", "_y"]);
        fields.ShouldAllBe(c => c.Text == "private int _x, _y;" && c.StartLine == 3 && c.EndLine == 3);
    }

    [Fact]
    public void Local_variables_and_parameters_are_not_chunks()
    {
        var src = "public class Sum\n{\n    public int Of(int left)\n    {\n        int local = left;\n        return local;\n    }\n}\n";

        var chunks = new FileChunker().Chunk("Sum.cs", src);

        chunks.Select(c => (c.Symbol, c.Kind)).ShouldBe([("Sum", "class"), ("Of", "method")]);
    }

    [Fact]
    public void Non_csharp_file_falls_back_to_line_windows()
    {
        var text = string.Join("\n", Enumerable.Range(1, 250).Select(i => $"line {i}"));
        var chunks = new FileChunker().Chunk("notes.md", text);
        chunks.Count.ShouldBeGreaterThan(1);              // 100-line windows -> 3 chunks
        chunks[0].Kind.ShouldBe("lines");
        chunks[0].StartLine.ShouldBe(1);
    }

    [Fact]
    public void CSharp_file_without_member_declarations_falls_back_to_line_windows()
    {
        var src = "// just a comment, no declarations\n";
        var chunks = new FileChunker().Chunk("Comment.cs", src);
        chunks.ShouldNotBeEmpty();
        chunks.ShouldAllBe(c => c.Kind == "lines");
        chunks[0].StartLine.ShouldBe(1);
    }
}
