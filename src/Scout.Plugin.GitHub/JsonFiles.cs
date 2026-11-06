using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scout.Plugin.GitHub;

/// <summary>
/// One JSON document per file, for the drafts and the profiles Scout keeps under its data folder: written through a
/// uniquely named temporary file and a rename, so a reader never sees half a document and two writers never share a
/// temporary file; read tolerantly, a missing or unreadable file being no document.
/// </summary>
internal static class JsonFiles
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task SaveAsync<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options, ct).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, Options, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
