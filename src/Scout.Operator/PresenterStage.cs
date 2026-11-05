using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Scout.Operator;

/// <summary>
/// The presenter job of a project the worker took as <c>presenting</c>: one presenter version made from the owner's request
/// and saved, with the status the project had before and the request cleared. A request <c>builtin:&lt;id&gt;</c> takes that
/// built-in presenter, or the project's own when the id is unknown; <c>first</c> on a project whose presenter picture the
/// owner attached takes that picture; any other request asks the image service for a picture of the owner's prompt, else the
/// current version's prompt, else the last one with a prompt, else <see cref="ComposedPrompt"/>, with the owner's feedback appended as <c>Change: ...</c> and
/// <see cref="SafetyTail"/> always last. Without an image service, with the day's pictures spent or when the request fails,
/// the project's own built-in presenter is used. The mouth of a generated or attached picture is found by
/// <see cref="IFaceMouth"/>; without a face the built-in presenter stands in with its own box and <see cref="NoFace"/>. The
/// preview is the hook's first sentence narrated in the project's voice into <c>presenter-preview-&lt;n&gt;</c> and rendered
/// at 1280x720 over the first scene's picture, or the dark card; a preview that fails leaves the version without one and a
/// note. Every file is written in the media directory; the worker removes only the preview narration of a version the control plane drops.
/// </summary>
internal sealed partial class PresenterStage(
    IOperatorControlPlane controlPlane,
    VisualGenerator visuals,
    NarrationGenerator narration,
    BuiltInPresenters builtIns,
    IFaceMouth faces,
    Assembler assembler,
    FfmpegToolset ffmpeg,
    MediaProbe probe,
    TimeProvider time,
    ILogger<PresenterStage> logger)
{
    internal const string Presenting = "presenting";

    internal const string First = "first";

    internal const string Another = "another";

    internal const string PromptOnly = "prompt";

    internal const string BuiltInRequest = "builtin:";

    internal const string Owner = "owner";

    internal const string BuiltIn = "builtin";

    internal const string NoLongerPresenting = "The project is no longer presenting.";

    internal const string Style =
        "flat 2D cartoon illustration, front-facing, looking at the viewer, mouth closed, neutral expression, head and shoulders, on a solid bright green background (#00FF00)";

    internal static readonly (int Width, int Height) PreviewFrame = (1280, 720);

    /// <summary>The closing sentence of every generated presenter's prompt, never part of the owner's editable text.</summary>
    internal static readonly string SafetyTail = string.Concat(char.ToUpperInvariant(VisualGenerator.NoRealPerson[0]).ToString(), VisualGenerator.NoRealPerson[1..]);

    internal static string NoFace(string label) =>
        $"No face was found on the presenter picture; Scout's own presenter {label} is used. Mark the mouth or ask for another picture.";

    internal static string PreviewFailed(string reason) => $"Scout could not make the presenter preview: {reason}";

    internal static string PreviewFile(int number) => $"presenter-preview-{number}.mp4";

    /// <summary>The prompt of a first presenter: the owner's direction, the presenter description or the topic, and <see cref="Style"/>.</summary>
    internal static string ComposedPrompt(OperatorProjectState project)
        => string.Join(
            ". ",
            new[] { project.Direction, project.Presenter.Length > 0 ? project.Presenter : project.Topic, Style }
                .Select(part => part.Trim().TrimEnd('.'))
                .Where(part => part.Length > 0)) + ".";

    /// <summary>The prompt sent to the image service: <paramref name="prompt"/>, then <see cref="SafetyTail"/>.</summary>
    internal static string WithSafetyTail(string prompt) => $"{prompt.Trim()} {SafetyTail}";

    /// <summary>
    /// Run the presenter job of project <paramref name="id"/>; false, with nothing saved, when the project is no longer
    /// <c>presenting</c>, read so or refused so by the control plane.
    /// </summary>
    public async Task<bool> RunAsync(string id, CancellationToken ct)
    {
        var directory = await controlPlane.GetMediaDirectoryAsync(id, ct).ConfigureAwait(false);
        var project = await controlPlane.GetProjectAsync(id, ct).ConfigureAwait(false);
        if (project.Status != Presenting)
        {
            return false;
        }

        var request = project.PresenterRequest.Trim();
        var number = NextNumber(project.PresenterVersions);
        var made = await PictureAsync(project, request, directory, ct).ConfigureAwait(false);
        if (made is null)
        {
            return await SaveAsync(id, directory, null, OperatorPipeline.NoPresenterPicture, ct).ConfigureAwait(false);
        }

        var (file, source, prompt, feedback, box, note) = made;
        string? preview = null;
        try
        {
            (preview, var stillNote) = await PreviewAsync(project, directory, new PresenterInput(file, box, source), number, ct).ConfigureAwait(false);
            note ??= stillNote;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("The presenter preview of video project {ProjectId} failed.", id);
            note = PreviewFailed(ex.Message);
            MediaFiles.Delete(directory, $"presenter-preview-{number}.wav");
            MediaFiles.Delete(directory, $"presenter-preview-{number}.mp3");
        }

        var version = new OperatorPresenterVersion(
            file, prompt, feedback, preview, box, time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), source);
        return await SaveAsync(id, directory, version, note, ct).ConfigureAwait(false);
    }

    private async Task<PresenterPick?> PictureAsync(
        OperatorProjectState project, string request, string directory, CancellationToken ct)
    {
        if (request.StartsWith(BuiltInRequest, StringComparison.Ordinal))
        {
            return await BuiltInAsync(project.Id, request[BuiltInRequest.Length..], directory, string.Empty, request, null, ct).ConfigureAwait(false);
        }

        if (request == First && project.PresenterSource == Owner && project.PresenterFile is { } attached && MediaFiles.Exists(directory, attached))
        {
            return await WithMouthAsync(project.Id, attached, Owner, string.Empty, string.Empty, directory, ct).ConfigureAwait(false);
        }

        var feedback = request is First or Another or PromptOnly ? string.Empty : request;
        var basePrompt = project.PresenterRequestPrompt.Trim() is { Length: > 0 } owned
            ? owned
            : project.PresenterVersions.FirstOrDefault(v => v.File == project.PresenterFile && v.Prompt.Length > 0)?.Prompt
                ?? project.PresenterVersions.LastOrDefault(v => v.Prompt.Length > 0)?.Prompt
                ?? ComposedPrompt(project);
        var prompt = feedback.Length == 0 ? basePrompt : $"{basePrompt.TrimEnd()} Change: {Sentence(feedback)}";
        var file = $"presenter-{RandomNumberGenerator.GetHexString(12, lowercase: true)}.png";
        bool generated;
        try
        {
            generated = await visuals.PictureAsync(WithSafetyTail(prompt), directory, file, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            generated = false;
        }

        return generated
            ? await WithMouthAsync(project.Id, file, OperatorPipeline.Generated, prompt, feedback, directory, ct).ConfigureAwait(false)
            : await BuiltInAsync(project.Id, null, directory, prompt, feedback, null, ct).ConfigureAwait(false);
    }

    private async Task<PresenterPick?> WithMouthAsync(
        string projectId, string file, string source, string prompt, string feedback, string directory, CancellationToken ct)
    {
        if (await faces.FindAsync(MediaFiles.PathFor(directory, file), ct).ConfigureAwait(false) is { } box)
        {
            return new PresenterPick(file, source, prompt, feedback, box, null);
        }

        var fallback = builtIns.Choose(projectId);
        if (fallback is null)
        {
            return new PresenterPick(file, source, prompt, feedback, null, Assembler.NoMouthBox);
        }

        if (source == OperatorPipeline.Generated)
        {
            MediaFiles.Delete(directory, file);
        }

        return await BuiltInAsync(projectId, fallback.Id, directory, prompt, feedback, NoFace(fallback.Label), ct).ConfigureAwait(false);
    }

    private async Task<PresenterPick?> BuiltInAsync(
        string projectId, string? id, string directory, string prompt, string feedback, string? note, CancellationToken ct)
    {
        var chosen = builtIns.Choose(projectId, id);
        if (chosen is null || await builtIns.CopyAsync(chosen, directory, ct).ConfigureAwait(false) is not { } file)
        {
            return null;
        }

        return new PresenterPick(file, BuiltIn, prompt, feedback, chosen.MouthBox, note);
    }

    private async Task<(string? Preview, string? Note)> PreviewAsync(
        OperatorProjectState project, string directory, PresenterInput presenter, int number, CancellationToken ct)
    {
        var sentence = FilterText.FirstSentence(project.Script?.Hook ?? string.Empty);
        if (sentence.Length == 0 || (await ffmpeg.ProbeAsync(ct).ConfigureAwait(false)).Tools is not { } tools)
        {
            return (null, null);
        }

        var stem = $"presenter-preview-{number}";
        var line = new OperatorScene($"preview-{number}", 1, sentence, string.Empty, false, OperatorPipeline.Ready, null, OperatorPipeline.NoAudio, OperatorPipeline.Presenter, null, null, 0);
        var spoken = await narration.GenerateAsync(line, project.NarrationVoice, directory, stem, ct).ConfigureAwait(false);
        var length = spoken.AudioFile is { } audio ? await probe.DurationMsAsync(tools, directory, audio, ct).ConfigureAwait(false) : spoken.DurationMs;
        var backdrop = project.Scenes.OrderBy(s => s.Order).FirstOrDefault()?.VisualFile is { } picture && MediaFiles.Exists(directory, picture) ? picture : null;
        var note = await assembler.PreviewAsync(
            tools, line with { AudioFile = spoken.AudioFile, DurationMs = length }, directory, backdrop, presenter, project.Motion, PreviewFrame, PreviewFile(number), ct)
            .ConfigureAwait(false);
        return (PreviewFile(number), note);
    }

    private async Task<bool> SaveAsync(string id, string directory, OperatorPresenterVersion? version, string? note, CancellationToken ct)
    {
        var current = await controlPlane.GetProjectAsync(id, ct).ConfigureAwait(false);
        if (current.Status != Presenting)
        {
            return false;
        }

        var next = Returned(current) with { Error = note ?? (current.PresenterReturnStatus == OperatorPipeline.Failed ? current.Error : null) };
        if (version is not null)
        {
            next = next with
            {
                PresenterVersions = [.. current.PresenterVersions, version],
                PresenterFile = version.File,
                MouthBox = version.MouthBox,
                PresenterPreviewFile = version.PreviewFile,
                PresenterSource = version.Source,
            };
        }

        OperatorProjectState saved;
        try
        {
            saved = await controlPlane.SaveProjectAsync(next, null, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message == NoLongerPresenting)
        {
            return false;
        }

        Prune(directory, current, saved);
        return true;
    }

    /// <summary>The project as the presenter job leaves it: back in the status it had before, the request cleared.</summary>
    internal static OperatorProjectState Returned(OperatorProjectState current)
        => current with
        {
            Status = current.PresenterReturnStatus.Length > 0 ? current.PresenterReturnStatus : current.Status,
            PresenterRequest = string.Empty,
            PresenterRequestPrompt = string.Empty,
        };

    private static void Prune(string directory, OperatorProjectState before, OperatorProjectState saved)
    {
        var kept = saved.PresenterVersions.Select(v => v.PreviewFile)
            .Append(saved.PresenterPreviewFile)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        foreach (var dropped in before.PresenterVersions.Where(v => !saved.PresenterVersions.Contains(v)))
        {
            if (dropped.PreviewFile is { } preview && !kept.Contains(preview))
            {
                MediaFiles.Delete(directory, Path.ChangeExtension(preview, ".wav"));
                MediaFiles.Delete(directory, Path.ChangeExtension(preview, ".mp3"));
            }
        }
    }

    private static int NextNumber(IReadOnlyList<OperatorPresenterVersion> versions)
        => 1 + versions
            .Select(v => v.PreviewFile is { } preview && Numbered().Match(preview) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0)
            .Append(versions.Count)
            .Max();

    private static string Sentence(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length > 0 && trimmed[^1] is '.' or '!' or '?' ? trimmed : $"{trimmed}.";
    }

    [GeneratedRegex(@"^presenter-preview-(\d{1,6})\.mp4$")]
    private static partial Regex Numbered();

    private sealed record PresenterPick(string File, string Source, string Prompt, string Feedback, OperatorMouthBox? Box, string? Note);
}
