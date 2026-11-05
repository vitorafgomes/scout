using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Scout.Operator;

/// <summary>
/// One scene as it plays in the video: where it starts, how long it lasts, how many caption lines it shows, whether it is a
/// placeholder, and a note on how an animated character was shown when it could not speak.
/// </summary>
internal sealed record SceneCut(OperatorScene Scene, int StartMs, int DurationMs, int Captions, bool Placeholder, string? Note = null);

/// <summary>The animated character of a project: its picture in the media directory and the mouth on it, either null until there is one.</summary>
internal sealed record PresenterInput(string? File, OperatorMouthBox? MouthBox);

/// <summary>The assembled video file, its length, its scenes as they play and the segment files it was joined from.</summary>
internal sealed record AssembledVideo(string File, int DurationMs, IReadOnlyList<SceneCut> Cuts, IReadOnlyList<string> Segments);

/// <summary>
/// Renders one 1920x1080 segment per scene, in order, then joins them with ffmpeg's concat demuxer into
/// <see cref="VideoFile"/>. A scene shows its picture, or, without one, the project's background picture, fading in
/// from black: under the <see cref="Gentle"/> motion, the default for any other value, scaled and padded once to twice
/// the frame and moved slowly across the scene at 30 frames a second, the first, third and every other scene zooming
/// in toward the centre and the others zooming out; under <see cref="Dynamic"/> with twice the zoom and twice the pan;
/// under <see cref="Still"/> scaled and padded to the frame and held. Without either, a dark text card whose first
/// sentence of the narration fades in and slides up, twice as far under <see cref="Dynamic"/>; every scene of
/// <see cref="MinSceneMs"/> or longer fades out to black at its end. It plays its narration, or silence, for its
/// duration, at least <see cref="MinSceneMs"/>; its narration is burned in as caption lines of at most
/// <see cref="CaptionWidth"/> characters, timed evenly across the scene. Every file is read and written inside the
/// project's media directory; a segment is named after its exact command and its inputs, so a segment already rendered
/// for the same inputs is used again. A scene that shows the owner's recording is its clip of the recording, with the
/// recording's own sound, scaled and padded to the frame at 30 frames a second, for exactly the clip's length, its text
/// burned in as captions the same way; only the video's first scene fades in and only its last fades out. A scene of an
/// animated character shows the presenter picture fitted to the frame's height and centred over the background or the dark
/// card, with the mouth shape of each moment of its narration, timed by rhubarb, scaled into the mouth box and laid over it,
/// then moved and faded as a picture; without a mouth box, narration or lip sync the character is shown still, and without
/// the picture, or with one ffmpeg cannot measure, the scene is a text card, each with a note on its cut.
/// </summary>
internal sealed class Assembler(FfmpegToolset ffmpeg, MediaProbe probe, RhubarbLipSync lipSync)
{
    internal const string VideoFile = "video.mp4";

    internal const string ConcatFile = "concat.txt";

    internal const int MinSceneMs = 2000;

    internal const int CaptionWidth = 42;

    internal const int CardWidth = 32;

    internal const string Still = "still";

    internal const string Gentle = "gentle";

    internal const string Dynamic = "dynamic";

    internal const string JoinFailed = "Scout could not join the scenes into the video";

    internal const string NoMouthBox = "Mark the mouth on the presenter picture in Scout Desktop, Production, so the character speaks.";

    internal const string UnreadablePresenter = "Scout could not read the presenter picture; attach another one in Scout Desktop, Production.";

    internal const string NoNarration = "The scene has no narration, so the character is shown still.";

    internal static string RenderFailed(int order) => $"Scout could not render scene {order}";

    /// <summary>
    /// Render the video of <paramref name="scenes"/>, whose durations are the real lengths of their narration; a scene
    /// without a picture file of its own is shown over <paramref name="background"/> when the media directory holds it,
    /// else as a text card, every scene moving under the <paramref name="motion"/> preset; a scene that shows the recording
    /// is its clip of <paramref name="recording"/>, which the media directory must hold.
    /// </summary>
    public Task<AssembledVideo> AssembleAsync(
        FfmpegTools tools, IReadOnlyList<OperatorScene> scenes, string directory, string? background, string? recording, string motion, CancellationToken ct)
        => AssembleAsync(tools, scenes, directory, background, recording, motion, null, ct);

    /// <summary>
    /// Render the video as above, a scene whose visual source is <c>presenter</c> showing <paramref name="presenter"/>'s
    /// picture and mouth.
    /// </summary>
    public async Task<AssembledVideo> AssembleAsync(
        FfmpegTools tools,
        IReadOnlyList<OperatorScene> scenes,
        string directory,
        string? background,
        string? recording,
        string motion,
        PresenterInput? presenter,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(scenes);
        var cuts = new List<SceneCut>();
        var segments = new List<string>();
        var start = 0;
        var backdrop = Usable(directory, background);
        var footage = Usable(directory, recording);
        var face = presenter is null ? null : Usable(directory, presenter.File);
        (int Width, int Height)? size = null;
        var measured = false;
        Task<LipSyncProbe>? lips = null;
        var ordered = scenes.OrderBy(s => s.Order).ToList();
        foreach (var (position, scene) in ordered.Index())
        {
            var clip = scene.VisualSource == OperatorPipeline.Recording
                ? footage ?? throw new InvalidOperationException(TranscriptionStage.NotFound)
                : null;
            var character = presenter is not null && scene.VisualSource == OperatorPipeline.Presenter;
            var duration = clip is not null ? scene.ClipEndMs - scene.ClipStartMs : Math.Max(scene.DurationMs, MinSceneMs);
            var image = clip ?? (character ? null : Usable(directory, scene.VisualFile) ?? backdrop);
            var audio = clip is not null ? null : Usable(directory, scene.AudioFile);
            var captions = FilterText.Wrap(scene.Narration, CaptionWidth);
            string? note = null;
            List<string> arguments;
            string identity = string.Empty;
            IReadOnlyList<MouthCue>? cues = null;
            if (clip is not null)
            {
                arguments = ClipArguments(tools, scene, position == 0, position == ordered.Count - 1, clip, captions, string.Empty);
            }
            else if (character)
            {
                if (face is not null && !measured)
                {
                    size = await PictureSizeAsync(tools, directory, presenter!.File!, ct).ConfigureAwait(false);
                    measured = true;
                }

                if (face is null || size is null)
                {
                    note = face is null ? OperatorPipeline.NoPresenterPicture : UnreadablePresenter;
                    arguments = SegmentArguments(tools, scene, position, duration, null, audio, captions, motion, string.Empty);
                }
                else
                {
                    (arguments, identity, cues, note) = await PresenterSceneAsync(
                        tools, scene, position, duration, directory, backdrop, face, size.Value, presenter!.MouthBox, audio, captions, motion, () => lips ??= lipSync.ProbeAsync(ct), ct)
                        .ConfigureAwait(false);
                    image = backdrop;
                }
            }
            else
            {
                arguments = SegmentArguments(tools, scene, position, duration, image, audio, captions, motion, string.Empty);
            }

            var segment = $"segment-{Key(tools, arguments, character ? (string?[])[image, face, audio] : [image, audio], identity)}.mp4";
            if (!MediaFiles.Exists(directory, segment))
            {
                var mouthTrack = cues is null ? null : await MouthTrack.WriteAsync(directory, scene.Id, cues, lipSync.MouthsPath, ct).ConfigureAwait(false);
                try
                {
                    await MediaFiles.RenderAsync(
                        directory,
                        segment,
                        RenderFailed(scene.Order),
                        temp => ffmpeg.RunAsync(tools, [.. arguments.SkipLast(1), temp], duration, RenderFailed(scene.Order), ct),
                        ct).ConfigureAwait(false);
                }
                finally
                {
                    if (mouthTrack is not null)
                    {
                        MediaFiles.Delete(directory, MouthTrack.FileName(scene.Id));
                    }
                }
            }

            segments.Add(segment);
            cuts.Add(new SceneCut(scene, start, duration, captions.Count, IsPlaceholder(scene, image, audio), note));
            start += duration;
        }

        await MediaFiles.WriteAsync(directory, ConcatFile, Encoding.UTF8.GetBytes(ConcatList(segments)), ct).ConfigureAwait(false);
        try
        {
            var list = MediaFiles.PathFor(directory, ConcatFile);
            await MediaFiles.RenderAsync(
                directory,
                VideoFile,
                JoinFailed,
                temp => ffmpeg.RunAsync(tools, ConcatArguments(tools, list, temp), start, JoinFailed, ct),
                ct).ConfigureAwait(false);
        }
        finally
        {
            MediaFiles.Delete(directory, ConcatFile);
        }

        return new AssembledVideo(VideoFile, start, cuts, segments);
    }

    internal static List<string> SegmentArguments(
        FfmpegTools tools, OperatorScene scene, int position, int durationMs, string? image, string? audio, IReadOnlyList<string> captions, string motion, string output)
    {
        var seconds = FfmpegToolset.Seconds(durationMs);
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-y" };
        var filters = new List<string>();
        var dynamic = motion == Dynamic;
        if (image is not null && motion == Still)
        {
            arguments.AddRange(["-loop", "1", "-framerate", "1", "-t", seconds, "-i", image]);
            filters.AddRange(["scale=1920:1080:force_original_aspect_ratio=decrease", "pad=1920:1080:(ow-iw)/2:(oh-ih)/2:color=black", "setsar=1", "fps=30"]);
        }
        else if (image is not null)
        {
            arguments.AddRange(["-i", image]);
            filters.AddRange(["scale=3840:2160:force_original_aspect_ratio=decrease", "pad=3840:2160:(ow-iw)/2:(oh-ih)/2:color=black", "setsar=1", ZoomPan(position, durationMs, dynamic)]);
        }
        else
        {
            arguments.AddRange(["-f", "lavfi", "-t", seconds, "-i", "color=c=0x14181f:s=1920x1080:r=30"]);
            var lines = FilterText.Wrap(FilterText.FirstSentence(scene.Narration), CardWidth);
            filters.AddRange(lines.Select((line, i) => CardLine(tools.FontPath, line, ((1080 - (84 * lines.Count)) / 2) + (84 * i), dynamic ? 48 : 24)));
        }

        return Encoded(tools, arguments, audio, "[0:v]", 1, filters, captions, image is not null, durationMs, output);
    }

    /// <summary>
    /// The end every picture and card segment shares: the narration, or silence, as the next input; the captions, the fade
    /// in when <paramref name="fadeIn"/>, the fade out of a scene of <see cref="MinSceneMs"/> or longer and the pixel format
    /// after <paramref name="filters"/> on the <paramref name="video"/> graph; the maps, the length, the encoders and
    /// <paramref name="output"/>.
    /// </summary>
    private static List<string> Encoded(
        FfmpegTools tools, List<string> arguments, string? audio, string video, int audioInput, List<string> filters, IReadOnlyList<string> captions, bool fadeIn, int durationMs, string output)
    {
        var seconds = FfmpegToolset.Seconds(durationMs);
        arguments.AddRange(audio is not null
            ? ["-i", audio]
            : ["-f", "lavfi", "-t", seconds, "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]);
        filters.AddRange(Captions(tools, captions, durationMs));
        if (fadeIn)
        {
            filters.Add("fade=t=in:st=0:d=0.4");
        }

        if (durationMs >= MinSceneMs)
        {
            filters.Add($"fade=t=out:st={FfmpegToolset.Seconds(durationMs - 400)}:d=0.4");
        }

        filters.Add("format=yuv420p");
        arguments.AddRange([
            "-filter_complex", string.Create(CultureInfo.InvariantCulture, $"{video}{string.Join(",", filters)}[v];[{audioInput}:a]aresample=48000,apad[a]"),
            "-map", "[v]", "-map", "[a]", "-t", seconds,
            .. tools.VideoArguments,
            .. tools.AudioArguments,
            output,
        ]);
        return arguments;
    }

    /// <summary>
    /// The clip of the owner's recording a scene shows: its own picture and sound from the clip's start for the clip's
    /// length, scaled and padded to 1920x1080 at 30 frames a second, its captions, a fade in on the
    /// <paramref name="first"/> scene and a fade out on the <paramref name="last"/>.
    /// </summary>
    internal static List<string> ClipArguments(
        FfmpegTools tools, OperatorScene scene, bool first, bool last, string recording, IReadOnlyList<string> captions, string output)
    {
        var durationMs = scene.ClipEndMs - scene.ClipStartMs;
        var seconds = FfmpegToolset.Seconds(durationMs);
        List<string> filters = ["scale=1920:1080:force_original_aspect_ratio=decrease", "pad=1920:1080:(ow-iw)/2:(oh-ih)/2:color=black", "setsar=1", "fps=30"];
        filters.AddRange(Captions(tools, captions, durationMs));
        if (first)
        {
            filters.Add("fade=t=in:st=0:d=0.4");
        }

        if (last)
        {
            filters.Add($"fade=t=out:st={FfmpegToolset.Seconds(Math.Max(0, durationMs - 400))}:d=0.4");
        }

        filters.Add("format=yuv420p");
        return
        [
            "-hide_banner", "-nostdin", "-y", "-ss", FfmpegToolset.Seconds(scene.ClipStartMs), "-t", seconds, "-i", recording,
            "-filter_complex", $"[0:v]{string.Join(",", filters)}[v];[0:a]aresample=48000,apad[a]",
            "-map", "[v]", "-map", "[a]", "-t", seconds,
            .. tools.VideoArguments,
            .. tools.AudioArguments,
            output,
        ];
    }

    /// <summary>
    /// A scene of the animated character: <paramref name="backdrop"/> scaled and padded to the canvas, else the dark card,
    /// at 30 frames a second; the presenter picture, <paramref name="size"/> in pixels, fitted to the canvas's height and
    /// centred; when <paramref name="mouth"/> is given, the mouth shapes of the ffconcat list at its track scaled to its box
    /// in the fitted picture's pixels and laid over it; then, unless the motion is <see cref="Still"/>, the slow move of the
    /// canvas, twice the frame, down to the frame, one output frame per input frame, and the captions and fades of a
    /// picture scene.
    /// </summary>
    internal static List<string> PresenterArguments(
        FfmpegTools tools,
        OperatorScene scene,
        int position,
        int durationMs,
        string? backdrop,
        string face,
        (int Width, int Height) size,
        (OperatorMouthBox Box, string Track)? mouth,
        string? audio,
        IReadOnlyList<string> captions,
        string motion,
        string output)
    {
        var seconds = FfmpegToolset.Seconds(durationMs);
        var still = motion == Still;
        var (width, height) = still ? (1920, 1080) : (3840, 2160);
        var scale = Math.Min((double)width / size.Width, (double)height / size.Height);
        var fitted = (Width: Even(size.Width * scale), Height: Even(size.Height * scale));
        var (left, top) = ((width - fitted.Width) / 2, (height - fitted.Height) / 2);
        List<string> arguments = ["-hide_banner", "-nostdin", "-y"];
        arguments.AddRange(backdrop is not null
            ? ["-loop", "1", "-framerate", "1", "-t", seconds, "-i", backdrop]
            : ["-f", "lavfi", "-t", seconds, "-i", $"color=c=0x14181f:s={width}x{height}:r=30"]);
        arguments.AddRange(["-i", face]);
        var background = backdrop is not null
            ? $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps=30"
            : "setsar=1";
        var graph = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"[0:v]{background}[bg];[1:v]scale={fitted.Width}:{fitted.Height},setsar=1[face];[bg][face]overlay={left}:{top}"));
        if (mouth is (var box, var track))
        {
            arguments.AddRange(["-f", "concat", "-safe", "0", "-i", track]);
            var placed = (
                X: left + (int)Math.Round(box.X * fitted.Width),
                Y: top + (int)Math.Round(box.Y * fitted.Height),
                Width: Math.Max(2, (int)Math.Round(box.Width * fitted.Width)),
                Height: Math.Max(2, (int)Math.Round(box.Height * fitted.Height)));
            graph.Append(CultureInfo.InvariantCulture, $"[body];[2:v]scale={placed.Width}:{placed.Height}[mouth];[body][mouth]overlay={placed.X}:{placed.Y}");
        }

        List<string> filters = still ? [] : [ZoomPan(position, durationMs, motion == Dynamic, frames: 1)];
        return Encoded(tools, arguments, audio, graph.Append(',').ToString(), mouth is null ? 2 : 3, filters, captions, true, durationMs, output);
    }

    /// <summary>
    /// A scene of the animated character with its picture <paramref name="face"/> of <paramref name="size"/>: its command,
    /// what its segment depends on beyond the command and its input files, the mouth cues of its narration when the
    /// character speaks, and the note on its cut when it is shown still: without a mouth box, without lip sync on this
    /// machine, which <paramref name="lips"/> answers once per assembly, or when rhubarb fails.
    /// </summary>
    private async Task<(List<string> Arguments, string Identity, IReadOnlyList<MouthCue>? Cues, string? Note)> PresenterSceneAsync(
        FfmpegTools tools,
        OperatorScene scene,
        int position,
        int durationMs,
        string directory,
        string? backdrop,
        string face,
        (int Width, int Height) size,
        OperatorMouthBox? mouthBox,
        string? audio,
        IReadOnlyList<string> captions,
        string motion,
        Func<Task<LipSyncProbe>> lips,
        CancellationToken ct)
    {
        string? note = null;
        IReadOnlyList<MouthCue>? cues = null;
        if (mouthBox is null)
        {
            note = NoMouthBox;
        }
        else if (audio is null)
        {
            note = NoNarration;
        }
        else
        {
            var probed = await lips().ConfigureAwait(false);
            if (!probed.Available)
            {
                note = probed.Notes[0];
            }
            else
            {
                var timed = await lipSync.CuesAsync(tools, directory, scene.Id, scene.AudioFile!, scene.DurationMs, ct).ConfigureAwait(false);
                cues = timed.Cues;
                note = timed.Failure is null ? null : RhubarbLipSync.Note(timed.Failure);
            }
        }

        (OperatorMouthBox, string)? mouth = cues is null ? null : (mouthBox!, MediaFiles.PathFor(directory, MouthTrack.FileName(scene.Id)));
        var arguments = PresenterArguments(tools, scene, position, durationMs, backdrop, face, size, mouth, audio, captions, motion, string.Empty);
        return (arguments, PresenterIdentity(mouthBox, cues, lipSync.MouthsPath), cues, note);
    }

    /// <summary>The size of the presenter picture <paramref name="file"/>; null when ffmpeg reports none, cannot start or runs past its timeout.</summary>
    private async Task<(int Width, int Height)?> PictureSizeAsync(FfmpegTools tools, string directory, string file, CancellationToken ct)
    {
        try
        {
            return await probe.PictureSizeAsync(tools, directory, file, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// What a presenter segment depends on beyond its command and input files: the mouth box, the mouth track by shape
    /// names, without the folder of the shapes, and the mouth set's version.
    /// </summary>
    private static string PresenterIdentity(OperatorMouthBox? mouth, IReadOnlyList<MouthCue>? cues, string mouths)
        => mouth is null
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $"{mouth.X:R},{mouth.Y:R},{mouth.Width:R},{mouth.Height:R}")
                + (cues is null ? string.Empty : $"\n{MouthTrack.List(cues, string.Empty)}\n{MouthTrack.Version(mouths)}");

    private static int Even(double pixels) => Math.Max(2, (int)Math.Round(pixels / 2) * 2);

    private static IEnumerable<string> Captions(FfmpegTools tools, IReadOnlyList<string> captions, int durationMs)
        => captions.Select((line, i) => FilterText.Draw(
            tools.FontPath,
            line,
            44,
            940,
            boxed: true,
            (int)((long)durationMs * i / captions.Count),
            (int)((long)durationMs * (i + 1) / captions.Count)));

    internal static List<string> ConcatArguments(FfmpegTools tools, string list, string output)
        => ["-hide_banner", "-nostdin", "-y", "-f", "concat", "-i", list, "-c:v", "copy", .. tools.AudioArguments, "-movflags", "+faststart", output];

    internal static string ConcatList(IEnumerable<string> segments)
        => "ffconcat version 1.0\n" + string.Concat(segments.Select(s => $"file '{s}'\n"));

    /// <summary>
    /// The slow move over a still scaled to 3840x2160: one input frame becomes every frame of the scene at 30 fps, the
    /// zoom a function of the frame index alone, from 1 to 1.08 drifting toward the centre at an even
    /// <paramref name="position"/>, from 1.08 back to 1 around the centre at an odd one; when
    /// <paramref name="dynamic"/>, to 1.16 with the drift twice as wide; with <paramref name="frames"/>, that many output
    /// frames per input frame, one over a moving input at 30 fps.
    /// </summary>
    internal static string ZoomPan(int position, int durationMs, bool dynamic, int? frames = null)
    {
        var total = (int)((((long)durationMs * 30) + 999) / 1000);
        var progress = string.Create(CultureInfo.InvariantCulture, $"on/{Math.Max(total - 1, 1)}");
        var (peak, rise, drift) = dynamic ? ("1.16", "0.16", "0.5") : ("1.08", "0.08", "0.25");
        var move = position % 2 == 0
            ? $"z=1+{rise}*{progress}:x=(iw-iw/zoom)*(0.25+{drift}*{progress}):y=(ih-ih/zoom)*(0.25+{drift}*{progress})"
            : $"z={peak}-{rise}*{progress}:x=(iw-iw/zoom)/2:y=(ih-ih/zoom)/2";
        return string.Create(CultureInfo.InvariantCulture, $"zoompan={move}:d={frames ?? total}:s=1920x1080:fps=30");
    }

    /// <summary>One line of a text card in white at 64 px, fading in over 0.6 s while it rises <paramref name="slide"/> px to <paramref name="y"/>.</summary>
    internal static string CardLine(string fontPath, string line, int y, int slide)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"drawtext=fontfile={FilterText.Escape(fontPath)}:expansion=none:text={FilterText.Escape(line)}:fontsize=64:fontcolor=white:x=(w-text_w)/2:y={FilterText.Escape($"{y}+{slide}*(1-min(t/0.6,1))")}:alpha={FilterText.Escape("if(lt(t,0.6),t/0.6,1)")}");

    private static string? Usable(string directory, string? file)
    {
        if (file is null)
        {
            return null;
        }

        var path = MediaFiles.PathFor(directory, file);
        return File.Exists(path) ? path : null;
    }

    private static bool IsPlaceholder(OperatorScene scene, string? image, string? audio)
        => scene.Status != OperatorPipeline.Ready
            || (scene.VisualSource != OperatorPipeline.Recording
                && ((image is null && scene.VisualSource is not (OperatorPipeline.TextCard or OperatorPipeline.Presenter))
                    || (audio is null && scene.AudioSource != OperatorPipeline.NoAudio)));

    /// <summary>
    /// The name of a segment: a hash of its command, with the font's folder left out, since the desktop mounts it at a new
    /// place on every launch, the length and write time of its <paramref name="inputs"/> and <paramref name="extra"/>.
    /// </summary>
    private static string Key(FfmpegTools tools, IEnumerable<string> arguments, IEnumerable<string?> inputs, string extra)
    {
        var font = FilterText.Escape(tools.FontPath);
        var named = FilterText.Escape(Path.GetFileName(tools.FontPath));
        var identity = new StringBuilder(string.Join('\n', arguments.Select(a => a.Replace(font, named, StringComparison.Ordinal))));
        foreach (var input in inputs)
        {
            if (input is not null)
            {
                var file = new FileInfo(input);
                identity.Append('\n').Append(file.Length).Append('@').Append(file.LastWriteTimeUtc.Ticks);
            }
        }

        if (extra.Length > 0)
        {
            identity.Append('\n').Append(extra);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())))[..16];
    }
}
