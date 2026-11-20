using System.Text.Json;

namespace Scout.Plugin.GitHub;

/// <summary>
/// What a review job measured, carried with a verdict or a review into its note: the files read of the files changed and
/// the job's own sentences on those not read.
/// </summary>
internal sealed record NoteExtras(int? FilesRead, int? FilesChanged, IReadOnlyList<string>? Notes)
{
    /// <summary>The extras of a queued payload's <c>filesRead</c>, <c>filesChanged</c> and <c>notes</c>; null when it carries none.</summary>
    internal static NoteExtras? Read(JsonElement? filesRead, JsonElement? filesChanged, JsonElement? notes)
    {
        static int? Count(JsonElement? value) =>
            value is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var count) && count >= 0 ? count : null;

        var lines = notes is { ValueKind: JsonValueKind.Array } array
            ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : null;
        var extras = new NoteExtras(Count(filesRead), Count(filesChanged), lines is { Count: > 0 } ? lines : null);
        return extras is { FilesRead: null, FilesChanged: null, Notes: null } ? null : extras;
    }
}
