namespace Scout.Operator.Tests;

public sealed class BuiltInPresentersTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("scout-presenters-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    internal static string Folder(string root, string? list = null)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, "presenters")).FullName;
        foreach (var id in new[] { "ada", "bo", "cy", "di" })
        {
            File.WriteAllBytes(Path.Combine(folder, $"{id}.png"), [0x89, 0x50, 0x4E, 0x47, (byte)id[0]]);
        }

        File.WriteAllText(Path.Combine(folder, BuiltInPresenters.ListFile), list ?? """
            [
              { "id": "ada", "label": "Ada, the baker", "file": "ada.png", "mouthBox": { "x": 0.42, "y": 0.61, "width": 0.16, "height": 0.08 } },
              { "id": "bo", "label": "Bo, the gardener", "file": "bo.png", "mouthBox": { "x": 0.40, "y": 0.60, "width": 0.18, "height": 0.09 } },
              { "id": "cy", "label": "Cy, the engineer", "mouthBox": { "x": 0.41, "y": 0.62, "width": 0.17, "height": 0.08 } },
              { "id": "di", "label": "Di, the teacher", "file": "di.png", "mouthBox": { "x": 0.43, "y": 0.63, "width": 0.15, "height": 0.07 } }
            ]
            """);
        return folder;
    }

    internal static BuiltInPresenters Presenters(string folder)
        => new(Microsoft.Extensions.Options.Options.Create(new OperatorMediaOptions { PresentersPath = folder }));

    [Fact]
    public void A_Project_Gets_The_Same_Built_In_Presenter_Every_Time()
    {
        var folder = Folder(_root);

        var first = Presenters(folder).Choose("p1");
        var again = Presenters(folder).Choose("p1");
        var other = Presenters(folder).Choose("project-42");

        first.ShouldNotBeNull().Id.ShouldBe("ada");
        again.ShouldBe(first);
        other.ShouldNotBeNull().Id.ShouldBe("bo");
    }

    [Fact]
    public void The_Owners_Pick_Wins_And_An_Unknown_Pick_Falls_Back_To_The_Projects_Own()
    {
        var presenters = Presenters(Folder(_root));

        presenters.Choose("p1", "di").ShouldNotBeNull().Id.ShouldBe("di");
        presenters.Choose("p1", "zed").ShouldNotBeNull().Id.ShouldBe("ada");
    }

    [Fact]
    public void The_List_Gives_Ids_Labels_Files_And_Mouth_Boxes_And_A_File_Defaults_To_The_Id()
    {
        var presenters = Presenters(Folder(_root)).List();

        presenters.Select(p => (p.Id, p.Label, p.File)).ShouldBe([
            ("ada", "Ada, the baker", "ada.png"), ("bo", "Bo, the gardener", "bo.png"), ("cy", "Cy, the engineer", "cy.png"), ("di", "Di, the teacher", "di.png"),
        ]);
        presenters[0].MouthBox.ShouldBe(new OperatorMouthBox(0.42, 0.61, 0.16, 0.08));
    }

    [Theory]
    [InlineData("""[{ "id": "../x", "label": "Escape", "mouthBox": { "x": 0.4, "y": 0.6, "width": 0.2, "height": 0.1 } }]""")]
    [InlineData("""[{ "id": "ada", "file": "../ada.png", "mouthBox": { "x": 0.4, "y": 0.6, "width": 0.2, "height": 0.1 } }]""")]
    [InlineData("""[{ "id": "ada", "mouthBox": { "x": 0.9, "y": 0.6, "width": 0.2, "height": 0.1 } }]""")]
    [InlineData("""[{ "id": "zed", "mouthBox": { "x": 0.4, "y": 0.6, "width": 0.2, "height": 0.1 } }]""")]
    [InlineData("not json")]
    public void An_Entry_That_Would_Leave_The_Folder_Or_The_Picture_Or_Is_Missing_Is_Left_Out(string list)
        => Presenters(Folder(_root, list)).List().ShouldBeEmpty();

    [Fact]
    public void A_List_Inside_A_Presenters_Object_Is_Read_Too()
        => Presenters(Folder(_root, """{ "presenters": [{ "id": "bo", "label": "Bo", "mouthBox": { "x": 0.4, "y": 0.6, "width": 0.2, "height": 0.1 } }] }"""))
            .List().ShouldHaveSingleItem().Id.ShouldBe("bo");

    [Fact]
    public async Task The_Picture_Is_Copied_Into_The_Media_Folder_Under_Its_Builtin_Name()
    {
        var folder = Folder(_root);
        var media = Directory.CreateDirectory(Path.Combine(_root, "media")).FullName;
        var presenters = Presenters(folder);

        var name = await presenters.CopyAsync(presenters.Choose("p1")!, media, CancellationToken.None);

        name.ShouldBe("presenter-builtin-ada.png");
        File.ReadAllBytes(Path.Combine(media, "presenter-builtin-ada.png")).ShouldBe(File.ReadAllBytes(Path.Combine(folder, "ada.png")));
    }

    [Fact]
    public async Task Copying_The_Same_Presenter_Twice_Leaves_The_File_Untouched()
    {
        var media = Directory.CreateDirectory(Path.Combine(_root, "media")).FullName;
        var presenters = Presenters(Folder(_root));
        var target = Path.Combine(media, "presenter-builtin-ada.png");
        await presenters.CopyAsync(presenters.Choose("p1")!, media, CancellationToken.None);
        var earlier = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(target, earlier);

        var name = await presenters.CopyAsync(presenters.Choose("p1")!, media, CancellationToken.None);

        name.ShouldBe("presenter-builtin-ada.png");
        File.GetLastWriteTimeUtc(target).ShouldBe(earlier);
    }
}
