namespace Scout.ControlPlane.Providers;

/// <summary>
/// PATH-only locator: splits the <c>PATH</c> environment variable by the
/// platform separator and returns the first existing file matching the
/// requested binary name. No platform-specific fallback paths are checked.
/// </summary>
public sealed class PathBinaryLocator : IBinaryLocator
{
    public Task<string?> FindOnPathAsync(string binaryName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryName);
        ct.ThrowIfCancellationRequested();

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return Task.FromResult<string?>(null);
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

            if (File.Exists(candidate))
            {
                return Task.FromResult<string?>(candidate);
            }
        }

        return Task.FromResult<string?>(null);
    }
}
