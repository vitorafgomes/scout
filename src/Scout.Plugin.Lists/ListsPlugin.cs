using System.ComponentModel;
using System.Text.Json;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common.Abstractions;
using Scout.Plugin.Lists.Abstractions;

namespace Scout.Plugin.Lists;

/// <summary>
/// Descriptive-write plugin for named everyday lists (shopping, packing, ...)
/// exposed to the LLM as Semantic Kernel functions. All state lives in the
/// Scout-local store under the "lists" collection: nothing external is ever
/// contacted or mutated. Every call returns a non-empty, user-safe string;
/// storage faults are caught and surfaced as a clean error line so a tool call
/// never throws back into the kernel, and invalid input returns a corrective
/// hint rather than an error.
/// </summary>
public sealed class ListsPlugin
{
    private const string Collection = "lists";
    private const int MaxHintItems = 3;

    private readonly IScoutLocalStore _store;

    /// <summary>Creates the plugin over the Scout-local store.</summary>
    /// <param name="store">The local store. Must be non-null.</param>
    public ListsPlugin(IScoutLocalStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>Add an item to a named list, creating the list when missing.</summary>
    /// <param name="listName">Target list name.</param>
    /// <param name="item">Item to append.</param>
    /// <param name="ct">Cancels the underlying store write.</param>
    /// <returns>A confirmation line, a corrective hint, or a clean error line.</returns>
    [KernelFunction("add_to_list")]
    [Description("Add an item to a named list (for example a shopping or packing list). Creates the list if it does not exist yet.")]
    public async Task<string> AddToListAsync(
        [Description("Name of the list, e.g. shopping.")] string listName,
        [Description("Item to add, e.g. milk.")] string item,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(listName))
        {
            return "Provide a list name to add to, for example: add milk to shopping.";
        }

        if (string.IsNullOrWhiteSpace(item))
        {
            return $"Provide an item to add to {listName.Trim()}, for example: add milk to {listName.Trim()}.";
        }

        try
        {
            var name = listName.Trim();
            var value = item.Trim();
            var count = 0;

            await _store.MutateAsync<ListsDocument>(Collection, doc =>
            {
                if (!doc.Lists.TryGetValue(name, out var items))
                {
                    items = [];
                    doc.Lists[name] = items;
                }

                items.Add(value);
                count = items.Count;
            }, ct).ConfigureAwait(false);

            return $"Added {value} to {name} ({count} items).";
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Show the items of a list, or an overview of every list.</summary>
    /// <param name="listName">List to show; empty lists every list with counts.</param>
    /// <param name="ct">Cancels the underlying store read.</param>
    /// <returns>Items or an overview, an empty-state sentence, or a clean error line.</returns>
    [KernelFunction("show_list")]
    [Description("Show the items of a named list, one per line. Leave the list name empty to list every existing list with its item count.")]
    public async Task<string> ShowListAsync(
        [Description("Name of the list to show, e.g. shopping. Leave empty to list all lists.")] string listName = "",
        CancellationToken ct = default)
    {
        try
        {
            var doc = await _store.ReadOrCreateAsync<ListsDocument>(Collection, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(listName))
            {
                return doc.Lists.Count == 0
                    ? "No lists yet."
                    : string.Join("\n", doc.Lists
                        .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(kvp => $"{kvp.Key} ({kvp.Value.Count} items)"));
            }

            var name = listName.Trim();
            return doc.Lists.TryGetValue(name, out var items) && items.Count > 0
                ? string.Join("\n", items)
                : $"List {name} is empty.";
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Remove an item from a list, matched leniently.</summary>
    /// <param name="listName">List to remove from.</param>
    /// <param name="item">Item to remove; matched case-insensitively then by substring.</param>
    /// <param name="ct">Cancels the underlying store write.</param>
    /// <returns>A confirmation line, a "did you mean" hint, an empty-state sentence, or a clean error line.</returns>
    [KernelFunction("remove_from_list")]
    [Description("Remove an item from a named list. Matching is case-insensitive with a substring fallback; when nothing matches, the closest existing items are suggested.")]
    public async Task<string> RemoveFromListAsync(
        [Description("Name of the list, e.g. shopping.")] string listName,
        [Description("Item to remove, e.g. milk.")] string item,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(listName))
        {
            return "Provide the list name to remove from, for example: remove milk from shopping.";
        }

        if (string.IsNullOrWhiteSpace(item))
        {
            return $"Provide the item to remove from {listName.Trim()}, for example: remove milk from {listName.Trim()}.";
        }

        try
        {
            var name = listName.Trim();
            var query = item.Trim();
            string? removed = null;
            var remaining = 0;
            var listHadItems = false;
            var candidates = new List<string>();

            await _store.MutateAsync<ListsDocument>(Collection, doc =>
            {
                if (!doc.Lists.TryGetValue(name, out var items) || items.Count == 0)
                {
                    return;
                }

                listHadItems = true;
                var index = FindMatchIndex(items, query);
                if (index >= 0)
                {
                    removed = items[index];
                    items.RemoveAt(index);
                    remaining = items.Count;
                }
                else
                {
                    candidates = [.. items];
                }
            }, ct).ConfigureAwait(false);

            if (removed is not null)
            {
                return $"Removed {removed} from {name} ({remaining} items).";
            }

            if (!listHadItems)
            {
                return $"List {name} is empty.";
            }

            var hints = ClosestItems(candidates, query);
            return $"No item matching \"{query}\" in {name}. Did you mean: {string.Join(", ", hints)}?";
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Remove every item from a list.</summary>
    /// <param name="listName">List to clear.</param>
    /// <param name="ct">Cancels the underlying store write.</param>
    /// <returns>A confirmation line, a corrective hint, or a clean error line.</returns>
    [KernelFunction("clear_list")]
    [Description("Remove every item from a named list, leaving it empty.")]
    public async Task<string> ClearListAsync(
        [Description("Name of the list to clear, e.g. shopping.")] string listName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(listName))
        {
            return "Provide the list name to clear, for example: clear shopping.";
        }

        try
        {
            var name = listName.Trim();
            var removedCount = 0;

            await _store.MutateAsync<ListsDocument>(Collection, doc =>
            {
                if (doc.Lists.TryGetValue(name, out var items))
                {
                    removedCount = items.Count;
                    doc.Lists.Remove(name);
                }
            }, ct).ConfigureAwait(false);

            return $"Cleared {name} ({removedCount} items removed).";
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    // Exact case-insensitive match first, then the first item that contains the
    // query as a substring (also case-insensitive).
    private static int FindMatchIndex(List<string> items, string query)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i], query, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    // Rank existing items by edit distance to the query and return the closest
    // few, breaking ties by name for deterministic output.
    private static IReadOnlyList<string> ClosestItems(List<string> items, string query)
        => items
            .OrderBy(x => EditDistance(x, query))
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Take(MaxHintItems)
            .ToList();

    // Case-insensitive Levenshtein distance; small inputs so the simple
    // two-row dynamic-programming form is more than fast enough.
    private static int EditDistance(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static bool IsHandledFailure(Exception ex)
        => ex is IOException or OperationCanceledException or JsonException;

    private static string ErrorLine(Exception ex) => ex switch
    {
        OperationCanceledException => "The lists operation timed out. Try again.",
        _ => "Could not access local lists storage. Try again.",
    };
}
