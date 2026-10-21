namespace Scout.Plugin.CodeQA.Indexing;

/// <summary>
/// One source file pulled from a remote host, ready to be chunked and
/// embedded.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Path"/> is repo-relative with forward slashes so downstream
/// chunkers, citations, and the vector store all key on the same string
/// across platforms. <see cref="Sha"/> is the Git blob SHA and gives us a
/// stable identity for future incremental-ingestion caches.
/// <see cref="SizeBytes"/> is the UTF-8 byte length of
/// <see cref="Content"/>; empty content is allowed (empty file), but
/// <c>null</c> content is not.
/// </para>
/// </remarks>
public sealed record RawFile
{
    public RawFile(string Path, string Sha, int SizeBytes, string Content)
    {
        if (string.IsNullOrWhiteSpace(Path))
        {
            throw new ArgumentException("Path must be non-empty.", nameof(Path));
        }
        if (string.IsNullOrWhiteSpace(Sha))
        {
            throw new ArgumentException("Sha must be non-empty.", nameof(Sha));
        }
        if (SizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SizeBytes), "SizeBytes must be >= 0.");
        }
        ArgumentNullException.ThrowIfNull(Content);

        this.Path = Path;
        this.Sha = Sha;
        this.SizeBytes = SizeBytes;
        this.Content = Content;
    }

    public string Path { get; }
    public string Sha { get; }
    public int SizeBytes { get; }
    public string Content { get; }
}
