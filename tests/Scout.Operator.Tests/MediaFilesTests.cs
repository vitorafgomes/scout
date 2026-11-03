namespace Scout.Operator.Tests;

public sealed class MediaFilesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("scout-media-files-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("../scene-s1.mp3")]
    [InlineData("nested/scene-s1.mp3")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("...")]
    [InlineData(".scene-s1.mp3")]
    [InlineData("scene-..mp3")]
    [InlineData("")]
    public async Task A_File_Name_That_Would_Leave_The_Media_Directory_Is_Refused_And_Nothing_Is_Written(string fileName)
    {
        var media = Directory.CreateDirectory(Path.Combine(_root, "p1")).FullName;

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => MediaFiles.WriteAsync(media, fileName, new byte[] { 1 }, CancellationToken.None));

        ex.Message.ShouldBe("Scout refused to write a file outside the project's media folder.");
        Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories).ShouldBe([media]);
        Directory.GetFileSystemEntries(media).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Media_Directory_That_Is_Not_An_Absolute_Path_Is_Refused()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => MediaFiles.WriteAsync("relative/media", "scene-s1.mp3", new byte[] { 1 }, CancellationToken.None));

        ex.Message.ShouldBe("Scout refused to write a file outside the project's media folder.");
    }

    [Fact]
    public async Task A_Write_Replaces_The_File_Whole_And_Leaves_No_Temporary_File()
    {
        var media = Path.Combine(_root, "p2");

        await MediaFiles.WriteAsync(media, "scene-s1.png", new byte[] { 1, 2, 3, 4 }, CancellationToken.None);
        await MediaFiles.WriteAsync(media, "scene-s1.png", new byte[] { 9 }, CancellationToken.None);

        File.ReadAllBytes(Path.Combine(media, "scene-s1.png")).ShouldBe(new byte[] { 9 });
        Directory.GetFiles(media).ShouldBe([Path.Combine(media, "scene-s1.png")]);
        MediaFiles.Exists(media, "scene-s1.png").ShouldBeTrue();
        MediaFiles.Exists(media, "../scene-s1.png").ShouldBeFalse();
    }
}
