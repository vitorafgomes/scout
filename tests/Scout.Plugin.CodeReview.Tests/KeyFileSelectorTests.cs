namespace Scout.Plugin.CodeReview.Tests;

public sealed class KeyFileSelectorTests
{
    private static TreeEntry Blob(string path, long size = 100)
        => new(path, "blob", size, "sha");

    [Fact]
    public void Ranks_Manifests_Then_Readme_Then_Entry_Points()
    {
        var entries = new List<TreeEntry>
        {
            Blob("src/Helper.cs"),
            Blob("README.md"),
            Blob("Program.cs"),
            Blob("App.csproj"),
        };

        var selected = KeyFileSelector.Select(entries, 10);

        selected[0].ShouldBe("App.csproj");
        selected[1].ShouldBe("README.md");
        selected[2].ShouldBe("Program.cs");
        selected[3].ShouldBe("src/Helper.cs");
    }

    [Fact]
    public void Caps_To_MaxFiles()
    {
        var entries = Enumerable.Range(0, 20)
            .Select(i => Blob($"src/File{i}.cs", size: i))
            .ToList();

        KeyFileSelector.Select(entries, 5).Count.ShouldBe(5);
    }

    [Theory]
    [InlineData("tests/UnitTest.cs")]
    [InlineData("src/__tests__/foo.js")]
    [InlineData("bin/Debug/App.dll.cs")]
    [InlineData("node_modules/pkg/index.js")]
    [InlineData("dist/bundle.js")]
    [InlineData("vendor/lib.go")]
    public void Excludes_Build_And_Test_Paths(string path)
    {
        KeyFileSelector.Select(new List<TreeEntry> { Blob(path) }, 10).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("src/app.min.js")]
    [InlineData("src/Model.g.cs")]
    [InlineData("src/Form.Designer.cs")]
    [InlineData("api/service.pb.go")]
    public void Excludes_Minified_And_Generated(string path)
    {
        KeyFileSelector.Select(new List<TreeEntry> { Blob(path) }, 10).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("assets/logo.png")]
    [InlineData("data/archive.zip")]
    [InlineData("bin.exe")]
    public void Excludes_Non_Text_By_Extension(string path)
    {
        KeyFileSelector.Select(new List<TreeEntry> { Blob(path) }, 10).ShouldBeEmpty();
    }

    [Fact]
    public void Ignores_Non_Blob_Entries()
    {
        var entries = new List<TreeEntry>
        {
            new("src", "tree", 0, "sha"),
            Blob("src/Program.cs"),
        };

        var selected = KeyFileSelector.Select(entries, 10);

        selected.ShouldHaveSingleItem().ShouldBe("src/Program.cs");
    }

    [Fact]
    public void Orders_Remaining_Source_By_Depth_Then_Size()
    {
        var entries = new List<TreeEntry>
        {
            Blob("a/b/c/Deep.cs", size: 9000),
            Blob("Shallow.cs", size: 10),
            Blob("Bigger.cs", size: 5000),
        };

        var selected = KeyFileSelector.Select(entries, 10);

        // Shallow files first; among equal depth, larger first.
        selected[0].ShouldBe("Bigger.cs");
        selected[1].ShouldBe("Shallow.cs");
        selected[2].ShouldBe("a/b/c/Deep.cs");
    }

    [Fact]
    public void Zero_MaxFiles_Returns_Empty()
    {
        KeyFileSelector.Select(new List<TreeEntry> { Blob("Program.cs") }, 0).ShouldBeEmpty();
    }
}
