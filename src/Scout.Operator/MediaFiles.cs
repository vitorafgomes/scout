namespace Scout.Operator;

/// <summary>
/// Writes a project's media files only inside the media directory the control plane gives: the directory must be an
/// absolute path, the name a plain file name without a leading dot, and the resolved path must be a file inside the
/// directory, never the directory itself, or the write is refused. A write goes to a new temporary file in the same
/// directory and replaces the target in one rename.
/// </summary>
internal static class MediaFiles
{
    internal const string Outside = "Scout refused to write a file outside the project's media folder.";

    internal static string CouldNotSave(string fileName) => $"Scout could not save {fileName} in the project's media folder.";

    public static async Task WriteAsync(string directory, string fileName, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var path = PathFor(directory, fileName);
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{fileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }

            if (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(CouldNotSave(fileName), ex);
            }

            throw;
        }
    }

    /// <summary>Whether <paramref name="fileName"/> is a file inside <paramref name="directory"/>; false for a name that would leave it.</summary>
    public static bool Exists(string directory, string fileName)
    {
        try
        {
            return File.Exists(PathFor(directory, fileName));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static string PathFor(string directory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(directory)
            || !Path.IsPathFullyQualified(directory)
            || string.IsNullOrEmpty(fileName)
            || fileName.StartsWith('.')
            || fileName.Contains("..", StringComparison.Ordinal)
            || fileName.Contains('\\', StringComparison.Ordinal)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException(Outside);
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, fileName));
        if (!path.StartsWith(root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(Outside);
        }

        return path;
    }

    /// <summary>
    /// Have <paramref name="render"/> write <paramref name="fileName"/> to the temporary path it is given, a hidden file in
    /// the same directory with the same extension, and move it over the target only when it is there and holds at least
    /// 1 KB; a smaller file fails with <paramref name="failure"/>, the step's sentence. On any failure the temporary file
    /// is deleted and the target is left as it was.
    /// </summary>
    public static async Task RenderAsync(string directory, string fileName, string failure, Func<string, Task> render, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(render);
        var path = PathFor(directory, fileName);
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Guid.NewGuid():N}.{fileName}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await render(temp).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(temp))
            {
                throw new InvalidOperationException(CouldNotSave(fileName));
            }

            if (new FileInfo(temp).Length < 1024)
            {
                throw new InvalidOperationException($"{failure} (the file came out empty)");
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            if (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(CouldNotSave(fileName), ex);
            }

            throw;
        }
    }

    /// <summary>Delete <paramref name="fileName"/> from the media directory when it is there; a name that would leave it, or a delete that fails, is ignored.</summary>
    public static void Delete(string directory, string fileName)
    {
        try
        {
            TryDelete(PathFor(directory, fileName));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
