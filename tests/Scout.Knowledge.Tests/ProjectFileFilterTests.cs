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
    [InlineData("main.go")]
    [InlineData("App.java")]
    [InlineData("Main.kt")]
    [InlineData("lib.rs")]
    [InlineData("app.rb")]
    [InlineData("index.php")]
    [InlineData("View.swift")]
    [InlineData("schema.sql")]
    [InlineData("build.sh")]
    [InlineData("deploy.ps1")]
    [InlineData("Cargo.toml")]
    [InlineData("Scout.csproj")]
    [InlineData("Dockerfile")]
    [InlineData("Makefile")]
    [InlineData("docs/guide.rst")]
    [InlineData(".env.example")]
    [InlineData(".env.sample")]
    [InlineData("config/.env.template")]
    [InlineData("ui/build/electron-builder.yml")]
    [InlineData("src/target/Order.java")]
    [InlineData("build/Build.cs")]
    public void IsIndexable_IncludesCodeAndDocExtensions(string relative)
    {
        ProjectFileFilter.IsIndexable(relative).ShouldBeTrue();
    }

    [Theory]
    [InlineData("image.png")]
    [InlineData("archive.zip")]
    [InlineData("binary.dll")]
    [InlineData("noextension")]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData("config/.env.production")]
    [InlineData("secrets.json")]
    [InlineData("certs/server.pem")]
    [InlineData("keys/id_rsa.key")]
    [InlineData("store.pfx")]
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
    [InlineData("vendor/lib/a.go")]
    [InlineData("target/classes/A.java")]
    [InlineData(".venv/lib/x.py")]
    [InlineData("app/__pycache__/x.py")]
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

    [Theory]
    [InlineData("CMakeLists.txt", "code")]
    [InlineData("Dockerfile", "code")]
    [InlineData("README.md", "doc")]
    [InlineData("notes.txt", "doc")]
    [InlineData("Program.cs", "code")]
    public void KindFor_Treats_A_Named_Build_File_As_Code_Whatever_Its_Extension(string path, string kind) =>
        ProjectFileFilter.KindFor(path).ShouldBe(kind);
}
