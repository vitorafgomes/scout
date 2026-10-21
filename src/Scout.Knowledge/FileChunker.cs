using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Scout.Knowledge;

/// <summary>Roslyn AST chunker for C#; 100-line window fallback otherwise.</summary>
public sealed class FileChunker : IChunker
{
    private const int LineWindow = 100;

    /// <inheritdoc />
    public IReadOnlyList<Chunk> Chunk(string path, string text)
        => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? ChunkCSharp(text) : ChunkLines(text);

    private static IReadOnlyList<Chunk> ChunkCSharp(string text)
    {
        var root = CSharpSyntaxTree.ParseText(text).GetRoot();
        var chunks = new List<Chunk>();
        foreach (var node in root.DescendantNodes())
        {
            (string name, string kind)? meta = node switch
            {
                MethodDeclarationSyntax m => (m.Identifier.Text, "method"),
                ClassDeclarationSyntax c => (c.Identifier.Text, "class"),
                RecordDeclarationSyntax r => (r.Identifier.Text, "record"),
                InterfaceDeclarationSyntax i => (i.Identifier.Text, "interface"),
                _ => null
            };
            if (meta is null)
            {
                continue;
            }

            var span = node.GetLocation().GetLineSpan();
            chunks.Add(new Chunk(meta.Value.name, meta.Value.kind,
                span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1, node.ToString()));
        }

        // Comment-only files, top-level statements, etc. yield no member declarations:
        // fall back to line windows so a file never produces zero chunks.
        return chunks.Count > 0 ? chunks : ChunkLines(text);
    }

    private static IReadOnlyList<Chunk> ChunkLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var chunks = new List<Chunk>();
        for (int i = 0; i < lines.Length; i += LineWindow)
        {
            int end = Math.Min(i + LineWindow, lines.Length);
            chunks.Add(new Chunk(string.Empty, "lines", i + 1, end, string.Join("\n", lines[i..end])));
        }

        return chunks;
    }
}
