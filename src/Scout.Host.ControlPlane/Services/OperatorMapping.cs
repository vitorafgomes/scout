using System.Globalization;
using Grpc.Core;
using Scout.ControlPlane.Operator;
using DomainChapter = Scout.ControlPlane.Operator.Chapter;
using DomainClaim = Scout.ControlPlane.Operator.Claim;
using DomainMandate = Scout.ControlPlane.Operator.Mandate;
using DomainScript = Scout.ControlPlane.Operator.Script;
using DomainScriptSection = Scout.ControlPlane.Operator.ScriptSection;
using DomainTopicCandidate = Scout.ControlPlane.Operator.TopicCandidate;
using WireChapter = Scout.ControlPlane.Contracts.Chapter;
using WireClaim = Scout.ControlPlane.Contracts.Claim;
using WireMandate = Scout.ControlPlane.Contracts.Mandate;
using WireProject = Scout.ControlPlane.Contracts.Project;
using WireScript = Scout.ControlPlane.Contracts.Script;
using WireScriptSection = Scout.ControlPlane.Contracts.ScriptSection;
using WireTopicCandidate = Scout.ControlPlane.Contracts.TopicCandidate;

namespace Scout.Host.ControlPlane.Services;

/// <summary>
/// Maps the operator's mandate and projects to and from their wire messages, and its exceptions
/// to gRPC status codes. A project's origin reference never goes into a <c>Project</c> message.
/// </summary>
internal static class OperatorMapping
{
    public static WireMandate ToWire(DomainMandate mandate)
    {
        var wire = new WireMandate
        {
            Objective = mandate.Objective,
            Audience = mandate.Audience,
            Cadence = mandate.Cadence,
            Language = mandate.Language,
            VoiceAndStyle = mandate.VoiceAndStyle,
            TargetMetric = mandate.TargetMetric,
            TargetValue = mandate.TargetValue,
        };
        wire.Pillars.AddRange(mandate.Pillars);
        wire.BlockedTopics.AddRange(mandate.BlockedTopics);
        if (mandate.UpdatedAt is { } updatedAt)
        {
            wire.UpdatedAt = Iso(updatedAt);
        }
        return wire;
    }

    public static DomainMandate ToDomain(WireMandate wire) => new(
        wire.Objective,
        wire.Audience,
        wire.Pillars.ToList(),
        wire.Cadence,
        wire.Language,
        wire.VoiceAndStyle,
        wire.BlockedTopics.ToList(),
        wire.TargetMetric,
        wire.TargetValue);

    public static WireProject ToWire(OperatorProject project)
    {
        var wire = new WireProject
        {
            Id = project.Id,
            Topic = project.Topic,
            Status = project.Status,
            Origin = project.Origin,
            CreatedAt = Iso(project.CreatedAt),
            UpdatedAt = Iso(project.UpdatedAt),
        };
        wire.Candidates.AddRange(project.Candidates.Select(c => new WireTopicCandidate
        {
            Title = c.Title,
            Reason = c.Reason,
            Fit = c.Fit,
        }));
        if (project.Script is { } script)
        {
            wire.Script = ToWire(script);
        }
        wire.Claims.AddRange(project.Claims.Select(ToWire));
        if (project.Error is not null)
        {
            wire.Error = project.Error;
        }
        return wire;
    }

    public static OperatorProject ToDomain(WireProject wire) => new(
        wire.Id,
        wire.Topic,
        wire.Status,
        wire.Origin,
        default,
        default,
        wire.Candidates.Select(c => new DomainTopicCandidate(c.Title, c.Reason, c.Fit)).ToList(),
        wire.Script is null ? null : ToDomain(wire.Script),
        wire.Claims.Select(c => new DomainClaim(
            c.Id,
            c.Text,
            c.Risk,
            c.SourceUrls.ToList(),
            c.Status,
            c.HasNote ? c.Note : null)).ToList(),
        wire.HasError ? wire.Error : null);

    public static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperatorValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (OperatorNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
        catch (OperatorWriteRefusedException ex)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
        }
        catch (OperatorTamperingException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
    }

    private static WireScript ToWire(DomainScript script)
    {
        var wire = new WireScript
        {
            Hook = script.Hook,
            CallToAction = script.CallToAction,
            Description = script.Description,
        };
        wire.Sections.AddRange(script.Sections.Select(s => new WireScriptSection { Heading = s.Heading, Narration = s.Narration }));
        wire.TitleVariants.AddRange(script.TitleVariants);
        wire.Tags.AddRange(script.Tags);
        wire.Chapters.AddRange(script.Chapters.Select(c => new WireChapter { At = c.At, Title = c.Title }));
        return wire;
    }

    private static DomainScript ToDomain(WireScript wire) => new(
        wire.Hook,
        wire.Sections.Select(s => new DomainScriptSection(s.Heading, s.Narration)).ToList(),
        wire.CallToAction,
        wire.TitleVariants.ToList(),
        wire.Description,
        wire.Tags.ToList(),
        wire.Chapters.Select(c => new DomainChapter(c.At, c.Title)).ToList());

    private static WireClaim ToWire(DomainClaim claim)
    {
        var wire = new WireClaim
        {
            Id = claim.Id,
            Text = claim.Text,
            Risk = claim.Risk,
            Status = claim.Status,
        };
        wire.SourceUrls.AddRange(claim.SourceUrls);
        if (claim.Note is not null)
        {
            wire.Note = claim.Note;
        }
        return wire;
    }

    private static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
