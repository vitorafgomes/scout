using Microsoft.Extensions.Logging;
using Scout.Operator.Tests.Fakes;

namespace Scout.Operator.Tests;

public sealed class FaceMouthTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("scout-face-").FullName;

    public FaceMouthTests()
    {
        Models = Directory.CreateDirectory(Path.Combine(_root, "face")).FullName;
        Media = Directory.CreateDirectory(Path.Combine(_root, "media")).FullName;
        File.WriteAllBytes(Path.Combine(Models, FaceMouth.DetectorFile), [1]);
        File.WriteAllBytes(Path.Combine(Models, FaceMouth.MeshFile), [1]);
        File.WriteAllBytes(Picture, [0x89, 0x50, 0x4E, 0x47]);
    }

    private string Models { get; }

    private string Media { get; }

    private string Picture => Path.Combine(Media, "presenter-0a1b2c3d4e5f.png");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    internal sealed class FakeOnnx : IOnnxRunner
    {
        public Func<string, IReadOnlyDictionary<string, float[]>> Answer { get; init; } = _ => throw new InvalidOperationException("no answer");

        public List<(string Model, string Input, long[] Shape, float[] Data)> Runs { get; } = [];

        public void Load(string modelPath) => Answer(modelPath);

        public IReadOnlyDictionary<string, float[]> Run(string modelPath, string inputName, float[] input, long[] shape)
        {
            Runs.Add((Path.GetFileName(modelPath), inputName, shape, input));
            return Answer(modelPath);
        }
    }

    private static int StrideEight(int x, int y, int anchor) => (((y * 16) + x) * 2) + anchor;

    private static float[] Detections(params (double X, double Y, double Size)[] faces)
    {
        var boxes = new float[896 * 16];
        var scores = Enumerable.Repeat(-10f, 896).ToArray();
        foreach (var (fx, fy, size) in faces)
        {
            var cell = (int)(fx * 16);
            var row = (int)(fy * 16);
            foreach (var (x, y) in new[] { (cell - 1, row - 1), (cell, row - 1), (cell - 1, row), (cell, row) })
            {
                for (var anchor = 0; anchor < 2; anchor++)
                {
                    var i = StrideEight(x, y, anchor);
                    boxes[i * 16] = (float)((fx - ((x + 0.5) / 16)) * 128);
                    boxes[(i * 16) + 1] = (float)((fy - ((y + 0.5) / 16)) * 128);
                    boxes[(i * 16) + 2] = (float)(size * 128);
                    boxes[(i * 16) + 3] = (float)(size * 128);
                    scores[i] = 2f + (0.1f * anchor);
                }
            }
        }

        return [.. boxes, .. scores];
    }

    private static Dictionary<string, float[]> Detector(float[] both)
        => new Dictionary<string, float[]> { ["regressors"] = both[..(896 * 16)], ["classificators"] = both[(896 * 16)..] };

    private static Dictionary<string, float[]> Mesh(float presence = 3f)
    {
        var points = Enumerable.Repeat(128f, 1434).ToArray();
        int[] upper = [61, 185, 40, 39, 37, 0, 267, 269, 270, 409, 291];
        int[] lower = [146, 91, 181, 84, 17, 314, 405, 321, 375];
        foreach (var (index, n) in upper.Select((index, n) => (index, n)))
        {
            points[index * 3] = 96 + (64f * n / 10);
            points[(index * 3) + 1] = n is 0 or 10 ? 188 : 176;
        }

        foreach (var (index, n) in lower.Select((index, n) => (index, n)))
        {
            points[index * 3] = 100 + (56f * n / 8);
            points[(index * 3) + 1] = 200;
        }

        return new Dictionary<string, float[]> { ["Identity"] = points, ["Identity_1"] = [presence], ["Identity_2"] = [0] };
    }

    private FakeProcessRunner Runner(int width = 1024, int height = 768, int exit = 0)
        => new()
        {
            Answer = (_, arguments) => arguments.Count >= 2 && arguments[^2] == "-i" && arguments[^1] == Picture
                ? new ProcessResult(1, string.Empty, $"Input #0, png_pipe, from '{Picture}':\n  Stream #0:0: Video: png, rgba(pc), {width}x{height}, 25 tbr\n")
                : arguments.Contains("rawvideo")
                    ? new ProcessResult(exit, string.Empty, exit == 0 ? string.Empty : "Invalid data found when processing input\n")
                    {
                        OutputBytes = new byte[arguments[arguments.ToList().IndexOf("-vf") + 1].StartsWith("crop", StringComparison.Ordinal) ? 256 * 256 * 3 : 128 * 128 * 3],
                    }
                    : null,
        };

    private FaceMouth Finder(FakeProcessRunner runner, IOnnxRunner onnx, ILogger<FaceMouth>? logger = null, string? models = null)
        => new(
            Given.Toolset(runner),
            new MediaProbe(runner),
            onnx,
            Microsoft.Extensions.Options.Options.Create(new OperatorMediaOptions { FaceModelsPath = models ?? Models }),
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<FaceMouth>.Instance);

    private static FakeOnnx Answering(float[] detections, float presence = 3f)
        => new() { Answer = model => model.EndsWith(FaceMouth.DetectorFile, StringComparison.Ordinal) ? Detector(detections) : Mesh(presence) };

    [Fact]
    public async Task A_Detector_Box_Without_A_Positive_Area_Is_Dropped_And_The_Search_Ends()
    {
        var onnx = Answering(Detections((0.5, 0.5, 0)));

        var box = await Task.Run(() => Finder(Runner(), onnx).FindAsync(Picture, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(10));

        box.ShouldBeNull();

        var nanCentre = Detections((0.5, 0.5, 0.25));
        var lone = Array.FindIndex(nanCentre[(896 * 16)..], score => score > 0);
        for (var i = 0; i < 896; i++)
        {
            nanCentre[(896 * 16) + i] = i == lone ? 2f : -10f;
        }

        nanCentre[lone * 16] = float.NaN;

        var dropped = await Task.Run(() => Finder(Runner(), Answering(nanCentre)).FindAsync(Picture, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(10));

        dropped.ShouldBeNull();
    }

    [Fact]
    public async Task One_Face_Gives_The_Padded_Lip_Box_In_Fractions_Of_The_Picture()
    {
        var runner = Runner();
        var onnx = Answering(Detections((0.5, 0.5, 0.25)));

        var box = await Finder(runner, onnx).FindAsync(Picture, CancellationToken.None);

        var mouth = box.ShouldNotBeNull();
        mouth.X.ShouldBe(449.6 / 1024, 1e-6);
        mouth.Y.ShouldBe(450.6 / 768, 1e-6);
        mouth.Width.ShouldBe(124.8 / 1024, 1e-6);
        mouth.Height.ShouldBe(46.8 / 768, 1e-6);
        var decodes = runner.Calls.Where(c => c.Arguments.Contains("rawvideo")).Select(c => c.Arguments).ToList();
        decodes.ShouldBe([
            ["-hide_banner", "-nostdin", "-i", Picture, "-vf", "scale=128:128:force_original_aspect_ratio=decrease,pad=128:128:(ow-iw)/2:(oh-ih)/2:black",
                "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"],
            ["-hide_banner", "-nostdin", "-i", Picture, "-vf", "crop=384:384:320:192,scale=256:256", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"],
        ]);
        onnx.Runs.Select(r => (r.Model, r.Input, string.Join(",", r.Shape), r.Data.Length)).ShouldBe([
            ("blazeface-short-range.onnx", "input", "1,128,128,3", 128 * 128 * 3),
            ("face-landmarker.onnx", "input_12", "1,256,256,3", 256 * 256 * 3),
        ]);
        (onnx.Runs[0].Data[0], onnx.Runs[1].Data[0]).ShouldBe((-1f, 0f));
    }

    [Theory]
    [InlineData("no face")]
    [InlineData("two faces")]
    [InlineData("no presence")]
    [InlineData("runtime throws")]
    [InlineData("decode fails")]
    public async Task No_Face_Or_Two_Faces_Or_A_Failing_Runtime_Gives_Null(string how)
    {
        var runner = Runner(exit: how == "decode fails" ? 1 : 0);
        var logger = new RecordingLogger<FaceMouth>();
        IOnnxRunner onnx = how switch
        {
            "no face" => Answering(Detections()),
            "two faces" => Answering(Detections((0.5, 0.5, 0.25), (0.2, 0.2, 0.2))),
            "no presence" => Answering(Detections((0.5, 0.5, 0.25)), presence: -3f),
            "runtime throws" => new FakeOnnx { Answer = _ => throw new DllNotFoundException("onnxruntime") },
            _ => Answering(Detections((0.5, 0.5, 0.25))),
        };
        var finder = Finder(runner, onnx, logger);

        var first = await finder.FindAsync(Picture, CancellationToken.None);
        var second = await finder.FindAsync(Picture, CancellationToken.None);

        (first, second).ShouldBe(((OperatorMouthBox?)null, (OperatorMouthBox?)null));
        logger.Entries.Count.ShouldBe(how is "runtime throws" or "decode fails" ? 1 : 0);
    }

    [Fact]
    public async Task A_Missing_Model_Gives_Null_Without_Running_Anything_And_Is_Logged_Once()
    {
        File.Delete(Path.Combine(Models, FaceMouth.MeshFile));
        var runner = Runner();
        var onnx = Answering(Detections((0.5, 0.5, 0.25)));
        var logger = new RecordingLogger<FaceMouth>();
        var finder = Finder(runner, onnx, logger);

        var first = await finder.FindAsync(Picture, CancellationToken.None);
        var second = await finder.FindAsync(Picture, CancellationToken.None);
        var available = await finder.AvailableAsync(CancellationToken.None);

        (first, second, available).ShouldBe(((OperatorMouthBox?)null, (OperatorMouthBox?)null, false));
        onnx.Runs.ShouldBeEmpty();
        runner.Calls.ShouldNotContain(c => c.Arguments.Contains("rawvideo"));
        logger.Entries.ShouldHaveSingleItem().Message.ShouldContain("the face models were not found");
    }

    [Fact]
    public async Task Models_That_The_Real_Runtime_Cannot_Load_Give_Null_And_No_Detection()
    {
        using var runtime = new OnnxRunner();
        var logger = new RecordingLogger<FaceMouth>();
        var finder = Finder(Runner(), runtime, logger);

        var box = await finder.FindAsync(Picture, CancellationToken.None);
        var available = await finder.AvailableAsync(CancellationToken.None);

        (box, available).ShouldBe(((OperatorMouthBox?)null, false));
        logger.Entries.Count.ShouldBe(1);
    }

    [Fact]
    public void The_Anchors_Are_896_Two_Per_Cell_Of_16_Then_Six_Per_Cell_Of_8()
    {
        var anchors = FaceMouth.Anchors();

        anchors.Count.ShouldBe(896);
        (anchors[0], anchors[1], anchors[2]).ShouldBe(((1 / 32f, 1 / 32f), (1 / 32f, 1 / 32f), (3 / 32f, 1 / 32f)));
        (anchors[512], anchors[517], anchors[518]).ShouldBe(((1 / 16f, 1 / 16f), (1 / 16f, 1 / 16f), (3 / 16f, 1 / 16f)));
    }

    [Fact]
    public void A_Crop_Past_The_Edge_Of_The_Picture_Is_Kept_Inside_It()
        => FaceMouth.Roi((0.0, 0.0, 0.5, 0.5), 1024, 768).ShouldBe((0, 0, 768));
}
