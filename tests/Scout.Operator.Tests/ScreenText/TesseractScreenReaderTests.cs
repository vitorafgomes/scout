using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.Conversation.Abstractions;
using Scout.Operator.ScreenText;
using Scout.Operator.Tests.Fakes;

namespace Scout.Operator.Tests.ScreenText;

public sealed class TesseractScreenReaderTests : IDisposable
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0xFF, 0xD9];

    private readonly string _root = Directory.CreateTempSubdirectory("scout-tesseract-").FullName;
    private readonly string _tesseract;
    private readonly string _tessdata;

    public TesseractScreenReaderTests()
    {
        _tesseract = Path.Combine(_root, "tesseract");
        File.WriteAllBytes(_tesseract, [1]);
        _tessdata = Directory.CreateDirectory(Path.Combine(_root, "tessdata")).FullName;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static FakeProcessRunner Answering(ProcessResult result) => new() { Answer = (_, _) => result };

    private TesseractScreenReader Reader(FakeProcessRunner runner, string locale = "en")
        => new(runner, new ToolsOptions { TesseractPath = _tesseract, TessdataPath = _tessdata, Locale = locale });

    [Fact]
    public async Task The_Reader_Runs_Tesseract_On_The_Bytes_With_Psm_6_And_Answers_The_Trimmed_Text()
    {
        var runner = Answering(new ProcessResult(0, "  Build 42 failed\nexit code 1\n\n\f", string.Empty));

        var text = await Reader(runner).ReadAsync(Jpeg, CancellationToken.None);

        text.ShouldBe("Build 42 failed\nexit code 1");
        var (program, arguments) = runner.Calls.ShouldHaveSingleItem();
        program.ShouldBe(_tesseract);
        arguments.ShouldBe(["stdin", "stdout", "--psm", "6", "-l", "eng", "--tessdata-dir", _tessdata]);
        runner.InputBytes.ShouldBe([Jpeg]);
        runner.Inputs.ShouldBe([null]);
        runner.Timeouts.ShouldBe([TimeSpan.FromSeconds(3)]);
    }

    [Theory]
    [InlineData("pt-BR", "por+eng")]
    [InlineData("pt", "por+eng")]
    [InlineData("en-GB", "eng")]
    [InlineData("", "eng")]
    public async Task The_Languages_Follow_The_Locale(string locale, string languages)
    {
        var runner = Answering(new ProcessResult(0, "Olá", string.Empty));

        await Reader(runner, locale).ReadAsync(Jpeg, CancellationToken.None);

        var arguments = runner.Calls.ShouldHaveSingleItem().Arguments;
        (arguments[4], arguments[5]).ShouldBe(("-l", languages));
    }

    [Fact]
    public async Task The_Text_Is_Cut_At_5000_Characters_And_Control_Characters_Become_Spaces()
    {
        var runner = Answering(new ProcessResult(0, "a\tb\rc\u0007d\ne" + new string('x', 6000), string.Empty));

        var text = await Reader(runner).ReadAsync(Jpeg, CancellationToken.None);

        text.Length.ShouldBe(5000);
        text.ShouldStartWith("a b c d\nex");
        text[9..].ShouldAllBe(c => c == 'x');
    }

    [Theory]
    [InlineData("slow", "tesseract ran longer than 3 seconds and was stopped.")]
    [InlineData("failing", "tesseract exited with code 1 (Error opening data file tessdata/por.traineddata).")]
    [InlineData("silent", "tesseract found no text on the screenshot.")]
    public async Task A_Slow_Failing_Or_Silent_Program_Throws(string kind, string message)
    {
        var runner = new FakeProcessRunner
        {
            Answer = (_, _) => kind switch
            {
                "slow" => throw new InvalidOperationException("tesseract ran longer than 3 seconds and was stopped."),
                "failing" => new ProcessResult(1, "Secret line on screen", "Tesseract Open Source OCR Engine\nError opening data file tessdata/por.traineddata\n"),
                _ => new ProcessResult(0, " \n\f\u0007 ", string.Empty),
            },
        };

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Reader(runner, "pt-BR").ReadAsync(Jpeg, CancellationToken.None));

        ex.Message.ShouldBe(message);
        runner.Timeouts.ShouldBe([TimeSpan.FromSeconds(3)]);
    }

    [Fact]
    public async Task The_Reader_Writes_No_File()
    {
        var runner = Answering(new ProcessResult(0, "Deploy approved", string.Empty));
        var before = Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories).Order().ToList();

        await Reader(runner).ReadAsync(Jpeg, CancellationToken.None);

        runner.InputBytes.ShouldBe([Jpeg]);
        var arguments = runner.Calls.ShouldHaveSingleItem().Arguments;
        arguments.Where(a => a != _tessdata).ShouldAllBe(a => !Path.IsPathRooted(a) && !a.Contains('/', StringComparison.Ordinal));
        (arguments[0], arguments[1]).ShouldBe(("stdin", "stdout"));
        Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories).Order().ShouldBe(before);
    }

    [Theory]
    [InlineData("tesseract", "tessdata", true)]
    [InlineData("missing-tesseract", "tessdata", false)]
    [InlineData("tesseract", "missing-tessdata", false)]
    [InlineData("tesseract", "tesseract", false)]
    [InlineData("", "tessdata", false)]
    [InlineData("tesseract", "", false)]
    public void AddScreenText_Registers_The_Reader_Only_When_Both_Paths_Exist(string tesseract, string tessdata, bool registered)
    {
        using var provider = new ServiceCollection().AddScreenText(Tools(tesseract, tessdata)).BuildServiceProvider();

        var reader = provider.GetService<IScreenTextReader>();

        (reader is TesseractScreenReader).ShouldBe(registered);
        (reader is null).ShouldBe(!registered);
    }

    [Fact]
    public async Task The_Configured_Locale_Reaches_The_Registered_Reader()
    {
        var runner = Answering(new ProcessResult(0, "Olá", string.Empty));
        using var provider = new ServiceCollection()
            .AddSingleton<IProcessRunner>(runner)
            .AddScreenText(Tools("tesseract", "tessdata", "pt-BR"))
            .BuildServiceProvider();

        await provider.GetRequiredService<IScreenTextReader>().ReadAsync(Jpeg, CancellationToken.None);

        runner.Calls.ShouldHaveSingleItem().Arguments.ShouldBe(["stdin", "stdout", "--psm", "6", "-l", "por+eng", "--tessdata-dir", _tessdata]);
    }

    private IConfiguration Tools(string tesseract, string tessdata, string? locale = null)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Tools:TesseractPath"] = tesseract.Length == 0 ? string.Empty : Path.Combine(_root, tesseract),
                ["Scout:Tools:TessdataPath"] = tessdata.Length == 0 ? string.Empty : Path.Combine(_root, tessdata),
                ["Scout:Tools:Locale"] = locale,
            })
            .Build();
}
