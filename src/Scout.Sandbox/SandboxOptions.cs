namespace Scout.Sandbox;

/// <summary>Where the runs live, which <c>docker</c> to call, and whether the restore may use the network.</summary>
public sealed class SandboxOptions
{
    /// <summary>The folder under Scout's data folder that holds each run's work folder and its log.</summary>
    public string WorkDirectory { get; set; } = DefaultWorkDirectory();

    /// <summary>The <c>docker</c> command line, by name on the PATH or by full path.</summary>
    public string DockerPath { get; set; } = "docker";

    /// <summary>False runs the restore with <c>--network none</c> too, so only an already restored repository runs.</summary>
    public bool RestoreNetwork { get; set; } = true;

    internal static string DefaultWorkDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scout", "sandbox");
}
