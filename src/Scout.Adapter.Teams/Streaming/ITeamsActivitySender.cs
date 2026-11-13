using Microsoft.Bot.Schema;

namespace Scout.Adapter.Teams.Streaming;

/// <summary>
/// Transport seam for the Teams sink. Sends one outbound <see cref="Activity"/>
/// to the originating conversation and returns the platform resource id the
/// response carried (the stream id on the first typing activity, the message id
/// on the final activity), or null when the platform returned none.
///
/// Production binds this to a Bot Framework <c>ITurnContext</c> /
/// <c>CloudAdapter.ContinueConversationAsync</c>; tests bind a delegate so the
/// sink lifecycle, cumulative-text invariant, and error mapping are exercised
/// without Azure or a network.
/// </summary>
public interface ITeamsActivitySender
{
    Task<string?> SendAsync(Activity activity, CancellationToken cancellationToken);
}

/// <summary>
/// Delegate-backed <see cref="ITeamsActivitySender"/>. Lets the adapter (and
/// tests) supply the send as a lambda without a bespoke class per call site.
/// </summary>
public sealed class DelegateTeamsActivitySender(
    Func<Activity, CancellationToken, Task<string?>> send) : ITeamsActivitySender
{
    private readonly Func<Activity, CancellationToken, Task<string?>> _send =
        send ?? throw new ArgumentNullException(nameof(send));

    public Task<string?> SendAsync(Activity activity, CancellationToken cancellationToken)
        => _send(activity, cancellationToken);
}
