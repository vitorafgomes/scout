using System.Runtime.CompilerServices;

namespace Scout.Llm.Abstractions;

/// <summary>
/// Whether a read the model asked for handed back the content it was asked for. A caller that counts what was read (the
/// audit's files read) begins a scope around one function call on the current async flow, and the read function marks
/// it when its answer is not the content: a file it did not find, a directory, a binary, a refused name or an error
/// line. The answer the model receives is the same either way; outside a scope a mark is dropped.
/// </summary>
public static class ReadOutcome
{
    private static readonly AsyncLocal<StrongBox<bool>?> Slot = new();

    /// <summary>
    /// Watch the call made on the current async flow until the scope is disposed, which restores whatever was watched
    /// before, so a call made from inside another leaves the outer one intact. Every scope starts unmarked.
    /// </summary>
    public static Scope Begin()
    {
        var previous = Slot.Value;
        var mark = new StrongBox<bool>();
        Slot.Value = mark;
        return new Scope(previous, mark);
    }

    /// <summary>Say that the answer of the current call is not the content asked for; nothing happens outside a scope.</summary>
    public static void MarkNoContent()
    {
        if (Slot.Value is { } mark)
        {
            mark.Value = true;
        }
    }

    /// <summary>One watched call; restores the previous one on dispose.</summary>
    public readonly struct Scope(StrongBox<bool>? previous, StrongBox<bool> mark) : IDisposable
    {
        /// <summary>True once the call said its answer is not the content asked for.</summary>
        public bool NoContent => mark.Value;

        /// <inheritdoc />
        public void Dispose() => Slot.Value = previous;
    }
}
