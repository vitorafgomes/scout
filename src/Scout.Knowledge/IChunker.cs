namespace Scout.Knowledge;

/// <summary>Splits a single file's text into chunks.</summary>
public interface IChunker
{
    /// <summary>Chunk a file. C# uses AST; everything else uses fixed line windows.</summary>
    IReadOnlyList<Chunk> Chunk(string path, string text);
}
