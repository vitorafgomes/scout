namespace Scout.Plugin.CodeReview;

/// <summary>
/// One entry in a repository's recursive Git tree. Only blob entries carry a
/// meaningful <see cref="Size"/>; tree entries describe directories.
/// </summary>
/// <param name="Path">Repository-relative path, e.g. "src/Program.cs".</param>
/// <param name="Type">Git object type, "blob" for files and "tree" for directories.</param>
/// <param name="Size">Blob size in bytes; zero for non-blob entries.</param>
/// <param name="Sha">Git object SHA.</param>
internal sealed record TreeEntry(string Path, string Type, long Size, string Sha);
