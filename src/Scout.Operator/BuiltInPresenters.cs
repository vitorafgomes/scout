using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Scout.Operator;

/// <summary>A presenter Scout ships: its id, its label, its picture in the presenters folder and the mouth on it.</summary>
internal sealed record BuiltInPresenter(string Id, string Label, string File, OperatorMouthBox MouthBox);

/// <summary>
/// The presenters Scout ships in <see cref="OperatorMediaOptions.PresentersPath"/>, listed in its <see cref="ListFile"/>: a
/// JSON array, or an object whose <c>presenters</c> holds it, of <c>id</c>, <c>label</c>, <c>file</c>, a plain name that
/// defaults to <c>&lt;id&gt;.png</c>, and <c>mouthBox</c> with <c>x</c>, <c>y</c>, <c>width</c> and <c>height</c> as fractions
/// of the picture. An entry with an id that is not lowercase letters, digits and dashes, a file that is not a plain name or
/// is not in the folder, or a mouth box outside the picture is left out. A project gets the same one every time, chosen from
/// a hash of its id; its picture is copied into the media directory as <c>presenter-builtin-&lt;id&gt;.png</c>.
/// </summary>
internal sealed partial class BuiltInPresenters(IOptions<OperatorMediaOptions> options)
{
    internal const string ListFile = "presenters.json";

    internal const string NotFound = "Presenters: the built-in presenters were not found.";

    internal static string FileName(string id) => $"presenter-builtin-{id}.png";

    /// <summary>Whether the desktop named a presenters folder, usable or not.</summary>
    public bool Configured => !string.IsNullOrWhiteSpace(options.Value.PresentersPath);

    /// <summary>The presenters the folder lists and holds, in the order of the list; none when the list cannot be read.</summary>
    public IReadOnlyList<BuiltInPresenter> List()
    {
        var folder = options.Value.PresentersPath.Trim();
        if (folder.Length == 0)
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, ListFile)));
            var entries = document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("presenters", out var listed)
                ? listed
                : document.RootElement;
            return entries.ValueKind != JsonValueKind.Array
                ? []
                : [.. entries.EnumerateArray().Select(e => Read(folder, e)).OfType<BuiltInPresenter>()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>The presenter with <paramref name="id"/> when there is one, else the one a hash of <paramref name="projectId"/> picks; null without any.</summary>
    public BuiltInPresenter? Choose(string projectId, string? id = null)
    {
        var all = List();
        if (all.Count == 0)
        {
            return null;
        }

        if (all.FirstOrDefault(p => p.Id == id) is { } chosen)
        {
            return chosen;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(projectId ?? string.Empty));
        return all[(int)(BinaryPrimitives.ReadUInt32BigEndian(hash) % (uint)all.Count)];
    }

    /// <summary>Copy the picture of <paramref name="presenter"/> into the media directory; its file name there, or null when it cannot be read.</summary>
    public async Task<string?> CopyAsync(BuiltInPresenter presenter, string directory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(Path.Combine(options.Value.PresentersPath.Trim(), presenter.File), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var name = FileName(presenter.Id);
        if (File.Exists(MediaFiles.PathFor(directory, name)) && await SameBytesAsync(MediaFiles.PathFor(directory, name), bytes, ct).ConfigureAwait(false))
        {
            return name;
        }

        await MediaFiles.WriteAsync(directory, name, bytes, ct).ConfigureAwait(false);
        return name;
    }

    private static async Task<bool> SameBytesAsync(string path, byte[] bytes, CancellationToken ct)
    {
        try
        {
            var existing = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            return existing.AsSpan().SequenceEqual(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static BuiltInPresenter? Read(string folder, JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || Text(entry, "id") is not { } id
            || !Id().IsMatch(id)
            || !entry.TryGetProperty("mouthBox", out var box)
            || box.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var file = Text(entry, "file") ?? $"{id}.png";
        var mouth = new OperatorMouthBox(Number(box, "x"), Number(box, "y"), Number(box, "width"), Number(box, "height"));
        var inside = mouth is { X: >= 0, Y: >= 0, Width: >= 0.02, Height: >= 0.02 } && mouth.X + mouth.Width <= 1 && mouth.Y + mouth.Height <= 1;
        return inside && file == Path.GetFileName(file) && !file.StartsWith('.') && File.Exists(Path.Combine(folder, file))
            ? new BuiltInPresenter(id, Text(entry, "label") ?? id, file, mouth)
            : null;
    }

    private static string? Text(JsonElement entry, string name)
        => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static double Number(JsonElement entry, string name)
        => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : -1;

    [GeneratedRegex("^[a-z0-9-]{1,40}$")]
    private static partial Regex Id();
}
