namespace Scout.Operator.Tests.Fakes;

/// <summary>
/// An <see cref="IProcessRunner"/> that records every call and its timeout and answers by the shape of the command, as
/// ffmpeg would: <c>-version</c> succeeds, <c>-encoders</c> lists <see cref="EncoderList"/>, <c>-filters</c>
/// <see cref="FilterList"/>, <c>-demuxers</c> <see cref="DemuxerList"/>, a command that only reads an input reports a
/// <c>Duration:</c> of <see cref="ProbedMs"/> and exits 1, a loudness measure ends with <see cref="LoudnessTail"/>, and
/// any other command writes 1 KB to its last argument, the output file. <see cref="Answer"/> answers, or throws,
/// before any of that.
/// </summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public const string AllEncoders = """
        Encoders:
         V..... = Video
         A..... = Audio
         S..... = Subtitle
         .F.... = Frame-level multithreading
         ..S... = Slice-level multithreading
         ...X.. = Codec is experimental
         ....B. = Supports draw_horiz_band
         .....D = Supports direct rendering method 1
         ------
         V....D libvpx-vp9           libvpx VP9 (codec vp9)
         V....D h264_videotoolbox    VideoToolbox H.264 Encoder (codec h264)
         V....D libopenh264          OpenH264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
         V....D png                  PNG (Portable Network Graphics) image
         A....D aac                  AAC (Advanced Audio Coding) (codec aac)
         A....D libopus              libopus Opus (codec opus)
        """;

    public const string Loudnorm = """
        [out#0/null @ 0x5581c7d3e2c0] video:0KiB audio:5625KiB subtitle:0KiB other streams:0KiB global headers:0KiB muxing overhead: unknown
        size=N/A time=00:00:30.00 bitrate=N/A speed= 412x
        [Parsed_loudnorm_0 @ 0x5581c7d1a840]
        {
        	"input_i" : "-14.21",
        	"input_tp" : "-1.03",
        	"input_lra" : "5.60",
        	"input_thresh" : "-24.40",
        	"output_i" : "-23.79",
        	"output_tp" : "-10.57",
        	"output_lra" : "4.80",
        	"output_thresh" : "-33.92",
        	"normalization_type" : "dynamic",
        	"target_offset" : "-0.21"
        }
        """;

    public static readonly string AllFilters = Filters(FfmpegToolset.Filters);

    public const string AllDemuxers = """
        File formats:
         D. = Demuxing supported
         .E = Muxing supported
         ..d = Is a device
         ---
         D   aac             raw ADTS AAC (Advanced Audio Coding)
         D   concat          Virtual concatenation script
         D d lavfi           Libavfilter virtual input device
         D   mp3             MP2/3 (MPEG audio layer 2/3)
        """;

    private readonly Lock _lock = new();
    private readonly List<TimeSpan> _timeouts = [];
    private readonly List<(string Program, IReadOnlyList<string> Arguments)> _calls = [];

    public string EncoderList { get; init; } = AllEncoders;

    public string FilterList { get; init; } = AllFilters;

    public string DemuxerList { get; init; } = AllDemuxers;

    public int ProbedMs { get; init; } = 3500;

    public string LoudnessTail { get; init; } = Loudnorm;

    public Func<string, IReadOnlyList<string>, ProcessResult?> Answer { get; init; } = (_, _) => null;

    public IReadOnlyList<(string Program, IReadOnlyList<string> Arguments)> Calls
    {
        get
        {
            lock (_lock)
            {
                return [.. _calls];
            }
        }
    }

    public IReadOnlyList<TimeSpan> Timeouts
    {
        get
        {
            lock (_lock)
            {
                return [.. _timeouts];
            }
        }
    }

    /// <summary>The ffmpeg commands that wrote a file, in order.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Renders
        => [.. Calls.Where(c => c.Program.EndsWith("ffmpeg", StringComparison.Ordinal) && Path.IsPathFullyQualified(c.Arguments[^1]) && !ReadsOnly(c.Arguments))
            .Select(c => c.Arguments)];

    /// <summary>The files whose length was read, in order.</summary>
    public IReadOnlyList<string> Measured => [.. Calls.Where(c => ReadsOnly(c.Arguments)).Select(c => c.Arguments[^1])];

    /// <summary>A <c>-filters</c> listing of exactly <paramref name="names"/>, in ffmpeg's layout.</summary>
    public static string Filters(IEnumerable<string> names)
        => "Filters:\n  T.. = Timeline support\n  .S. = Slice threading\n  A = Audio input/output\n  V = Video input/output\n  | = Source or sink filter\n"
            + string.Concat(names.Select(n => $" T.. {n,-16} V->V       A filter.\n"));

    public Task<ProcessResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        lock (_lock)
        {
            _calls.Add((program, [.. arguments]));
            _timeouts.Add(timeout);
        }

        if (Answer(program, arguments) is { } answered)
        {
            return Task.FromResult(answered);
        }

        if (arguments.Contains("-version"))
        {
            return Task.FromResult(new ProcessResult(0, $"{Path.GetFileName(program)} version 7.1", string.Empty));
        }

        if (arguments.Contains("-encoders"))
        {
            return Task.FromResult(new ProcessResult(0, EncoderList, string.Empty));
        }

        if (arguments.Contains("-filters"))
        {
            return Task.FromResult(new ProcessResult(0, FilterList, string.Empty));
        }

        if (arguments.Contains("-demuxers"))
        {
            return Task.FromResult(new ProcessResult(0, DemuxerList, string.Empty));
        }

        if (ReadsOnly(arguments))
        {
            var length = TimeSpan.FromMilliseconds(ProbedMs);
            return Task.FromResult(new ProcessResult(
                1,
                string.Empty,
                $"Input #0, mp3, from '{arguments[^1]}':\n  Duration: {(int)length.TotalHours:00}:{length.Minutes:00}:{length.Seconds:00}.{length.Milliseconds / 10:00}, start: 0.025057, bitrate: 128 kb/s\nAt least one output file must be specified\n"));
        }

        if (arguments.Contains("loudnorm=print_format=json"))
        {
            return Task.FromResult(new ProcessResult(0, string.Empty, LoudnessTail));
        }

        File.WriteAllBytes(arguments[^1], new byte[1024]);
        return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
    }

    private static bool ReadsOnly(IReadOnlyList<string> arguments) => arguments.Count >= 2 && arguments[^2] == "-i";
}
