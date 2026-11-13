namespace Scout.Operator;

/// <summary>What an assembly rendered: the video, its Shorts, its thumbnail and its measured loudness.</summary>
internal sealed record AssemblyRender(AssembledVideo Video, IReadOnlyList<OperatorShort> Shorts, string Thumbnail, double? Loudness);

/// <summary>
/// Renders a project's package in its media directory: the real length of every narration file, the video, then the
/// Shorts, the thumbnail and the loudness measure at the same time. A render that fails waits for all three to end,
/// then removes the video, Shorts and thumbnail they may have left, and keeps the scene segments already rendered, so
/// the next assembly continues from them; once the package is saved only the segments and Shorts of other assemblies
/// are removed, so a later assembly renders again only the scenes that changed.
/// </summary>
internal sealed class AssemblyStage(
    FfmpegToolset ffmpeg,
    MediaProbe probe,
    Assembler assembler,
    ShortsCutter shorts,
    ThumbnailMaker thumbnails)
{
    internal const string Assembled = "assembled";

    /// <summary>The tools, or the readiness note as the failure when videos cannot be assembled here.</summary>
    public async Task<FfmpegTools> ToolsAsync(CancellationToken ct)
    {
        var found = await ffmpeg.ProbeAsync(ct).ConfigureAwait(false);
        return found.Tools ?? throw new InvalidOperationException(found.Note);
    }

    public async Task<AssemblyRender> RenderAsync(FfmpegTools tools, OperatorProjectState project, string directory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        try
        {
            var scenes = new List<OperatorScene>();
            foreach (var scene in project.Scenes.OrderBy(s => s.Order))
            {
                scenes.Add(scene.AudioFile is { } audio && MediaFiles.Exists(directory, audio)
                    ? scene with { DurationMs = await probe.DurationMsAsync(tools, directory, audio, ct).ConfigureAwait(false) }
                    : scene);
            }

            var presenter = project.Format == OperatorPipeline.AnimatedCharacter ? new PresenterInput(project.PresenterFile, project.MouthBox, project.PresenterSource) : null;
            var video = await assembler.AssembleAsync(
                tools, scenes, directory, project.BackgroundFile, project.RecordingFile, project.Motion, presenter, ct).ConfigureAwait(false);
            var cut = shorts.CutAsync(tools, directory, video, ct);
            var thumbnail = thumbnails.MakeAsync(
                tools, directory, PackageChecks.Title(project.Script, project.Topic), video, project.Format == OperatorPipeline.OwnVideo, ct);
            var loudness = LoudnessAsync(tools, directory, video, ct);
            await Task.WhenAll(cut, thumbnail, loudness).ConfigureAwait(false);
            return new AssemblyRender(video, await cut.ConfigureAwait(false), await thumbnail.ConfigureAwait(false), await loudness.ConfigureAwait(false));
        }
        catch
        {
            Discard(directory);
            throw;
        }
    }

    /// <summary>Remove the video, the Shorts and the thumbnail an assembly writes.</summary>
    public static void Discard(string directory)
    {
        MediaFiles.Delete(directory, Assembler.VideoFile);
        MediaFiles.Delete(directory, ThumbnailMaker.FileName);
        for (var number = 1; number <= ShortsCutter.MaxShorts; number++)
        {
            MediaFiles.Delete(directory, ShortsCutter.FileName(number));
        }
    }

    /// <summary>
    /// Remove the scene segments the saved video was not joined from and the Shorts past the ones it cut; the segments it
    /// was joined from stay for the next assembly. A folder that cannot be listed leaves its files as they are.
    /// </summary>
    public static void Prune(string directory, AssemblyRender render)
    {
        ArgumentNullException.ThrowIfNull(render);
        List<string> segments;
        try
        {
            segments = [.. Directory.EnumerateFiles(directory, "segment-*.mp4").Select(Path.GetFileName).OfType<string>()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            segments = [];
        }

        foreach (var segment in segments)
        {
            if (!render.Video.Segments.Contains(segment, StringComparer.Ordinal))
            {
                MediaFiles.Delete(directory, segment);
            }
        }

        for (var number = render.Shorts.Count + 1; number <= ShortsCutter.MaxShorts; number++)
        {
            MediaFiles.Delete(directory, ShortsCutter.FileName(number));
        }
    }

    /// <summary>
    /// The package of <paramref name="render"/> on the project as the control plane holds it now: the owner's rights,
    /// approval and edited metadata exactly as read, the first metadata otherwise, and the checks over the metadata kept.
    /// </summary>
    public static OperatorPackage Package(OperatorProjectState current, AssemblyRender render, OperatorMandate mandate)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(render);
        var stored = current.Package;
        var edited = stored?.MetadataEdited == true;
        var title = edited ? stored!.Title : PackageChecks.Title(current.Script, current.Topic);
        var description = edited ? stored!.Description : PackageChecks.Description(current.Script, render.Video.Cuts);
        var tags = edited ? stored!.Tags : PackageChecks.Tags(current.Script?.Tags ?? []);
        return new OperatorPackage(
            Assembled,
            render.Video.File,
            render.Shorts,
            render.Thumbnail,
            PackageChecks.Run(render.Video, render.Loudness, title, description, tags, current.Topic, mandate),
            stored?.RightsConfirmed ?? false,
            stored?.ApprovedAt,
            title,
            description,
            tags,
            render.Video.DurationMs,
            null,
            edited);
    }

    /// <summary>The project failed with <paramref name="reason"/>: no files in its package, the owner's fields as read.</summary>
    public static OperatorProjectState Failed(OperatorProjectState current, string reason)
    {
        ArgumentNullException.ThrowIfNull(current);
        var stored = current.Package ?? new OperatorPackage(
            OperatorPipeline.Failed, null, [], null, [], false, null, string.Empty, string.Empty, [], 0, null, false);
        return current with
        {
            Status = OperatorPipeline.Failed,
            Error = reason,
            Package = stored with
            {
                Status = OperatorPipeline.Failed,
                VideoFile = null,
                Shorts = [],
                ThumbnailFile = null,
                Checks = [],
                DurationMs = 0,
                Error = reason,
            },
        };
    }

    private async Task<double?> LoudnessAsync(FfmpegTools tools, string directory, AssembledVideo video, CancellationToken ct)
    {
        try
        {
            var measured = await ffmpeg.RunAsync(
                tools, PackageChecks.LoudnessArguments(MediaFiles.PathFor(directory, video.File)), video.DurationMs, "loudness", ct)
                .ConfigureAwait(false);
            return PackageChecks.ParseLoudness(measured.ErrorTail);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
