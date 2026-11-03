using System.Globalization;
using Grpc.Core;
using Scout.ControlPlane.Operator;
using DomainChapter = Scout.ControlPlane.Operator.Chapter;
using DomainClaim = Scout.ControlPlane.Operator.Claim;
using DomainMandate = Scout.ControlPlane.Operator.Mandate;
using DomainPackage = Scout.ControlPlane.Operator.VideoPackage;
using DomainPackageCheck = Scout.ControlPlane.Operator.PackageCheck;
using DomainReadiness = Scout.ControlPlane.Operator.Readiness;
using DomainScene = Scout.ControlPlane.Operator.Scene;
using DomainScript = Scout.ControlPlane.Operator.Script;
using DomainScriptSection = Scout.ControlPlane.Operator.ScriptSection;
using DomainShortClip = Scout.ControlPlane.Operator.ShortClip;
using DomainTopicCandidate = Scout.ControlPlane.Operator.TopicCandidate;
using WireChapter = Scout.ControlPlane.Contracts.Chapter;
using WireClaim = Scout.ControlPlane.Contracts.Claim;
using WireMandate = Scout.ControlPlane.Contracts.Mandate;
using WirePackage = Scout.ControlPlane.Contracts.VideoPackage;
using WirePackageCheck = Scout.ControlPlane.Contracts.PackageCheck;
using WireProject = Scout.ControlPlane.Contracts.Project;
using WireReadiness = Scout.ControlPlane.Contracts.Readiness;
using WireScene = Scout.ControlPlane.Contracts.Scene;
using WireScript = Scout.ControlPlane.Contracts.Script;
using WireScriptSection = Scout.ControlPlane.Contracts.ScriptSection;
using WireShortClip = Scout.ControlPlane.Contracts.ShortClip;
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
        wire.Scenes.AddRange(project.Scenes.Select(ToWire));
        if (project.Package is { } package)
        {
            wire.Package = ToWire(package);
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
        wire.HasError ? wire.Error : null)
    {
        Scenes = wire.Scenes.Select(s => new DomainScene(
            s.Id,
            s.Order,
            s.Narration,
            s.VisualPrompt,
            s.Locked,
            s.Status,
            s.HasError ? s.Error : null,
            s.AudioSource,
            s.VisualSource,
            s.HasAudioFile ? s.AudioFile : null,
            s.HasVisualFile ? s.VisualFile : null,
            s.DurationMs)).ToList(),
        Package = wire.Package is null ? null : ToDomain(wire.Package),
    };

    public static WireReadiness ToWire(DomainReadiness readiness)
    {
        var wire = new WireReadiness
        {
            SpeechAvailable = readiness.SpeechAvailable,
            ImagesAvailable = readiness.ImagesAvailable,
            Provider = readiness.Provider,
            FfmpegAvailable = readiness.FfmpegAvailable,
            YoutubeSignedIn = readiness.YouTubeSignedIn,
        };
        wire.Notes.AddRange(readiness.Notes);
        if (readiness.ReportedAt is { } reportedAt)
        {
            wire.ReportedAt = Iso(reportedAt);
        }
        return wire;
    }

    public static DomainReadiness ToDomain(WireReadiness wire) => new(
        wire.SpeechAvailable,
        wire.ImagesAvailable,
        wire.Provider,
        wire.FfmpegAvailable,
        wire.YoutubeSignedIn,
        wire.Notes.ToList());

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
        catch (OperatorStoreException ex)
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

    private static WireScene ToWire(DomainScene scene)
    {
        var wire = new WireScene
        {
            Id = scene.Id,
            Order = scene.Order,
            Narration = scene.Narration,
            VisualPrompt = scene.VisualPrompt,
            Locked = scene.Locked,
            Status = scene.Status,
            AudioSource = scene.AudioSource,
            VisualSource = scene.VisualSource,
            DurationMs = scene.DurationMs,
        };
        if (scene.Error is not null)
        {
            wire.Error = scene.Error;
        }
        if (scene.AudioFile is not null)
        {
            wire.AudioFile = scene.AudioFile;
        }
        if (scene.VisualFile is not null)
        {
            wire.VisualFile = scene.VisualFile;
        }
        return wire;
    }

    private static WirePackage ToWire(DomainPackage package)
    {
        var wire = new WirePackage
        {
            Status = package.Status,
            RightsConfirmed = package.RightsConfirmed,
            Title = package.Title,
            Description = package.Description,
            DurationMs = package.DurationMs,
            MetadataEdited = package.MetadataEdited,
        };
        if (package.VideoFile is not null)
        {
            wire.VideoFile = package.VideoFile;
        }
        if (package.ThumbnailFile is not null)
        {
            wire.ThumbnailFile = package.ThumbnailFile;
        }
        if (package.ApprovedAt is { } approvedAt)
        {
            wire.ApprovedAt = Iso(approvedAt);
        }
        if (package.Error is not null)
        {
            wire.Error = package.Error;
        }
        wire.Shorts.AddRange(package.Shorts.Select(s => new WireShortClip
        {
            File = s.File,
            StartMs = s.StartMs,
            DurationMs = s.DurationMs,
            Hook = s.Hook,
        }));
        wire.Checks.AddRange(package.Checks.Select(c => new WirePackageCheck { Name = c.Name, Result = c.Result, Detail = c.Detail }));
        wire.Tags.AddRange(package.Tags);
        return wire;
    }

    private static DomainPackage ToDomain(WirePackage wire) => new(
        wire.Status,
        wire.HasVideoFile ? wire.VideoFile : null,
        wire.Shorts.Select(s => new DomainShortClip(s.File, s.StartMs, s.DurationMs, s.Hook)).ToList(),
        wire.HasThumbnailFile ? wire.ThumbnailFile : null,
        wire.Checks.Select(c => new DomainPackageCheck(c.Name, c.Result, c.Detail)).ToList(),
        wire.RightsConfirmed,
        wire.HasApprovedAt ? ParseIso(wire.ApprovedAt) : null,
        wire.Title,
        wire.Description,
        wire.Tags.ToList(),
        wire.DurationMs,
        wire.HasError ? wire.Error : null,
        wire.MetadataEdited);

    private static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : throw new OperatorValidationException($"'{value}' is not a time Scout accepts.");

    private static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
