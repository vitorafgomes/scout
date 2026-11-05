using System.Text;
using System.Text.Json;

namespace Scout.Adapter.Local.Tests;

/// <summary>Collects every line written to the adapter's stdout and lets a test wait for one.</summary>
internal sealed class LineCapture : TextWriter
{
    private readonly StringBuilder _pending = new();
    private readonly List<string> _lines = [];
    private readonly object _gate = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override Encoding Encoding => Encoding.UTF8;

    public string Raw { get; private set; } = string.Empty;

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    public IReadOnlyList<JsonElement> Events
        => Lines.Select(l => JsonDocument.Parse(l["SCOUT_LOCAL ".Length..]).RootElement).ToList();

    public override void Write(char value)
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            Raw += value;
            if (value != '\n')
            {
                _pending.Append(value);
                return;
            }
            _lines.Add(_pending.ToString().TrimEnd('\r'));
            _pending.Clear();
            changed = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        changed.TrySetResult();
    }

    public async Task<JsonElement> WaitForAsync(string? id, string kind, Func<JsonElement, bool>? where = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                changed = _changed.Task;
            }
            var found = Events.FirstOrDefault(
                e => e.GetProperty("event").GetString() == kind && IdOf(e) == id && (where is null || where(e)));
            if (found.ValueKind != JsonValueKind.Undefined)
            {
                return found;
            }
            try
            {
                await changed.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new ShouldAssertException(
                    $"No \"{kind}\" event for id {id ?? "null"} on stdout within 10 s. Stdout was:\n{Raw}");
            }
        }
    }

    public IReadOnlyList<string> KindsFor(string id)
        => Events.Where(e => IdOf(e) == id).Select(e => e.GetProperty("event").GetString()!).ToList();

    public static string? IdOf(JsonElement e)
        => e.GetProperty("id").ValueKind == JsonValueKind.String ? e.GetProperty("id").GetString() : null;
}
