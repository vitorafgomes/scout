using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Scout.Knowledge;

/// <summary>
/// Roslyn AST chunker for C#: one chunk per type (class, record, interface, struct, enum, delegate) and per member
/// (method, constructor, property, event, field), a field or field-like event of several names yielding one chunk
/// per name with the whole declaration; 100-line window fallback otherwise.
/// </summary>
public sealed class FileChunker : IChunker
{
    /// <summary>The kind of a field's chunk: found by name, never embedded.</summary>
    internal const string FieldKind = "field";

    private const int LineWindow = 100;

    /// <inheritdoc />
    public IReadOnlyList<Chunk> Chunk(string path, string text)
        => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? ChunkCSharp(text) : ChunkLines(text);

    private static List<Chunk> ChunkCSharp(string text)
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
                StructDeclarationSyntax s => (s.Identifier.Text, "struct"),
                EnumDeclarationSyntax e => (e.Identifier.Text, "enum"),
                DelegateDeclarationSyntax d => (d.Identifier.Text, "delegate"),
                ConstructorDeclarationSyntax c => (c.Identifier.Text, "constructor"),
                PropertyDeclarationSyntax p => (p.Identifier.Text, "property"),
                EventDeclarationSyntax e => (e.Identifier.Text, "event"),
                VariableDeclaratorSyntax { Parent.Parent: EventFieldDeclarationSyntax } v => (v.Identifier.Text, "event"),
                VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax } v => (v.Identifier.Text, FieldKind),
                _ => null
            };
            if (meta is null)
            {
                continue;
            }

            var declaration = node is VariableDeclaratorSyntax ? node.Parent!.Parent! : node;
            var span = declaration.GetLocation().GetLineSpan();
            chunks.Add(new Chunk(meta.Value.name, meta.Value.kind,
                span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1, declaration.ToString()));
        }

        // Comment-only files, top-level statements, etc. yield no member declarations:
        // fall back to line windows so a file never produces zero chunks.
        return chunks.Count > 0 ? chunks : ChunkLines(text);
    }

    private static List<Chunk> ChunkLines(string text)
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
