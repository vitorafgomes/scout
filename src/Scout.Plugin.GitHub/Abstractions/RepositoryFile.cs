namespace Scout.Plugin.GitHub.Abstractions;

/// <summary>A path read from a repository: the decoded text of a file, the entries of a directory, or a symlink or submodule GitHub names as such.</summary>
/// <param name="Path">The repository-relative path that was read.</param>
/// <param name="Kind">"file", "dir", "symlink" or "submodule", as GitHub types the path.</param>
/// <param name="Text">The decoded file text, or null for anything but a file GitHub returned inline.</param>
/// <param name="Size">The file size in bytes GitHub reported; 0 for a directory.</param>
/// <param name="Entries">Directory entries as "name (type)"; empty unless the path is a directory.</param>
public sealed record RepositoryFile(
    string Path,
    string Kind,
    string? Text,
    long Size,
    IReadOnlyList<string> Entries)
{
    /// <summary>True when the path is a directory and <see cref="Entries"/> lists it.</summary>
    public bool IsDirectory => Kind == "dir";
}
