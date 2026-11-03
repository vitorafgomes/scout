using Microsoft.Extensions.Logging;
using Scout.Conversation.Abstractions;

namespace Scout.Operator;

/// <summary>
/// Runs one project from research to a script ready for the owner's review: researching, then scripting, then
/// <c>script_ready</c>, or <c>failed</c> with a sentence saying why, saving through the control plane after
/// each stage. Every save starts from the project as the control plane holds it, so a claim the owner decided
/// goes back exactly as the owner left it. When the project came from a chat, it posts there that research
/// started and that the script is ready, or the failure, even when the failure cannot be saved; it posts
/// nothing else. A failure that cannot be saved is thrown as an <see cref="InvalidOperationException"/>
/// carrying the original reason, so a later save records the real cause, never the save's own failure. A topic
/// the mandate blocks fails the project before any search.
/// </summary>
internal sealed class OperatorPipeline(
    IOperatorControlPlane controlPlane,
    TopicResearcher researcher,
    ScriptWriter writer,
    IOriginNotifier notifier,
    ILogger<OperatorPipeline> logger)
{
    internal const string Researching = "researching";
    internal const string Scripting = "scripting";
    internal const string ScriptReady = "script_ready";
    internal const string Failed = "failed";
    internal const string Pending = "pending";

    internal static string ResearchingNotice(string topic) => $"Researching {topic}...";

    internal static string ReadyNotice(string topic) =>
        $"Script ready for {topic}: open Scout Desktop, Production, to review it.";

    internal static string FailedNotice(string topic, string error) =>
        $"The video project on {topic} stopped: {error}";

    internal static string Reason(Exception ex) =>
        ex is InvalidOperationException ? ex.Message : $"Scout hit an unexpected error ({ex.GetType().Name}).";

    public async Task RunAsync(QueuedProject queued, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queued);
        var id = queued.Project.Id;
        var topic = queued.Project.Topic;
        await NotifyAsync(queued.OriginReference, ResearchingNotice(topic), ct).ConfigureAwait(false);

        try
        {
            var mandate = await controlPlane.GetMandateAsync(ct).ConfigureAwait(false);
            if (TopicResearcher.BlockedTopic(mandate, topic) is { } blocked)
            {
                throw new InvalidOperationException(TopicResearcher.Blocked(blocked));
            }

            var research = await researcher.ResearchAsync(topic, mandate, ct).ConfigureAwait(false);
            await SaveAsync(id, current => current with { Status = Scripting, Candidates = research.Candidates, Error = null }, ct)
                .ConfigureAwait(false);
            var draft = await writer.WriteAsync(topic, mandate, research, ct).ConfigureAwait(false);
            await SaveAsync(
                id,
                current => current with
                {
                    Status = ScriptReady,
                    Script = draft.Script,
                    Claims = WithOwnerDecisions(current.Claims, draft.Claims),
                    Error = null,
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var reason = Reason(ex);
            logger.LogWarning("Video project {ProjectId} failed ({ExceptionType}).", id, ex.GetType().Name);
            try
            {
                await SaveFailedAsync(id, reason, ct).ConfigureAwait(false);
            }
            catch (Exception saveFailure) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException(reason, saveFailure);
            }
            finally
            {
                await NotifyAsync(queued.OriginReference, FailedNotice(topic, reason), ct).ConfigureAwait(false);
            }

            return;
        }

        await NotifyAsync(queued.OriginReference, ReadyNotice(topic), ct).ConfigureAwait(false);
    }

    /// <summary>Save the project as failed with <paramref name="error"/>, starting from the control plane's copy.</summary>
    public Task SaveFailedAsync(string id, string error, CancellationToken ct)
        => SaveAsync(id, current => current with { Status = Failed, Error = error }, ct);

    private async Task SaveAsync(string id, Func<OperatorProjectState, OperatorProjectState> change, CancellationToken ct)
    {
        var current = await controlPlane.GetProjectAsync(id, ct).ConfigureAwait(false);
        await controlPlane.SaveProjectAsync(change(current), null, ct).ConfigureAwait(false);
    }

    private static List<OperatorClaim> WithOwnerDecisions(IReadOnlyList<OperatorClaim> stored, IReadOnlyList<OperatorClaim> drafted)
    {
        var decided = stored.Where(c => c.Status != Pending).ToList();
        var usedIds = decided.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var next = 0;
        var fresh = drafted
            .Where(c => !decided.Any(d => string.Equals(d.Text, c.Text, StringComparison.Ordinal)))
            .Select(c =>
            {
                string claimId;
                do
                {
                    claimId = $"c{++next}";
                }
                while (usedIds.Contains(claimId));

                return c with { Id = claimId };
            });
        return [.. decided, .. fresh];
    }

    private async Task NotifyAsync(string? originReference, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(originReference))
        {
            return;
        }

        try
        {
            if (!await notifier.PostAsync(originReference, text, ct).ConfigureAwait(false))
            {
                logger.LogInformation("The progress notice could not be posted to the originating conversation.");
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Posting the progress notice failed ({ExceptionType}).", ex.GetType().Name);
        }
    }
}
