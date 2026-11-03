#pragma warning disable SKEXP0001
using Microsoft.Extensions.Time.Testing;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.TextToImage;
using Scout.Operator.Tests.Fakes;

namespace Scout.Operator.Tests;

public sealed class VisualGeneratorTests : IDisposable
{
    private readonly string _media = Directory.CreateTempSubdirectory("scout-visual-").FullName;

    public void Dispose() => Directory.Delete(_media, recursive: true);

    private static VisualGenerator Generator(Kernel kernel, TimeProvider? time = null)
        => new(kernel, Given.Capabilities(kernel), time ?? new FakeTimeProvider(Given.Now));

    [Fact]
    public async Task With_An_Image_Service_The_Picture_Is_Saved_As_Scene_Id_Png_From_The_Scenes_Prompt_With_The_Image_Settings()
    {
        var images = Given.Images([0x89, 0x50, 0x4E, 0x47, 1]);

        var result = await Generator(Given.Media(images: images)).GenerateAsync(Given.Scene("s4", 4), 4, _media, CancellationToken.None);

        result.ShouldBe(new VisualResult("generated", "scene-s4.png", null));
        File.ReadAllBytes(Path.Combine(_media, "scene-s4.png")).ShouldBe(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1 });
        await images.Received(1).GetImageContentsAsync(
            Arg.Is<TextContent>(t => t.Text == "A picture for s4"),
            Arg.Is<PromptExecutionSettings?>(s => s!.ModelId == "image-settings"),
            Arg.Any<Kernel?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_An_Image_Service_The_Scene_Becomes_A_Text_Card_With_No_File()
    {
        var result = await Generator(Given.Media()).GenerateAsync(Given.Scene("s1", 1), 1, _media, CancellationToken.None);

        result.ShouldBe(new VisualResult(
            "text_card",
            null,
            "No picture: the connected AI Source has no image service in Scout; the scene shows a text card, or attach your own image in Scout Desktop, Production."));
        Directory.GetFiles(_media).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_Image_Service_That_Throws_Fails_The_Scene_With_A_Readable_Reason()
    {
        var images = Substitute.For<ITextToImageService>();
        images.GetImageContentsAsync(Arg.Any<TextContent>(), Arg.Any<PromptExecutionSettings?>(), Arg.Any<Kernel?>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ImageContent>>(_ => throw new TimeoutException());

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Generator(Given.Media(images: images)).GenerateAsync(Given.Scene("s2", 2), 2, _media, CancellationToken.None));

        ex.Message.ShouldBe("The AI Source could not make the picture of scene 2; regenerate it in Scout Desktop, Production.");
        Directory.GetFiles(_media).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Picture_Returned_Only_As_A_Link_Is_Refused_And_Never_Fetched()
    {
        var images = Substitute.For<ITextToImageService>();
        images.GetImageContentsAsync(Arg.Any<TextContent>(), Arg.Any<PromptExecutionSettings?>(), Arg.Any<Kernel?>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<ImageContent>)[new ImageContent(new Uri("https://provider.example.test/picture.png"))]);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Generator(Given.Media(images: images)).GenerateAsync(Given.Scene("s2", 2), 2, _media, CancellationToken.None));

        ex.Message.ShouldBe("The AI Source could not make the picture of scene 2; regenerate it in Scout Desktop, Production.");
        Directory.GetFiles(_media).ShouldBeEmpty();
    }

    [Fact]
    public async Task After_40_Pictures_In_A_Pacific_Day_The_Next_Scene_Becomes_A_Text_Card_With_A_Note_Until_Midnight_Pacific()
    {
        var images = Given.Images();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.FromHours(-8)));
        var generator = Generator(Given.Media(images: images), time);
        for (var i = 1; i <= 40; i++)
        {
            (await generator.GenerateAsync(Given.Scene($"a{i}", 1), 1, _media, CancellationToken.None)).VisualSource.ShouldBe("generated");
        }

        var capped = await generator.GenerateAsync(Given.Scene("b1", 1), 1, _media, CancellationToken.None);
        time.Advance(TimeSpan.FromHours(12));
        var nextDay = await generator.GenerateAsync(Given.Scene("c1", 1), 1, _media, CancellationToken.None);

        capped.ShouldBe(new VisualResult(
            "text_card", null, "Scout made today's 40 pictures; this scene shows a text card. Regenerate it after midnight Pacific time."));
        File.Exists(Path.Combine(_media, "scene-b1.png")).ShouldBeFalse();
        nextDay.VisualSource.ShouldBe("generated");
        await images.Received(41).GetImageContentsAsync(
            Arg.Any<TextContent>(), Arg.Any<PromptExecutionSettings?>(), Arg.Any<Kernel?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("throws")]
    [InlineData("no bytes")]
    public async Task A_Picture_The_Provider_Fails_To_Make_Gives_Its_Place_In_The_Daily_40_Back(string failure)
    {
        var images = Substitute.For<ITextToImageService>();
        images.GetImageContentsAsync(Arg.Any<TextContent>(), Arg.Any<PromptExecutionSettings?>(), Arg.Any<Kernel?>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => failure == "throws" ? throw new TimeoutException() : (IReadOnlyList<ImageContent>)[],
                _ => (IReadOnlyList<ImageContent>)[new ImageContent(new byte[] { 0x89, 0x50 }, "image/png")]);
        var generator = Generator(Given.Media(images: images));

        var failed = await Should.ThrowAsync<InvalidOperationException>(
            () => generator.GenerateAsync(Given.Scene("f1", 1), 1, _media, CancellationToken.None));
        var made = new List<string>();
        for (var i = 1; i <= 40; i++)
        {
            made.Add((await generator.GenerateAsync(Given.Scene($"a{i}", 1), 1, _media, CancellationToken.None)).VisualSource);
        }

        failed.Message.ShouldBe("The AI Source could not make the picture of scene 1; regenerate it in Scout Desktop, Production.");
        made.ShouldAllBe(source => source == "generated");
    }

    [Fact]
    public async Task A_Scene_Past_The_30th_Of_A_Video_Becomes_A_Text_Card_With_A_Note_Without_Asking_For_A_Picture()
    {
        var images = Given.Images();

        var result = await Generator(Given.Media(images: images)).GenerateAsync(Given.Scene("s31", 31), 31, _media, CancellationToken.None);

        result.ShouldBe(new VisualResult(
            "text_card", null, "Scout makes pictures for at most 30 scenes of a video; this scene shows a text card."));
        images.ReceivedCalls().ShouldBeEmpty();
    }
}
