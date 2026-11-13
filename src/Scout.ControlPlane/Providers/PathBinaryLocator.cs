namespace Scout.ControlPlane.Providers;

/// <summary>
/// PATH-only locator: splits the <c>PATH</c> environment variable by the
/// platform separator and returns the first existing file matching the
/// requested binary name, on Unix only one with an execute bit set. No
/// platform-specific fallback paths are checked.
/// </summary>
public sealed class PathBinaryLocator : IBinaryLocator
{
    public Task<string?> FindOnPathAsync(string binaryName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryName);
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(Find(Environment.GetEnvironmentVariable("PATH"), binaryName));
    }

    internal static string? Find(string? path, string binaryName)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var executableName = OperatingSystem.IsWindows()
            ? binaryName + ".exe"
            : binaryName;

        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            string candidate;
            try
            {
                candidate = Path.Combine(directory, executableName);
            }
            catch (ArgumentException)
            {
                // Skip malformed PATH entries (e.g. invalid characters).
                continue;
            }

            if (File.Exists(candidate) && IsExecutable(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsExecutable(string file) =>
        OperatingSystem.IsWindows()
        || (File.GetUnixFileMode(file) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
}
