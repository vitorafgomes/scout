using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;

namespace Scout.Operator;

/// <summary>Finds the mouth on a presenter picture.</summary>
internal interface IFaceMouth
{
    /// <summary>Whether the desktop named a face models folder, usable or not.</summary>
    bool Configured { get; }

    /// <summary>Whether both face models are in the folder and load in the runtime.</summary>
    Task<bool> AvailableAsync(CancellationToken ct);

    /// <summary>
    /// The box around the lips of the one face on the picture at <paramref name="picturePath"/>, in fractions of the picture;
    /// null when there is no face, more than one, or the models or the decoding fail.
    /// </summary>
    Task<OperatorMouthBox?> FindAsync(string picturePath, CancellationToken ct);
}

/// <summary>Runs an ONNX model on one float tensor.</summary>
internal interface IOnnxRunner
{
    /// <summary>Load the model at <paramref name="modelPath"/> once; a model that cannot load throws.</summary>
    void Load(string modelPath);

    /// <summary>Run the model at <paramref name="modelPath"/> on <paramref name="input"/> of <paramref name="shape"/> as <paramref name="inputName"/>; every output by name, flattened.</summary>
    IReadOnlyDictionary<string, float[]> Run(string modelPath, string inputName, float[] input, long[] shape);
}

/// <summary>
/// The mouth on a presenter picture, found on this machine with two models of the MediaPipe Face Landmarker in the local ONNX
/// runtime, nothing over the network. The picture's size comes from <see cref="MediaProbe"/>; ffmpeg decodes it to raw RGB on
/// its standard output, letterboxed to 128x128 for <see cref="DetectorFile"/>, BlazeFace short-range, whose
/// <see cref="Anchors"/> boxes are decoded and merged by weighted non-maximum suppression; exactly one face scoring above
/// <see cref="MinScore"/> is kept, its box made square on its long side, scaled by <see cref="RoiScale"/>, kept inside the
/// picture and decoded again at 256x256 for <see cref="MeshFile"/>, whose 478 points give the <see cref="OuterLips"/>; their
/// bounding box padded by <see cref="Padding"/> of its size on each side is the mouth. The face's rotation is not used, so a
/// tilted head gets the box of its tilted lips. A missing model, a runtime that fails or throws and a decoding that fails
/// answer null and are logged once.
/// </summary>
internal sealed class FaceMouth(
    FfmpegToolset ffmpeg, MediaProbe probe, IOnnxRunner onnx, IOptions<OperatorMediaOptions> options, ILogger<FaceMouth> logger) : IFaceMouth
{
    /// <summary>The face detector's file in the face models folder.</summary>
    internal const string DetectorFile = "blazeface-short-range.onnx";

    /// <summary>The face mesh's file in the face models folder.</summary>
    internal const string MeshFile = "face-landmarker.onnx";

    /// <summary>The detector's input: float32 [1,128,128,3], RGB scaled to -1..1.</summary>
    internal const string DetectorInput = "input";

    /// <summary>The detector's boxes: float32 [1,896,16], centre x, centre y, width and height, then six keypoints, in input pixels relative to the anchor.</summary>
    internal const string DetectorBoxes = "regressors";

    /// <summary>The detector's scores: float32 [1,896,1], logits.</summary>
    internal const string DetectorScores = "classificators";

    /// <summary>The mesh's input: float32 [1,256,256,3], RGB scaled to 0..1, a crop around the face.</summary>
    internal const string MeshInput = "input_12";

    /// <summary>The mesh's points: float32 [1,1,1,1434], 478 points of x, y and z in input pixels.</summary>
    internal const string MeshPoints = "Identity";

    /// <summary>The mesh's face presence: float32 [1,1,1,1], a logit.</summary>
    internal const string MeshPresence = "Identity_1";

    internal const int DetectorSize = 128;

    internal const int MeshSize = 256;

    internal const int AnchorCount = 896;

    internal const double MinScore = 0.5;

    internal const double MinOverlap = 0.3;

    internal const double RoiScale = 1.5;

    internal const double Padding = 0.15;

    /// <summary>The MediaPipe face mesh's outer lip points, the <c>lipsUpperOuter</c> and <c>lipsLowerOuter</c> groups, 291 shared.</summary>
    internal static readonly int[] OuterLips =
        [61, 185, 40, 39, 37, 0, 267, 269, 270, 409, 291, 146, 91, 181, 84, 17, 314, 405, 321, 375];

    private bool _logged;

    public bool Configured => !string.IsNullOrWhiteSpace(options.Value.FaceModelsPath);

    private string Model(string file) => Path.Combine(options.Value.FaceModelsPath.Trim(), file);

    public Task<bool> AvailableAsync(CancellationToken ct)
        => Task.FromResult(Configured && File.Exists(Model(DetectorFile)) && File.Exists(Model(MeshFile)) && Loads());

    public async Task<OperatorMouthBox?> FindAsync(string picturePath, CancellationToken ct)
    {
        if (!Configured || !File.Exists(Model(DetectorFile)) || !File.Exists(Model(MeshFile)))
        {
            Log("the face models were not found");
            return null;
        }

        if ((await ffmpeg.ProbeAsync(ct).ConfigureAwait(false)).Tools is not { } tools)
        {
            Log("ffmpeg is not available");
            return null;
        }

        try
        {
            var directory = Path.GetDirectoryName(picturePath)!;
            var file = Path.GetFileName(picturePath);
            var size = await probe.PictureSizeAsync(tools, directory, file, ct).ConfigureAwait(false);
            if (size is not var (width, height))
            {
                Log("ffmpeg reported no picture size");
                return null;
            }

            var letterbox = await DecodeAsync(tools, MediaFiles.PathFor(directory, file), DetectorFilter, DetectorSize, ct).ConfigureAwait(false);
            var detected = onnx.Run(Model(DetectorFile), DetectorInput, [.. letterbox.Select(b => (b / 127.5f) - 1f)], [1, DetectorSize, DetectorSize, 3]);
            var faces = Faces(detected[DetectorBoxes], detected[DetectorScores]);
            if (faces.Count != 1)
            {
                return null;
            }

            var roi = Roi(faces[0], width, height);
            var crop = await DecodeAsync(tools, MediaFiles.PathFor(directory, file), CropFilter(roi), MeshSize, ct).ConfigureAwait(false);
            var mesh = onnx.Run(Model(MeshFile), MeshInput, [.. crop.Select(b => b / 255f)], [1, MeshSize, MeshSize, 3]);
            return Sigmoid(mesh[MeshPresence][0]) < MinScore ? null : Lips(mesh[MeshPoints], roi, width, height);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log(ex is InvalidOperationException ? ex.Message : ex.GetType().Name);
            return null;
        }
    }

    internal const string DetectorFilter =
        "scale=128:128:force_original_aspect_ratio=decrease,pad=128:128:(ow-iw)/2:(oh-ih)/2:black";

    internal static string CropFilter((int X, int Y, int Side) roi)
        => string.Create(CultureInfo.InvariantCulture, $"crop={roi.Side}:{roi.Side}:{roi.X}:{roi.Y},scale=256:256");

    /// <summary>
    /// The anchors of BlazeFace short-range, centres as fractions of the input: strides 8, 16, 16 and 16, two anchors per
    /// layer and cell, the layers of one stride sharing their cells, 896 in all.
    /// </summary>
    internal static IReadOnlyList<(float X, float Y)> Anchors()
    {
        int[] strides = [8, 16, 16, 16];
        var anchors = new List<(float X, float Y)>(AnchorCount);
        for (var layer = 0; layer < strides.Length;)
        {
            var last = layer;
            while (last < strides.Length && strides[last] == strides[layer])
            {
                last++;
            }

            var cells = (int)Math.Ceiling((double)DetectorSize / strides[layer]);
            for (var y = 0; y < cells; y++)
            {
                for (var x = 0; x < cells; x++)
                {
                    for (var n = 0; n < 2 * (last - layer); n++)
                    {
                        anchors.Add(((x + 0.5f) / cells, (y + 0.5f) / cells));
                    }
                }
            }

            layer = last;
        }

        return anchors;
    }

    /// <summary>The faces the detector found, as boxes of fractions of the letterboxed input, after weighted non-maximum suppression.</summary>
    internal static List<(double Left, double Top, double Right, double Bottom)> Faces(float[] boxes, float[] scores)
    {
        var anchors = Anchors();
        var found = new List<(double Score, (double Left, double Top, double Right, double Bottom) Box)>();
        for (var i = 0; i < AnchorCount; i++)
        {
            var score = Sigmoid(Math.Clamp(scores[i], -100f, 100f));
            if (score <= MinScore)
            {
                continue;
            }

            var raw = boxes.AsSpan(i * 16, 4);
            var (cx, cy) = ((raw[0] / DetectorSize) + anchors[i].X, (raw[1] / DetectorSize) + anchors[i].Y);
            var (w, h) = (raw[2] / (double)DetectorSize, raw[3] / (double)DetectorSize);
            if (!double.IsFinite(cx) || !double.IsFinite(cy) || !double.IsFinite(w) || !double.IsFinite(h) || w <= 0 || h <= 0)
            {
                continue;
            }

            found.Add((score, (cx - (w / 2), cy - (h / 2), cx + (w / 2), cy + (h / 2))));
        }

        var remaining = found.OrderByDescending(f => f.Score).ToList();
        var faces = new List<(double, double, double, double)>();
        while (remaining.Count > 0)
        {
            var top = remaining[0].Box;
            var cluster = remaining.Where(f => Overlap(top, f.Box) > MinOverlap).ToList();
            var total = cluster.Sum(f => f.Score);
            faces.Add((
                cluster.Sum(f => f.Score * f.Box.Left) / total,
                cluster.Sum(f => f.Score * f.Box.Top) / total,
                cluster.Sum(f => f.Score * f.Box.Right) / total,
                cluster.Sum(f => f.Score * f.Box.Bottom) / total));
            remaining = [.. remaining.Skip(1).Except(cluster)];
        }

        return faces;
    }

    /// <summary>
    /// The square crop around <paramref name="face"/> in the picture's pixels: the face mapped back through the letterbox
    /// ffmpeg drew, square on its long side, scaled by <see cref="RoiScale"/> around its centre and kept inside the picture.
    /// </summary>
    internal static (int X, int Y, int Side) Roi((double Left, double Top, double Right, double Bottom) face, int width, int height)
    {
        var fitted = Fitted(width, height);
        var (padX, padY) = ((DetectorSize - fitted.Width) / 2, (DetectorSize - fitted.Height) / 2);
        double X(double fraction) => ((fraction * DetectorSize) - padX) * width / fitted.Width;
        double Y(double fraction) => ((fraction * DetectorSize) - padY) * height / fitted.Height;
        var (left, top, right, bottom) = (X(face.Left), Y(face.Top), X(face.Right), Y(face.Bottom));
        var side = (int)Math.Min(Math.Max(right - left, bottom - top) * RoiScale, Math.Min(width, height));
        var x = (int)Math.Clamp(((left + right) / 2) - (side / 2.0), 0, width - side);
        var y = (int)Math.Clamp(((top + bottom) / 2) - (side / 2.0), 0, height - side);
        return (x, y, side);
    }

    /// <summary>
    /// The box around <see cref="OuterLips"/> of <paramref name="points"/>, in the crop's input pixels, mapped back to the
    /// picture, padded by <see cref="Padding"/> of its size on each side and given in fractions of the picture, at least 0.02
    /// wide and high and inside it.
    /// </summary>
    internal static OperatorMouthBox Lips(float[] points, (int X, int Y, int Side) roi, int width, int height)
    {
        var per = roi.Side / (double)MeshSize;
        var xs = OuterLips.Select(i => roi.X + (points[i * 3] * per)).ToList();
        var ys = OuterLips.Select(i => roi.Y + (points[(i * 3) + 1] * per)).ToList();
        var (padX, padY) = ((xs.Max() - xs.Min()) * Padding, (ys.Max() - ys.Min()) * Padding);
        var (left, w) = Span((xs.Min() - padX) / width, (xs.Max() + padX) / width);
        var (top, h) = Span((ys.Min() - padY) / height, (ys.Max() + padY) / height);
        return new OperatorMouthBox(left, top, w, h);
    }

    private static (double Start, double Length) Span(double from, double to)
    {
        var length = Math.Max(0.02, Math.Min(1, to) - Math.Max(0, from));
        return (Math.Clamp(from, 0, 1 - length), length);
    }

    private static (int Width, int Height) Fitted(int width, int height)
        => (Math.Min(DetectorSize, (int)Math.Round((double)DetectorSize * width / height, MidpointRounding.AwayFromZero)),
            Math.Min(DetectorSize, (int)Math.Round((double)DetectorSize * height / width, MidpointRounding.AwayFromZero)));

    private static double Overlap((double Left, double Top, double Right, double Bottom) a, (double Left, double Top, double Right, double Bottom) b)
    {
        var w = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        var h = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        var shared = w * h;
        var union = ((a.Right - a.Left) * (a.Bottom - a.Top)) + ((b.Right - b.Left) * (b.Bottom - b.Top)) - shared;
        return union <= 0 ? 0 : shared / union;
    }

    private static double Sigmoid(float logit) => 1 / (1 + Math.Exp(-logit));

    private async Task<byte[]> DecodeAsync(FfmpegTools tools, string path, string filter, int size, CancellationToken ct)
    {
        var result = await ffmpeg.RunAsync(
            tools,
            ["-hide_banner", "-nostdin", "-i", path, "-vf", filter, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"],
            0,
            DecodeFailed,
            ct,
            rawOutput: true).ConfigureAwait(false);
        return result.OutputBytes.Length == size * size * 3
            ? result.OutputBytes
            : throw new InvalidOperationException($"{DecodeFailed} (ffmpeg wrote {result.OutputBytes.Length} bytes)");
    }

    internal const string DecodeFailed = "Scout could not decode the presenter picture for face detection";

    private bool Loads()
    {
        try
        {
            onnx.Load(Model(DetectorFile));
            onnx.Load(Model(MeshFile));
            return true;
        }
        catch (Exception ex)
        {
            Log(ex.GetType().Name);
            return false;
        }
    }

    private void Log(string reason)
    {
        if (_logged)
        {
            return;
        }

        _logged = true;
        logger.LogWarning("Scout could not look for the mouth on the presenter picture: {Reason}", reason);
    }
}

/// <summary><see cref="IOnnxRunner"/> over ONNX Runtime on the CPU, one session per model kept for the life of the process.</summary>
internal sealed class OnnxRunner : IOnnxRunner, IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, InferenceSession> _sessions = new(StringComparer.Ordinal);

    public void Load(string modelPath) => Session(modelPath);

    public IReadOnlyDictionary<string, float[]> Run(string modelPath, string inputName, float[] input, long[] shape)
    {
        var session = Session(modelPath);
        using var options = new RunOptions();
        using var tensor = OrtValue.CreateTensorValueFromMemory(input, shape);
        using var outputs = session.Run(options, new Dictionary<string, OrtValue> { [inputName] = tensor }, session.OutputNames);
        return session.OutputNames
            .Zip(outputs, (name, value) => (name, value.GetTensorDataAsSpan<float>().ToArray()))
            .ToDictionary(o => o.name, o => o.Item2, StringComparer.Ordinal);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var session in _sessions.Values)
            {
                session.Dispose();
            }

            _sessions.Clear();
        }
    }

    private InferenceSession Session(string modelPath)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(modelPath, out var session))
            {
                session = new InferenceSession(modelPath);
                _sessions[modelPath] = session;
            }

            return session;
        }
    }
}
