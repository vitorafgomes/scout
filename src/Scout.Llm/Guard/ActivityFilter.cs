using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.SemanticKernel;
using Scout.Llm.Abstractions;

namespace Scout.Llm.Guard;

/// <summary>
/// Tells the turn's chat which function the model is about to call, in words: the function name as a sentence and
/// the short values of the arguments on an allow-list of names that identify a thing (an owner, a path, a query, a
/// namespace), never a body, a label, a token or any name the list does not know, so a credential handed to a
/// function cannot reach the chat or the worker's stdout. A report that fails never stops the function. Runs before
/// <see cref="WriteGateFilter"/>, so a queued or refused write is still announced as an attempt.
/// </summary>
internal sealed class ActivityFilter : IFunctionInvocationFilter
{
    internal const int MaxArgumentLength = 80;
    internal const int MaxLength = 160;

    /// <summary>The only argument names whose values are shown; everything else is named by the function alone.</summary>
    internal static readonly HashSet<string> Shown = new(StringComparer.OrdinalIgnoreCase)
    {
        "owner", "repo", "number", "path", "reference", "query", "prefix", "extension", "extensions",
        "project", "symbol", "name", "namespace", "context", "kind", "state", "id", "file", "language", "limit", "page", "since",
    };

    public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        try
        {
            await TurnProgress.ReportAsync(Describe(context.Function.Name, context.Arguments), context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!context.CancellationToken.IsCancellationRequested)
        {
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// "get_pull_request_file" with owner, repo, 42 and a path becomes "Get pull request file: octocat hello 42
    /// src/Checkout.cs". The connectors hand the arguments as CLR values or as <see cref="JsonElement"/>; both read.
    /// </summary>
    internal static string Describe(string functionName, KernelArguments? arguments)
    {
        var words = functionName.Replace('_', ' ').Trim();
        var sentence = words.Length == 0 ? "Working" : char.ToUpperInvariant(words[0]) + words[1..];
        var detail = new StringBuilder();
        foreach (var (name, value) in arguments ?? [])
        {
            if (!Shown.Contains(name) || value is null or bool)
            {
                continue;
            }

            var text = value switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String or JsonValueKind.Number } element => element.ToString(),
                JsonElement => null,
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxArgumentLength || text.Contains('\n', StringComparison.Ordinal))
            {
                continue;
            }

            detail.Append(detail.Length == 0 ? ": " : " ").Append(text.Trim());
        }

        var line = sentence + detail;
        return line.Length <= MaxLength ? line : line[..MaxLength];
    }
}
