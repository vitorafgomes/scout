using System.Text.Json.Serialization;

namespace Scout.Plugin.Lists.Abstractions;

/// <summary>
/// Persistence contract for the "lists" collection in the Scout-local store: a
/// map of list name to its ordered items. Names are compared case-insensitively
/// so "Shopping" and "shopping" address the same list.
/// </summary>
// Populate (not replace) properties on deserialization so the read-only
// dictionary below is filled in place, preserving its case-insensitive comparer
// across a save/load round-trip. Without this the serializer would skip the
// read-only property and every reload would start from an empty document.
[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public sealed class ListsDocument
{
    /// <summary>
    /// Named lists keyed case-insensitively. Read-only and pre-built with
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>; the type-level populate
    /// handling above keeps that comparer intact after a round-trip (a settable
    /// property would be replaced with a case-sensitive dictionary).
    /// </summary>
    public Dictionary<string, List<string>> Lists { get; } =
        new(StringComparer.OrdinalIgnoreCase);
}
