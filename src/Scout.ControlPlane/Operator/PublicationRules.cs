using System.Text.Json;

namespace Scout.ControlPlane.Operator;

/// <summary>
/// The publication vocabulary and what the worker may change in a project's publication: the status after
/// <c>publish_approved</c>, the video id while it is unset, the upload time, the error, snapshots by window, new
/// proposals, and the timing, metrics, winner, result and end of a running experiment.
/// </summary>
internal static class PublicationRules
{
    public const string PublishApproved = "publish_approved";
    public const string Uploading = "uploading";
    public const string Failed = "failed";
    public const string Scheduled = "scheduled";
    public const string Published = "published";
    public const string Private = "private";
    public const string Proposed = "proposed";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string Tested = "tested";
    public const string Title = "title";
    public const string Thumbnail = "thumbnail";
    public const string Running = "running";
    public const string Concluded = "concluded";
    public const string Inconclusive = "inconclusive";
    public const string Adopted = "adopted";
    public const string Control = "control";

    public static readonly string[] PrivacyStatuses = ["public", Private, "unlisted"];

    private static readonly string[] WorkerStatuses = [Uploading, "uploaded", Scheduled, Published, Failed];
    private static readonly string[] ProposalKinds = [Title, Thumbnail, "topic", "strategy"];
    private static readonly string[] ProposalStatuses = [Proposed, Accepted, Rejected, Tested];
    private static readonly string[] ExperimentEnds = [Concluded, Inconclusive];

    /// <summary>
    /// The publication after a worker save: the stored one when the worker sends none, else the worker's with the
    /// stored snapshots it did not replace and the stored experiment with what the worker may set in it.
    /// </summary>
    /// <exception cref="OperatorValidationException">A status, proposal or winner outside the vocabulary.</exception>
    /// <exception cref="OperatorTamperingException">The worker changes what only the owner sets.</exception>
    public static Publication? MergeFromWorker(string projectId, Publication? stored, Publication? incoming)
    {
        if (incoming is null)
        {
            return stored;
        }
        if (stored is null)
        {
            throw new OperatorTamperingException($"Only the owner publishes project '{projectId}'.");
        }

        Validate(incoming);
        if (!string.Equals(incoming.PrivacyStatus, stored.PrivacyStatus, StringComparison.Ordinal)
            || incoming.PublishAt != stored.PublishAt
            || incoming.MadeForKids != stored.MadeForKids
            || incoming.SyntheticMedia != stored.SyntheticMedia)
        {
            throw new OperatorTamperingException(
                $"Only the owner sets the privacy, the publish time and the declarations of project '{projectId}'.");
        }
        if (!WorkerStatuses.Contains(incoming.Status, StringComparer.Ordinal)
            && !string.Equals(incoming.Status, stored.Status, StringComparison.Ordinal))
        {
            throw new OperatorTamperingException($"Only the owner sets the publication of project '{projectId}' to {incoming.Status}.");
        }
        if (stored.VideoId is not null && !string.Equals(incoming.VideoId, stored.VideoId, StringComparison.Ordinal))
        {
            throw new OperatorTamperingException($"Project '{projectId}' is on YouTube as '{stored.VideoId}'; the worker may not change its video id.");
        }

        RefuseProposalDecisions(stored.Proposals, incoming.Proposals);
        var windows = incoming.Snapshots.Select(s => s.Window).ToHashSet(StringComparer.Ordinal);
        return incoming with
        {
            Snapshots = [.. stored.Snapshots.Where(s => !windows.Contains(s.Window)), .. incoming.Snapshots],
            Experiment = MergeExperiment(projectId, stored.Experiment, incoming.Experiment),
        };
    }

    private static void Validate(Publication publication)
    {
        if (!WorkerStatuses.Contains(publication.Status, StringComparer.Ordinal) && publication.Status != PublishApproved)
        {
            throw new OperatorValidationException(
                $"Publication status '{publication.Status}' is not one the operator accepts: {PublishApproved}, {string.Join(", ", WorkerStatuses)}.");
        }
        if (publication.Snapshots.Any(s => string.IsNullOrWhiteSpace(s.Window))
            || publication.Snapshots.Select(s => s.Window).Distinct(StringComparer.Ordinal).Count() != publication.Snapshots.Count)
        {
            throw new OperatorValidationException("Every snapshot needs a window of its own.");
        }
        if (publication.Proposals.Any(p => string.IsNullOrWhiteSpace(p.Id))
            || publication.Proposals.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != publication.Proposals.Count)
        {
            throw new OperatorValidationException("Every proposal needs an id of its own.");
        }

        foreach (var proposal in publication.Proposals)
        {
            if (!ProposalKinds.Contains(proposal.Kind, StringComparer.Ordinal)
                || !ProposalStatuses.Contains(proposal.Status, StringComparer.Ordinal))
            {
                throw new OperatorValidationException(
                    $"Proposal '{proposal.Id}' is a {string.Join(", ", ProposalKinds)} proposal, {string.Join(", ", ProposalStatuses)}.");
            }
            if (proposal.Kind == Thumbnail || proposal.ThumbnailFile is not null)
            {
                SceneRules.ValidateFileName(proposal.ThumbnailFile, SceneRules.Thumbnail);
            }
        }
    }

    private static void RefuseProposalDecisions(IReadOnlyList<Proposal> stored, IReadOnlyList<Proposal> incoming)
    {
        foreach (var decided in stored.Where(p => p.Status != Proposed))
        {
            if (!incoming.Contains(decided))
            {
                throw new OperatorTamperingException(
                    $"Proposal '{decided.Id}' carries the owner's decision; the worker may not change or drop it.");
            }
        }

        var undecided = incoming.FirstOrDefault(p =>
            p.Status != Proposed && !stored.Any(s => s.Id == p.Id && s.Status == p.Status));
        if (undecided is not null)
        {
            throw new OperatorTamperingException($"Only the owner marks a proposal {undecided.Status}; proposal '{undecided.Id}' was not.");
        }
    }

    private static Experiment? MergeExperiment(string projectId, Experiment? stored, Experiment? incoming)
    {
        if (incoming is null)
        {
            return stored;
        }
        if (stored is null
            || incoming.Id != stored.Id
            || incoming.Kind != stored.Kind
            || !incoming.Arms.Select(a => (a.Id, a.Label, a.Value)).SequenceEqual(stored.Arms.Select(a => (a.Id, a.Label, a.Value))))
        {
            throw new OperatorTamperingException($"Only the owner starts an experiment on project '{projectId}' and chooses its arms.");
        }

        var merged = stored with
        {
            Status = incoming.Status,
            Arms = incoming.Arms,
            WinnerArmId = incoming.WinnerArmId,
            Result = incoming.Result,
        };
        if (stored.Status != Running)
        {
            return Json(merged) == Json(stored)
                ? stored
                : throw new OperatorTamperingException($"The experiment of project '{projectId}' is over; the worker may not change it.");
        }
        if (incoming.Status != Running && !ExperimentEnds.Contains(incoming.Status, StringComparer.Ordinal))
        {
            throw new OperatorTamperingException(
                $"A running experiment ends {string.Join(" or ", ExperimentEnds)}; project '{projectId}' sent {incoming.Status}.");
        }
        if (incoming.WinnerArmId is not null && !stored.Arms.Any(a => a.Id == incoming.WinnerArmId))
        {
            throw new OperatorValidationException($"The winner '{incoming.WinnerArmId}' is not an arm of the experiment.");
        }

        return merged;
    }

    private static string Json(Experiment experiment) => JsonSerializer.Serialize(experiment);
}
