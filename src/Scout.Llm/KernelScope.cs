using Microsoft.SemanticKernel;

namespace Scout.Llm;

/// <summary>
/// The functions one turn may call: a copy of the shared kernel that holds only those functions, and the test whether
/// a function name the model used is one of them.
/// </summary>
public static class KernelScope
{
    /// <summary>
    /// A copy of <paramref name="shared"/> with its services and invocation filters, the write gate among them, holding
    /// only the functions <paramref name="qualified"/> names as <c>Plugin.function</c>. A name the shared kernel does not
    /// hold is skipped, so an empty or unresolved list gives a copy with no function.
    /// </summary>
    /// <param name="shared">The shared kernel every plugin is registered on.</param>
    /// <param name="qualified">The <c>Plugin.function</c> names the turn declares.</param>
    /// <returns>The per-turn kernel.</returns>
    public static Kernel Only(Kernel shared, IEnumerable<string> qualified)
    {
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentNullException.ThrowIfNull(qualified);

        var turn = shared.Clone();
        turn.Plugins.Clear();
        var functions = qualified
            .Select(name => name.Split('.'))
            .Where(parts => parts.Length == 2)
            .Select(parts => shared.Plugins.TryGetFunction(parts[0], parts[1], out var function) ? function : null)
            .OfType<KernelFunction>()
            .DistinctBy(function => (function.PluginName, function.Name))
            .GroupBy(function => function.PluginName!, StringComparer.Ordinal);
        foreach (var plugin in functions)
        {
            turn.Plugins.Add(KernelPluginFactory.CreateFromFunctions(plugin.Key, plugin));
        }

        return turn;
    }

    /// <summary>
    /// Whether <paramref name="called"/>, a function name as the model sent it, names one of the declared functions:
    /// <c>Plugin-function</c>, <c>Plugin_function</c>, <c>Plugin.function</c> or the bare function name, without case.
    /// </summary>
    /// <param name="called">The function name the model called.</param>
    /// <param name="qualified">The <c>Plugin.function</c> names the turn declares.</param>
    /// <returns>True when the name is one of the declared functions.</returns>
    public static bool IsDeclared(string called, IEnumerable<string> qualified)
    {
        ArgumentNullException.ThrowIfNull(called);
        ArgumentNullException.ThrowIfNull(qualified);

        foreach (var name in qualified)
        {
            var parts = name.Split('.');
            if (parts.Length != 2)
            {
                continue;
            }

            if (called.Equals(parts[1], StringComparison.OrdinalIgnoreCase)
                || called.Equals($"{parts[0]}-{parts[1]}", StringComparison.OrdinalIgnoreCase)
                || called.Equals($"{parts[0]}_{parts[1]}", StringComparison.OrdinalIgnoreCase)
                || called.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
