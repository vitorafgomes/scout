using Grpc.Core;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Contracts;
using Scout.ControlPlane.Controls;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.Operator;
using Scout.ControlPlane.WriteGate;
using GrpcFeatures = Scout.ControlPlane.Contracts.Features;
using WirePendingAction = Scout.ControlPlane.Contracts.PendingAction;

namespace Scout.Host.ControlPlane.Services;

/// <summary>
/// Thin gRPC adapter over <see cref="FeaturesService"/>, the action queue through <see cref="ActionDecisionService"/> and
/// the write log. Deleting the YouTube credentials first purges what Scout read from YouTube through
/// <see cref="OperatorService.PurgeYouTubeDataAsync"/>; a purge that fails keeps the credentials. Revoking the
/// YouTube write grant, disabling YouTube or deleting its credentials first rejects every approved YouTube action through
/// <see cref="OperatorService.CancelApprovedYouTubeActionsAsync"/>. A verdict clicked on a review card is queued already
/// approved through <see cref="PullRequestVerdictService"/>, and read back as its state, never with its payload. Every call that
/// clears a feature's write grant rejects that feature's actions that have not started, waiting for the owner or approved and
/// not yet claimed, with
/// <see cref="WriteGateService.WritesRevoked"/>, each logged: revoking it, disabling the feature and deleting its credentials do it
/// first, so a queue or log that cannot be written fails the call and leaves the grant in place; an enable whose activation fails
/// does it after, and a queue or log that cannot be written then leaves the activation's error as the answer. The owner's
/// approve and reject go through <see cref="ActionDecisionService"/>, which refuses an approval without the grant and logs every
/// decision. The list of actions carries each one's group and repository and, for one waiting for the owner only, the
/// title and body its payload will write, the payload's digest and its group's state. The owner's Approve all and Reject all
/// decide exactly the actions the desktop listed, through <see cref="ActionDecisionService.ApproveSetAsync"/> and
/// <see cref="ActionDecisionService.RejectSetAsync"/>, a set refused whole answering <see cref="StatusCode.FailedPrecondition"/>,
/// and a group's actions in any status are read with their text. A findings card's Open issues answers one row per issue,
/// each with its own group, status and outcome, and as the answer's group the group the click queued its issues in, or,
/// when it queued none, the first row's group. A set call without a group id answers
/// <see cref="StatusCode.InvalidArgument"/>. The write log is listed as <see cref="WriteGateService.AsListed"/> reads it, from
/// twice the entries asked for, so a reversible write and its record list once. Maps domain
/// exceptions to gRPC status codes; validation failures carry the dotted-path
/// error map in the status detail.
/// </summary>
public sealed class FeaturesGrpcService(
    FeaturesService features,
    IWriteLog writes,
    OperatorService @operator,
    PullRequestVerdictService verdicts,
    ActionDecisionService decisions,
    OwnerControlService controls,
    IActionQueue actions,
    CardIssuesService cards) : GrpcFeatures.FeaturesBase
{
    private const int DefaultWritesLimit = 50;
    private const int MaxWritesLimit = 200;

    private readonly FeaturesService _features = features ?? throw new ArgumentNullException(nameof(features));
    private readonly PullRequestVerdictService _verdicts = verdicts ?? throw new ArgumentNullException(nameof(verdicts));
    private readonly IWriteLog _writes = writes ?? throw new ArgumentNullException(nameof(writes));
    private readonly OperatorService _operator = @operator ?? throw new ArgumentNullException(nameof(@operator));
    private readonly ActionDecisionService _decisions = decisions ?? throw new ArgumentNullException(nameof(decisions));
    private readonly OwnerControlService _controls = controls ?? throw new ArgumentNullException(nameof(controls));
    private readonly IActionQueue _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    private readonly CardIssuesService _cards = cards ?? throw new ArgumentNullException(nameof(cards));

    public override async Task<ListFeaturesResponse> ListFeatures(
        ListFeaturesRequest request,
        ServerCallContext context)
    {
        var snapshots = await Guard(() => _features.ListAsync(context.CancellationToken))
            .ConfigureAwait(false);
        var response = new ListFeaturesResponse();
        foreach (var snapshot in snapshots)
        {
            response.Features.Add(FeatureMapping.ToWire(snapshot));
        }
        return response;
    }

    public override Task<Feature> SetCredentials(
        SetCredentialsRequest request,
        ServerCallContext context) =>
        Guard(async () => FeatureMapping.ToWire(
            await _features.SetCredentialsAsync(
                request.Id,
                request.Credentials,
                context.CancellationToken).ConfigureAwait(false)));

    public override Task<Feature> Setup(SetupRequest request, ServerCallContext context) =>
        Guard(async () =>
        {
            var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (key, value) in request.Settings)
            {
                settings[key] = value;
            }
            foreach (var cleared in request.ClearedSettings)
            {
                settings[cleared] = null;
            }

            var input = new FeatureSetupInput(
                Credentials: request.Credentials.Count == 0 ? null : request.Credentials,
                Settings: settings.Count == 0 ? null : settings);

            return FeatureMapping.ToWire(
                await _features.SetupAsync(request.Id, input, context.CancellationToken)
                    .ConfigureAwait(false));
        });

    public override Task<Feature> DeleteCredentials(
        DeleteCredentialsRequest request,
        ServerCallContext context) =>
        Guard(async () =>
        {
            if (request.Id == FeatureCatalog.YouTubeId)
            {
                await _operator.PurgeYouTubeDataAsync(context.CancellationToken).ConfigureAwait(false);
                await _operator.CancelApprovedYouTubeActionsAsync(context.CancellationToken).ConfigureAwait(false);
            }

            await RejectWaitingActionsAsync(request.Id, context.CancellationToken).ConfigureAwait(false);
            return FeatureMapping.ToWire(
                await _features.DeleteCredentialsAsync(request.Id, context.CancellationToken).ConfigureAwait(false));
        });

    public override Task<Feature> Enable(EnableRequest request, ServerCallContext context) =>
        Guard(async () =>
        {
            try
            {
                return FeatureMapping.ToWire(
                    await _features.EnableAsync(request.Id, request.ConfirmWrites, context.CancellationToken)
                        .ConfigureAwait(false));
            }
            catch (FeatureActivationException)
            {
                try
                {
                    await RejectWaitingActionsAsync(request.Id, context.CancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                }

                throw;
            }
        });

    public override Task<Feature> Disable(DisableRequest request, ServerCallContext context) =>
        Guard(async () =>
        {
            if (request.Id == FeatureCatalog.YouTubeId)
            {
                await _operator.CancelApprovedYouTubeActionsAsync(context.CancellationToken).ConfigureAwait(false);
            }

            await RejectWaitingActionsAsync(request.Id, context.CancellationToken).ConfigureAwait(false);
            return FeatureMapping.ToWire(
                await _features.DisableAsync(request.Id, context.CancellationToken).ConfigureAwait(false));
        });

    public override Task<Feature> SetWriteGrant(SetWriteGrantRequest request, ServerCallContext context) =>
        Guard(async () =>
        {
            if (!request.Granted && request.Id == FeatureCatalog.YouTubeId)
            {
                await _operator.CancelApprovedYouTubeActionsAsync(context.CancellationToken).ConfigureAwait(false);
            }

            if (!request.Granted)
            {
                await RejectWaitingActionsAsync(request.Id, context.CancellationToken).ConfigureAwait(false);
            }

            return FeatureMapping.ToWire(
                await _features.SetWriteGrantAsync(
                    request.Id,
                    request.Granted,
                    request.ConfirmWrites,
                    context.CancellationToken).ConfigureAwait(false));
        });

    public override Task<ListPendingActionsResponse> ListPendingActions(
        ListPendingActionsRequest request,
        ServerCallContext context) =>
        Guard(async () =>
        {
            var response = new ListPendingActionsResponse();
            response.Actions.Add(FeatureMapping.ToWire(await _decisions.ListPendingAsync(context.CancellationToken).ConfigureAwait(false)));
            return response;
        });

    public override Task<WirePendingAction> ApproveAction(
        ActionDecisionRequest request,
        ServerCallContext context) =>
        Guard(async () => FeatureMapping.ToWire(
            await _decisions.ApproveAsync(
                request.Id,
                request.HasReason ? request.Reason : null,
                context.CancellationToken).ConfigureAwait(false)));

    public override Task<WirePendingAction> RejectAction(
        ActionDecisionRequest request,
        ServerCallContext context) =>
        Guard(async () => FeatureMapping.ToWire(
            await _decisions.RejectAsync(
                request.Id,
                request.HasReason ? request.Reason : null,
                context.CancellationToken).ConfigureAwait(false)));

    public override Task<ActionSetResponse> ApproveSet(ActionSetRequest request, ServerCallContext context) =>
        Guard(async () => FeatureMapping.ToWire(
            GroupIdOf(request.GroupId),
            await _decisions.ApproveSetAsync(
                request.GroupId,
                request.Actions.Select(a => (a.Id, a.Digest)).ToList(),
                request.HasReason ? request.Reason : null,
                context.CancellationToken).ConfigureAwait(false)));

    public override Task<ActionSetResponse> RejectSet(ActionSetRequest request, ServerCallContext context) =>
        Guard(async () => FeatureMapping.ToWire(
            GroupIdOf(request.GroupId),
            await _decisions.RejectSetAsync(
                request.GroupId,
                request.Actions.Select(a => a.Id).ToList(),
                request.HasReason ? request.Reason : null,
                context.CancellationToken).ConfigureAwait(false)));

    public override Task<GetActionGroupResponse> GetActionGroup(GetActionGroupRequest request, ServerCallContext context) =>
        Guard(async () =>
        {
            var response = new GetActionGroupResponse { GroupId = GroupIdOf(request.GroupId) };
            var group = await _decisions.GetGroupAsync(request.GroupId, context.CancellationToken).ConfigureAwait(false);
            response.Actions.Add(FeatureMapping.ToWire(group.Select(g => (g.Action, (string?)g.GroupState)), anyStatus: true));
            return response;
        });

    public override Task<ActionSetResponse> ApproveCardIssues(ApproveCardIssuesRequest request, ServerCallContext context) =>
        Guard(async () =>
        {
            var decision = await _cards.ApproveAsync(
                    request.CardId, request.Repository, request.Issues.Select(i => (i.Title, i.Body)).ToList(), context.CancellationToken)
                .ConfigureAwait(false);
            return FeatureMapping.ToWire(decision.GroupId!, decision);
        });

    public override Task<GetActionTextResponse> GetActionText(GetActionTextRequest request, ServerCallContext context) =>
        Guard(async () =>
        {
            var action = (string.IsNullOrWhiteSpace(request.Id) ? null : await _actions.FindAsync(request.Id, context.CancellationToken).ConfigureAwait(false))
                ?? throw new PendingActionNotFoundException(request.Id);
            var response = new GetActionTextResponse { CutAfterExecution = action.Status == PendingActionStatus.Executed };
            var (title, body) = action.FullText();
            if (title is not null)
            {
                response.Title = title;
            }
            if (body is not null)
            {
                response.Body = body;
            }
            if (!response.CutAfterExecution)
            {
                response.PayloadDigest = action.PayloadDigest();
            }
            return response;
        });

    private static string GroupIdOf(string groupId) =>
        string.IsNullOrWhiteSpace(groupId)
            ? throw new RpcException(new Status(StatusCode.InvalidArgument, "A group is named by its id; none was given."))
            : groupId;

    public override Task<ListWritesResponse> ListWrites(ListWritesRequest request, ServerCallContext context) =>
        Guard(async () =>
        {
            var limit = request.Limit > 0 ? Math.Min(request.Limit, MaxWritesLimit) : DefaultWritesLimit;
            var response = new ListWritesResponse();
            var read = await _writes.ListAsync(limit * 2, context.CancellationToken).ConfigureAwait(false);
            foreach (var entry in WriteGateService.AsListed(read, DateTimeOffset.UtcNow).Take(limit))
            {
                response.Entries.Add(FeatureMapping.ToWire(entry));
            }
            return response;
        });

    public override Task<ControlJobResponse> ControlJob(ControlJobRequest request, ServerCallContext context) =>
        Guard(async () =>
        {
            var answer = await _controls.RequestAsync(
                    request.Kind, request.Owner, request.Repo, request.HasNumber ? request.Number : null, request.Action, context.CancellationToken)
                .ConfigureAwait(false);
            var response = new ControlJobResponse { Accepted = answer.Accepted, Reason = answer.Reason };
            if (answer.ControlId is not null)
            {
                response.ControlId = answer.ControlId;
            }

            return response;
        });

    public override Task<GiveVerdictResponse> GiveVerdict(GiveVerdictRequest request, ServerCallContext context) =>
        Guard(async () => new GiveVerdictResponse
        {
            ActionId = (await _verdicts.GiveAsync(
                    PullRequestVerdictRequest.FromCard(
                        request.Repository, request.Number, request.Verdict, request.Body, request.CommentsJson, request.Commit, request.TestsLine),
                    context.CancellationToken)
                .ConfigureAwait(false)).Id,
        });

    public override Task<GetVerdictResponse> GetVerdict(GetVerdictRequest request, ServerCallContext context) =>
        Guard(async () =>
        {
            var state = await _verdicts.ReadAsync(request.ActionId, context.CancellationToken).ConfigureAwait(false)
                ?? throw new PendingActionNotFoundException(request.ActionId);
            return new GetVerdictResponse
            {
                State = state.State,
                Url = state.Url,
                Reason = state.Reason,
                Repository = state.Repository,
                Number = state.Number,
                Commit = state.Commit,
                Verdict = state.Verdict,
            };
        });

    public override Task<GetVerdictForResponse> GetVerdictFor(GetVerdictForRequest request, ServerCallContext context) =>
        Guard(async () =>
        {
            var found = await _verdicts.FindAsync(request.Repository, request.Number, request.Commit, context.CancellationToken)
                .ConfigureAwait(false);
            return found is not { } verdict
                ? new GetVerdictForResponse { Found = false }
                : new GetVerdictForResponse
                {
                    Found = true,
                    ActionId = verdict.ActionId,
                    State = verdict.State.State,
                    Url = verdict.State.Url,
                    Reason = verdict.State.Reason,
                    Verdict = verdict.State.Verdict,
                };
        });

    private Task<IReadOnlyList<Scout.ControlPlane.Actions.PendingAction>> RejectWaitingActionsAsync(string featureId, CancellationToken ct) =>
        _decisions.RejectUnstartedAsync(featureId, ct);

    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (FeatureUnknownException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
        catch (FeatureValidationFailedException ex)
        {
            var detail = string.Join("; ", ex.Errors.Select(e => $"{e.Key}: {e.Value}"));
            throw new RpcException(new Status(StatusCode.InvalidArgument, detail));
        }
        catch (FeatureCredentialsMissingException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
        catch (WriteConfirmationRequiredException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
        catch (PendingActionNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
        catch (PendingActionAlreadyDecidedException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
        catch (WriteNotGrantedException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
        catch (ActionSetRefusedException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
        catch (FeatureNotImplementedException ex)
        {
            throw new RpcException(new Status(StatusCode.Unimplemented, ex.Message));
        }
        catch (FeatureActivationException ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
        catch (FeaturePersistFailedException ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }
}
