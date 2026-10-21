namespace Scout.Knowledge.Tests;

public sealed class ProjectFileFilterTests
{
    [Theory]
    [InlineData("Foo.cs")]
    [InlineData("app.ts")]
    [InlineData("view.tsx")]
    [InlineData("script.js")]
    [InlineData("comp.jsx")]
    [InlineData("main.py")]
    [InlineData("data.json")]
    [InlineData("config.yml")]
    [InlineData("config.yaml")]
    [InlineData("README.md")]
    [InlineData("notes.txt")]
    public void IsIndexable_IncludesCodeAndDocExtensions(string relative)
    {
        ProjectFileFilter.IsIndexable(relative).ShouldBeTrue();
    }

    [Theory]
    [InlineData("image.png")]
    [InlineData("archive.zip")]
    [InlineData("binary.dll")]
    [InlineData("noextension")]
    public void IsIndexable_ExcludesOtherExtensions(string relative)
    {
        ProjectFileFilter.IsIndexable(relative).ShouldBeFalse();
    }

    [Theory]
    [InlineData("bin/Foo.cs")]
    [InlineData("obj/Foo.cs")]
    [InlineData(".git/config.yml")]
    [InlineData("node_modules/pkg/index.js")]
    [InlineData("dist/app.js")]
    [InlineData(".vs/Foo.cs")]
    [InlineData("src/bin/Foo.cs")]
    public void IsIndexable_ExcludesSkipSegments(string relative)
    {
        ProjectFileFilter.IsIndexable(relative).ShouldBeFalse();
    }

    [Fact]
    public void ToRelativePath_NormalizesSeparators()
    {
        var repo = Path.Combine(Path.GetTempPath(), "scout-filter-repo");
        var abs = Path.Combine(repo, "src", "Foo.cs");

        ProjectFileFilter.ToRelativePath(repo, abs).ShouldBe("src/Foo.cs");
    }
}
