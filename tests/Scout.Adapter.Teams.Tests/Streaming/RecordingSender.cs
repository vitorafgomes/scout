using Microsoft.Bot.Schema;
using Scout.Adapter.Teams.Streaming;

namespace Scout.Adapter.Teams.Tests.Streaming;

/// <summary>
/// Test double for <see cref="ITeamsActivitySender"/> that records every sent
/// activity and lets a test script per-call behaviour (throw, return an id). No
/// network, no Azure.
/// </summary>
internal sealed class RecordingSender : ITeamsActivitySender
{
    private readonly Func<Activity, int, string?>? _onSend;
    private int _calls;

    public RecordingSender(Func<Activity, int, string?>? onSend = null) => _onSend = onSend;

    public List<Activity> Sent { get; } = [];

    public Task<string?> SendAsync(Activity activity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = _calls++;
        // onSend may throw to simulate a transport failure for that call.
        var id = _onSend?.Invoke(activity, index);
        Sent.Add(activity);
        return Task.FromResult(id);
    }

    public static string? StreamType(Activity a) =>
        Entity(a)?.Properties["streamType"]?.ToString();

    public static int? StreamSequence(Activity a)
    {
        var v = Entity(a)?.Properties["streamSequence"];
        return v is null ? null : (int)v;
    }

    public static string? StreamId(Activity a)
    {
        var props = Entity(a)?.Properties;
        if (props is not null && props.TryGetValue("streamId", out var id))
        {
            return id?.ToString();
        }
        return null;
    }

    public static bool HasStreamInfoEntity(Activity a) => Entity(a) is not null;

    private static Entity? Entity(Activity a) =>
        a.Entities?.FirstOrDefault(e => e.Type == "streaminfo");
}
