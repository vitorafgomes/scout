namespace Scout.Plugin.Common;

/// <summary>Options for <see cref="JsonFileScoutLocalStore"/>.</summary>
public sealed class ScoutLocalStoreOptions
{
    /// <summary>
    /// Directory that holds the per-collection JSON files. When null the store
    /// defaults to <c>{LocalApplicationData}/Scout/plugin-data</c>. Tests point
    /// this at a temporary directory.
    /// </summary>
    public string? RootDirectory { get; set; }
}
