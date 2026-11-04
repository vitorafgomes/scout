#pragma warning disable SKEXP0001
using Microsoft.Extensions.Time.Testing;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.TextToImage;
using Scout.Operator.Tests.Fakes;

namespace Scout.Operator.Tests;

public sealed class ThumbnailMakerTests : IDisposable
{
    private readonly string _media = Directory.CreateTempSubdirectory("scout-thumbnail-").FullName;
    private readonly AssembledVideo _video = new("video.mp4", 80_000, [], []);

    public ThumbnailMakerTests() => File.WriteAllBytes(Path.Combine(_media, "video.mp4"), [1]);

    public void Dispose() => Directory.Delete(_media, recursive: true);

    private static (ThumbnailMaker Maker, VisualGenerator Visuals) Maker(Kernel kernel, FakeProcessRunner runner)
    {
        var visuals = new VisualGenerator(kernel, Given.Capabilities(kernel), new FakeTimeProvider(Given.Now));
        return (new ThumbnailMaker(visuals, Given.Toolset(runner)), visuals);
    }

    [Fact]
    public async Task With_An_Image_Service_The_Thumbnail_Is_A_Picture_Of_The_First_Title_Made_With_The_Image_Settings()
    {
        var images = Given.Images([7, 7, 7]);
        var runner = new FakeProcessRunner();

        var file = await Maker(Given.Media(images: images), runner).Maker.MakeAsync(Given.Tools(), _media, "Why rivers bend", _video, CancellationToken.None);

        file.ShouldBe("thumbnail.png");
        File.ReadAllBytes(Path.Combine(_media, "thumbnail.png")).ShouldBe(new byte[] { 7, 7, 7 });
        var call = images.ReceivedCalls().ShouldHaveSingleItem().GetArguments();
        ((TextContent)call[0]!).Text.ShouldBe("A YouTube thumbnail picture for a video titled \"Why rivers bend\": one bold, simple subject, strong contrast, no text, no letters, no logos, no trademarks, no real person's likeness and no public figure.");
        ((PromptExecutionSettings)call[1]!).ModelId.ShouldBe("image-settings");
        Directory.GetFiles(_media).Select(Path.GetFileName).Order().ShouldBe(["thumbnail.png", "thumbnail.txt", "video.mp4"]);
        runner.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Picture_Already_Made_For_The_Same_Title_Is_Used_Again_And_A_New_Title_Asks_The_Image_Service_Again()
    {
        var images = Given.Images([7, 7, 7]);
        var runner = new FakeProcessRunner();
        var maker = Maker(Given.Media(images: images), runner).Maker;

        await maker.MakeAsync(Given.Tools(), _media, "Why rivers bend", _video, CancellationToken.None);
        await maker.MakeAsync(Given.Tools(), _media, "Why rivers bend", _video, CancellationToken.None);

        images.ReceivedCalls().Count().ShouldBe(1);
        File.ReadAllText(Path.Combine(_media, "thumbnail.txt")).ShouldBe("Why rivers bend");

        await maker.MakeAsync(Given.Tools(), _media, "How rivers bend", _video, CancellationToken.None);

        images.ReceivedCalls().Count().ShouldBe(2);
        File.ReadAllText(Path.Combine(_media, "thumbnail.txt")).ShouldBe("How rivers bend");
        runner.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Title_File_That_Cannot_Be_Read_Or_Written_Makes_The_Picture_Again_And_Still_Saves_The_Thumbnail()
    {
        File.WriteAllBytes(Path.Combine(_media, "thumbnail.png"), [1]);
        Directory.CreateDirectory(Path.Combine(_media, "thumbnail.txt"));
        var images = Given.Images([7, 7, 7]);
        var runner = new FakeProcessRunner();

        var file = await Maker(Given.Media(images: images), runner).Maker.MakeAsync(Given.Tools(), _media, "Why rivers bend", _video, CancellationToken.None);

        file.ShouldBe("thumbnail.png");
        images.ReceivedCalls().Count().ShouldBe(1);
        File.ReadAllBytes(Path.Combine(_media, "thumbnail.png")).ShouldBe(new byte[] { 7, 7, 7 });
        runner.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Frame_Thumbnail_Is_Never_Taken_For_A_Picture_And_Is_Taken_Again_On_The_Next_Assembly()
    {
        File.WriteAllBytes(Path.Combine(_media, "thumbnail.png"), [7]);
        File.WriteAllText(Path.Combine(_media, "thumbnail.txt"), "Why rivers bend");
        var runner = new FakeProcessRunner();
        var maker = Maker(Given.Media(), runner).Maker;

        await maker.MakeAsync(Given.Tools(), _media, "How rivers bend", _video, CancellationToken.None);
        await maker.MakeAsync(Given.Tools(), _media, "How rivers bend", _video, CancellationToken.None);

        runner.Renders.Count.ShouldBe(2);
        File.Exists(Path.Combine(_media, "thumbnail.txt")).ShouldBeFalse();
    }

    [Fact]
    public async Task Without_An_Image_Service_The_Thumbnail_Is_The_Frame_At_A_Tenth_Of_The_Video()
    {
        var runner = new FakeProcessRunner();

        await Maker(Given.Media(), runner).Maker.MakeAsync(Given.Tools(), _media, "Why rivers bend", _video, CancellationToken.None);

        var frame = runner.Renders.ShouldHaveSingleItem();
        frame.Take(9).ShouldBe(["-hide_banner", "-nostdin", "-y", "-ss", "8.000", "-i", Path.Combine(_media, "video.mp4"), "-frames:v", "1"]);
        ThumbnailMaker.FrameArguments("/media/p1/video.mp4", 8000, "/media/p1/out.png")
            .ShouldBe(["-hide_banner", "-nostdin", "-y", "-ss", "8.000", "-i", "/media/p1/video.mp4", "-frames:v", "1", "/media/p1/out.png"]);
        File.Exists(Path.Combine(_media, "thumbnail.png")).ShouldBeTrue();
    }

    [Fact]
    public async Task An_Image_Service_That_Fails_Gives_The_Frame_Instead_And_Its_Place_In_The_Days_Pictures_Back()
    {
        var images = Substitute.For<ITextToImageService>();
        images.GetImageContentsAsync(default!, default, default, default)
            .ReturnsForAnyArgs<Task<IReadOnlyList<ImageContent>>>(_ => throw new HttpRequestException("503 Service Unavailable"));
        var runner = new FakeProcessRunner();
        var (maker, visuals) = Maker(Given.Media(images: images), runner);

        await maker.MakeAsync(Given.Tools(), _media, "Why rivers bend", _video, CancellationToken.None);

        runner.Renders.ShouldHaveSingleItem().ShouldContain("-frames:v");
        File.Exists(Path.Combine(_media, "thumbnail.png")).ShouldBeTrue();
        for (var i = 0; i < VisualGenerator.PerDay; i++)
        {
            (await visuals.ThumbnailAsync("again", _media, "other.png", CancellationToken.None)).ShouldBeFalse();
        }

        images.ReceivedCalls().Count().ShouldBe(VisualGenerator.PerDay + 1);
    }

    [Fact]
    public async Task Once_The_Days_40_Pictures_Are_Made_The_Thumbnail_Is_The_Frame()
    {
        var images = Given.Images();
        var runner = new FakeProcessRunner();
        var (maker, visuals) = Maker(Given.Media(images: images), runner);
        for (var i = 0; i < VisualGenerator.PerDay; i++)
        {
            (await visuals.ThumbnailAsync("spend", _media, "spent.png", CancellationToken.None)).ShouldBeTrue();
        }

        await maker.MakeAsync(Given.Tools(), _media, "Why rivers bend", _video, CancellationToken.None);

        images.ReceivedCalls().Count().ShouldBe(VisualGenerator.PerDay);
        runner.Renders.ShouldHaveSingleItem().ShouldContain("-frames:v");
    }
}
