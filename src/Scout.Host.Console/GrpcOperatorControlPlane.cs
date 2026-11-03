using Grpc.Core;
using Scout.ControlPlane.Contracts;
using Scout.Operator;

namespace Scout.Host.Console;

/// <summary>
/// <see cref="IOperatorControlPlane"/> over the control plane's <c>WorkerGate</c> service, on the channel, the
/// worker token and the deadline of the <see cref="GrpcWriteGate"/> it is given. A refusal the control plane
/// words (no grant, a value it does not accept, a change only the owner may make, an unknown project) keeps its
/// words; any other failure reads "Scout could not reach its control plane."
/// </summary>
internal sealed class GrpcOperatorControlPlane(GrpcWriteGate gate) : IOperatorControlPlane
{
    internal const string Unreachable = "Scout could not reach its control plane.";

    public async Task<OperatorMandate> GetMandateAsync(CancellationToken ct)
    {
        var mandate = await CallAsync(
            () => gate.Client.GetOperatorMandateAsync(new GetMandateRequest(), gate.CallOptions(ct)).ResponseAsync, ct)
            .ConfigureAwait(false);
        return new OperatorMandate(
            mandate.Objective,
            mandate.Audience,
            [.. mandate.Pillars],
            mandate.Cadence,
            mandate.Language,
            mandate.VoiceAndStyle,
            [.. mandate.BlockedTopics],
            mandate.TargetMetric,
            mandate.TargetValue,
            mandate.HasUpdatedAt ? mandate.UpdatedAt : null);
    }

    public async Task<OperatorProjectState> SaveProjectAsync(
        OperatorProjectState project, string? originReference, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var request = new SaveOperatorProjectRequest { Project = ToWire(project) };
        if (originReference is not null)
        {
            request.OriginReference = originReference;
        }

        var saved = await CallAsync(
            () => gate.Client.SaveOperatorProjectAsync(request, gate.CallOptions(ct)).ResponseAsync, ct)
            .ConfigureAwait(false);
        return FromWire(saved);
    }

    public async Task<QueuedProject?> NextQueuedAsync(CancellationToken ct)
    {
        var next = await CallAsync(
            () => gate.Client.NextQueuedProjectAsync(new NextQueuedProjectRequest(), gate.CallOptions(ct)).ResponseAsync, ct)
            .ConfigureAwait(false);
        return next.Project is null
            ? null
            : new QueuedProject(FromWire(next.Project), next.HasOriginReference ? next.OriginReference : null);
    }

    public async Task<OperatorProjectState> GetProjectAsync(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var project = await CallAsync(
            () => gate.Client.GetOperatorProjectAsync(new GetProjectRequest { Id = id }, gate.CallOptions(ct)).ResponseAsync, ct)
            .ConfigureAwait(false);
        return FromWire(project);
    }

    public async Task<IReadOnlyList<OperatorProjectState>> ListRecentAsync(int limit, CancellationToken ct)
    {
        var list = await CallAsync(
            () => gate.Client.ListOperatorProjectsAsync(new ListProjectsRequest { Limit = limit }, gate.CallOptions(ct)).ResponseAsync, ct)
            .ConfigureAwait(false);
        return [.. list.Projects.Select(FromWire)];
    }

    private static async Task<T> CallAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (RpcException ex) when (!ct.IsCancellationRequested)
        {
            var worded = ex.StatusCode is StatusCode.PermissionDenied or StatusCode.FailedPrecondition
                or StatusCode.InvalidArgument or StatusCode.NotFound;
            throw new InvalidOperationException(
                worded && !string.IsNullOrWhiteSpace(ex.Status.Detail) ? ex.Status.Detail : Unreachable, ex);
        }
    }

    private static Project ToWire(OperatorProjectState project)
    {
        var wire = new Project
        {
            Id = project.Id,
            Topic = project.Topic,
            Status = project.Status,
            Origin = project.Origin,
            CreatedAt = project.CreatedAt,
            UpdatedAt = project.UpdatedAt,
        };
        wire.Candidates.AddRange(project.Candidates.Select(c => new TopicCandidate { Title = c.Title, Reason = c.Reason, Fit = c.Fit }));
        if (project.Script is { } script)
        {
            var wireScript = new Script { Hook = script.Hook, CallToAction = script.CallToAction, Description = script.Description };
            wireScript.Sections.AddRange(script.Sections.Select(s => new ScriptSection { Heading = s.Heading, Narration = s.Narration }));
            wireScript.TitleVariants.AddRange(script.TitleVariants);
            wireScript.Tags.AddRange(script.Tags);
            wireScript.Chapters.AddRange(script.Chapters.Select(c => new Chapter { At = c.At, Title = c.Title }));
            wire.Script = wireScript;
        }

        wire.Claims.AddRange(project.Claims.Select(c =>
        {
            var claim = new Claim { Id = c.Id, Text = c.Text, Risk = c.Risk, Status = c.Status };
            claim.SourceUrls.AddRange(c.SourceUrls);
            if (c.Note is not null)
            {
                claim.Note = c.Note;
            }

            return claim;
        }));
        if (project.Error is not null)
        {
            wire.Error = project.Error;
        }

        return wire;
    }

    private static OperatorProjectState FromWire(Project wire)
        => new(
            wire.Id,
            wire.Topic,
            wire.Status,
            wire.Origin,
            wire.CreatedAt,
            wire.UpdatedAt,
            [.. wire.Candidates.Select(c => new OperatorTopicCandidate(c.Title, c.Reason, c.Fit))],
            wire.Script is null
                ? null
                : new OperatorScript(
                    wire.Script.Hook,
                    [.. wire.Script.Sections.Select(s => new OperatorScriptSection(s.Heading, s.Narration))],
                    wire.Script.CallToAction,
                    [.. wire.Script.TitleVariants],
                    wire.Script.Description,
                    [.. wire.Script.Tags],
                    [.. wire.Script.Chapters.Select(c => new OperatorChapter(c.At, c.Title))]),
            [.. wire.Claims.Select(c => new OperatorClaim(c.Id, c.Text, c.Risk, [.. c.SourceUrls], c.Status, c.HasNote ? c.Note : null))],
            wire.HasError ? wire.Error : null);
}
