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
