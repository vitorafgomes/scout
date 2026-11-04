using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scout.Operator;

/// <summary>
/// The package's first metadata and its checks. Metadata: the first title variant cut to YouTube's 100 characters at a
/// word, the script's description followed by chapters timed from the real scene lengths and the credits of the stock
/// pictures, cut to YouTube's 5000 characters by shortening the script's description first, and the script's tags kept in order while they fit
/// YouTube's 500 characters. Checks, each <c>pass</c>, <c>warn</c> or <c>fail</c>: duration, loudness, captions, placeholders,
/// metadata and discoverability.
/// </summary>
internal static partial class PackageChecks
{
    internal const string Pass = "pass";
    internal const string Warn = "warn";
    internal const string Fail = "fail";

    internal const int MinVideoMs = 30_000;
    internal const int MaxVideoMs = 12 * 60 * 60 * 1000;
    internal const double QuietestLufs = -18;
    internal const double LoudestLufs = -11;
    internal const int MaxTitle = 100;
    internal const int MaxDescription = 5000;
    internal const int MaxTags = 500;
    internal const int MaxTag = 100;
    internal const int MinChapterMs = 10_000;

    /// <summary>The checks of a package with this metadata over this video.</summary>
    public static IReadOnlyList<OperatorCheck> Run(
        AssembledVideo video, double? loudness, string title, string description, IReadOnlyList<string> tags, string topic, OperatorMandate mandate)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(mandate);
        return [Duration(video.DurationMs), Loudness(loudness), Captions(video.Cuts), Placeholders(video.Cuts),
            Metadata(title, description, tags), Discoverability(title, description, topic, mandate)];
    }

    internal static OperatorCheck Duration(int ms)
        => ms < MinVideoMs
            ? new("duration", Fail, $"The video lasts {Clock(ms)}; a video needs at least 0:30.")
            : ms > MaxVideoMs
                ? new("duration", Fail, $"The video lasts {Clock(ms)}; YouTube takes at most 12 hours.")
                : new("duration", Pass, $"The video lasts {Clock(ms)}.");

    internal static OperatorCheck Loudness(double? lufs)
        => lufs switch
        {
            null => new("loudness", Warn, "Scout could not measure the loudness of the video."),
            < QuietestLufs or > LoudestLufs => new(
                "loudness", Warn, $"The video plays at {Lufs(lufs.Value)} LUFS; aim between -18 and -11 LUFS."),
            _ => new("loudness", Pass, $"The video plays at {Lufs(lufs.Value)} LUFS."),
        };

    internal static OperatorCheck Captions(IReadOnlyList<SceneCut> cuts)
    {
        var missing = cuts.Where(c => FilterText.Clean(c.Scene.Narration).Length > 0 && c.Captions == 0).Select(c => c.Scene.Order).ToList();
        return missing.Count > 0
            ? new("captions", Fail, $"Scenes {string.Join(", ", missing)} have narration but no captions.")
            : new("captions", Pass, "Every scene with narration has captions.");
    }

    internal static OperatorCheck Placeholders(IReadOnlyList<SceneCut> cuts)
    {
        var placeholders = cuts.Where(c => c.Placeholder).Select(c => c.Scene.Order).ToList();
        return placeholders.Count > 0
            ? new("placeholders", Fail, $"Scenes {string.Join(", ", placeholders)} are not ready or miss their picture or narration file.")
            : new("placeholders", Pass, "Every scene is ready with its own media, the project's background or a text card.");
    }

    internal static OperatorCheck Metadata(string title, string description, IReadOnlyList<string> tags)
    {
        var problems = new List<string>();
        if (title.Trim().Length is 0 or > MaxTitle)
        {
            problems.Add($"the title must have 1 to {MaxTitle} characters (it has {title.Trim().Length})");
        }

        if (description.Length > MaxDescription)
        {
            problems.Add($"the description must have at most {MaxDescription} characters (it has {description.Length})");
        }

        if (TagsLength(tags) > MaxTags)
        {
            problems.Add($"the tags must fit in {MaxTags} characters (they take {TagsLength(tags)})");
        }

        return problems.Count > 0
            ? new("metadata", Fail, $"YouTube would refuse this metadata: {string.Join("; ", problems)}.")
            : new("metadata", Pass, "The title, description and tags fit YouTube's limits.");
    }

    internal static OperatorCheck Discoverability(string title, string description, string topic, OperatorMandate mandate)
    {
        var titleWords = Words(title);
        var warnings = new List<string>();
        if (!titleWords.Overlaps(Words(string.Join(' ', mandate.Pillars.Prepend(topic)))))
        {
            warnings.Add("the title shares no word with the topic or the channel's pillars");
        }

        if (!ChapterLine().IsMatch(description))
        {
            warnings.Add("the description has no chapters list");
        }

        return warnings.Count > 0
            ? new("discoverability", Warn, $"Viewers may not find this video: {string.Join("; ", warnings)}.")
            : new("discoverability", Pass, "The title matches the topic and the description lists chapters.");
    }

    /// <summary>The first title variant, or the topic when the script has none, cut to <see cref="MaxTitle"/> characters at a word.</summary>
    internal static string Title(OperatorScript? script, string topic)
        => Cut(script?.TitleVariants.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))?.Trim() ?? topic.Trim(), MaxTitle);

    /// <summary>
    /// The script's description, then the chapters, when the video has at least three of 10 seconds or more, each at the
    /// first scene whose narration its section holds, or for a scene of the recording the one whose clip starts where the
    /// section starts, so the owner's caption edits keep the chapters, then a
    /// <c>Credits</c> block with one line per distinct credit of the scenes' stock pictures; within
    /// <see cref="MaxDescription"/> characters, the script's description cut at a word to leave the chapters and credits
    /// whole, or the whole cut when they alone do not fit.
    /// </summary>
    internal static string Description(OperatorScript? script, IReadOnlyList<SceneCut> cuts)
    {
        var text = script?.Description.Trim() ?? string.Empty;
        var chapters = Chapters(script, cuts);
        var credits = cuts
            .Where(c => c.Scene.VisualSource == OperatorPipeline.Stock)
            .Select(c => FilterText.Clean(c.Scene.VisualCredit ?? string.Empty))
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        List<string> blocks =
        [
            .. chapters.Count > 0 ? [string.Join('\n', chapters.Select(c => $"{Clock(c.StartMs)} {c.Title}"))] : Array.Empty<string>(),
            .. credits.Count > 0 ? ["Credits\n" + string.Join('\n', credits)] : Array.Empty<string>(),
        ];
        if (blocks.Count == 0)
        {
            return Cut(text, MaxDescription);
        }

        var list = string.Join("\n\n", blocks);
        var room = MaxDescription - list.Length - 2;
        var kept = room > 0 ? Cut(text, room) : string.Empty;
        return Cut((kept.Length > 0 ? kept + "\n\n" : string.Empty) + list, MaxDescription);
    }

    /// <summary><paramref name="text"/> when it fits in <paramref name="max"/> characters, else cut at the last space that fits, without the space.</summary>
    internal static string Cut(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var end = max;
        for (var i = max; i > 0; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                end = i;
                break;
            }
        }

        if (char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }

        return text[..end].TrimEnd();
    }

    /// <summary>The script's tags of at most <see cref="MaxTag"/> characters, cleaned and kept in order while they fit in <see cref="MaxTags"/> characters.</summary>
    internal static IReadOnlyList<string> Tags(IEnumerable<string> tags)
    {
        var kept = new List<string>();
        foreach (var tag in tags.Select(t => FilterText.Clean(t).Replace(",", string.Empty, StringComparison.Ordinal).Trim()).Where(t => t.Length is > 0 and <= MaxTag))
        {
            if (TagsLength([.. kept, tag]) <= MaxTags)
            {
                kept.Add(tag);
            }
        }

        return kept;
    }

    /// <summary>How YouTube counts tags: their characters, two more for a tag with a space, and a comma between tags.</summary>
    internal static int TagsLength(IReadOnlyList<string> tags)
        => tags.Sum(t => t.Length + (t.Contains(' ', StringComparison.Ordinal) ? 2 : 0)) + Math.Max(0, tags.Count - 1);

    /// <summary>The integrated loudness from the end of ffmpeg's <c>loudnorm</c> output with <c>print_format=json</c>.</summary>
    internal static double? ParseLoudness(string errorTail)
    {
        var open = errorTail.LastIndexOf('{');
        var close = errorTail.LastIndexOf('}');
        if (open < 0 || close < open)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(errorTail[open..(close + 1)]);
            return document.RootElement.TryGetProperty("input_i", out var value)
                && value.ValueKind == JsonValueKind.String
                && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lufs)
                && double.IsFinite(lufs)
                    ? lufs
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static List<string> LoudnessArguments(string video)
        => ["-hide_banner", "-nostdin", "-i", video, "-vn", "-af", "loudnorm=print_format=json", "-f", "null", "-"];

    internal static string Clock(int ms)
    {
        var time = TimeSpan.FromMilliseconds(ms);
        return time.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{time.Minutes}:{time.Seconds:00}");
    }

    private static List<(int StartMs, string Title)> Chapters(OperatorScript? script, IReadOnlyList<SceneCut> cuts)
    {
        var chapters = new List<(int StartMs, string Title)>();
        foreach (var section in script?.Sections ?? [])
        {
            var narration = FilterText.Clean(section.Narration);
            var first = cuts.FirstOrDefault(c => c.Scene.VisualSource == OperatorPipeline.Recording
                ? c.Scene.ClipStartMs == section.StartMs
                : FilterText.Clean(c.Scene.Narration) is { Length: > 0 } scene && narration.Contains(scene, StringComparison.Ordinal));
            var heading = FilterText.Clean(section.Heading);
            if (first is null || heading.Length == 0)
            {
                continue;
            }

            var start = chapters.Count == 0 ? 0 : first.StartMs;
            if (chapters.Count == 0 || start - chapters[^1].StartMs >= MinChapterMs)
            {
                chapters.Add((start, heading));
            }
        }

        var end = cuts.Count > 0 ? cuts[^1].StartMs + cuts[^1].DurationMs : 0;
        if (chapters.Count > 0 && end - chapters[^1].StartMs < MinChapterMs)
        {
            chapters.RemoveAt(chapters.Count - 1);
        }

        return chapters.Count >= 3 ? chapters : [];
    }

    private static HashSet<string> Words(string text)
        => [.. WordPattern().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length >= 3)];

    private static string Lufs(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\p{L}[\p{L}\p{N}]*|\p{N}+")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"(^|\n)0:00 \S")]
    private static partial Regex ChapterLine();
}
