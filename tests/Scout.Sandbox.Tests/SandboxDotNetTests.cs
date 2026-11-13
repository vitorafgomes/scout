namespace Scout.Sandbox.Tests;

public sealed class SandboxDotNetTests : IDisposable
{
    private const string Supported = "Scout runs SDK 8.0.427, 9.0.320 and 10.0.403.";

    private readonly string _base = Directory.CreateTempSubdirectory("scout-dotnet-").FullName;

    public SandboxDotNetTests() => Directory.CreateDirectory(Root);

    public void Dispose() => Directory.Delete(_base, recursive: true);

    private string Root => Path.Combine(_base, "repo");

    private void Write(string name, string content)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void Project(string name, string frameworks) =>
        Write(name, $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{frameworks}</PropertyGroup></Project>");

    private void GlobalJson(string version, string? rollForward = null) =>
        Write("global.json", rollForward is null
            ? $$"""{ "sdk": { "version": "{{version}}" } }"""
            : $$"""{ "sdk": { "version": "{{version}}", "rollForward": "{{rollForward}}" } }""");

    private SandboxStack Run()
    {
        var stack = SandboxStacks.Detect(Root, null, out var reason);
        return stack.ShouldNotBeNull(reason);
    }

    private string Refusal()
    {
        SandboxStacks.Detect(Root, null, out var reason).ShouldBeNull();
        return reason;
    }

    public static TheoryData<string, string?, string, string> GlobalJsonDeclarations => new()
    {
        { "9.0.300", "latestPatch", SandboxImages.DotNet9, "global.json asks for SDK 9.0.300 with rollForward latestPatch; ran on SDK 9.0.320." },
        { "9.0.310", null, SandboxImages.DotNet9, "global.json asks for SDK 9.0.310 with rollForward latestPatch; ran on SDK 9.0.320." },
        { "9.0.100", "latestFeature", SandboxImages.DotNet9, "global.json asks for SDK 9.0.100 with rollForward latestFeature; ran on SDK 9.0.320." },
        { "8.0.100", "feature", SandboxImages.DotNet8, "global.json asks for SDK 8.0.100 with rollForward feature; ran on SDK 8.0.427." },
        { "8.0.100", "latestMinor", SandboxImages.DotNet8, "global.json asks for SDK 8.0.100 with rollForward latestMinor; ran on SDK 8.0.427." },
        { "10.0.100", "latestMinor", SandboxImages.DotNet, "global.json asks for SDK 10.0.100 with rollForward latestMinor; ran on SDK 10.0.403." },
        { "10.0.403", "disable", SandboxImages.DotNet, "global.json asks for SDK 10.0.403 with rollForward disable; ran on SDK 10.0.403." },
        { "8.0.100", "major", SandboxImages.DotNet8, "global.json asks for SDK 8.0.100 with rollForward major; ran on SDK 8.0.427." },
        { "8.0.100", "LatestMajor", SandboxImages.DotNet, "global.json asks for SDK 8.0.100 with rollForward latestMajor; ran on SDK 10.0.403; net8.0 tests ran on the .NET 10 runtime (roll-forward)." },
        { "7.0.100", "latestMajor", SandboxImages.DotNet, "global.json asks for SDK 7.0.100 with rollForward latestMajor; ran on SDK 10.0.403; net8.0 tests ran on the .NET 10 runtime (roll-forward)." },
        { "9.0.300", "sideways", SandboxImages.DotNet9, "global.json asks for SDK 9.0.300 with rollForward latestPatch; ran on SDK 9.0.320." },
    };

    [Theory]
    [MemberData(nameof(GlobalJsonDeclarations))]
    public void A_global_json_runs_on_the_sdk_its_roll_forward_policy_accepts(string version, string? rollForward, string image, string note)
    {
        Project("App.csproj", "<TargetFramework>net8.0</TargetFramework>");
        GlobalJson(version, rollForward);

        var stack = Run();

        stack.Name.ShouldBe("dotnet");
        stack.Image.ShouldBe(image);
        stack.Note.ShouldBe(note);
    }

    [Theory]
    [InlineData("9.0.100", null, "latestPatch")]
    [InlineData("9.0.330", "latestFeature", "latestFeature")]
    [InlineData("7.0.100", "latestFeature", "latestFeature")]
    [InlineData("7.0.100", "latestMinor", "latestMinor")]
    [InlineData("11.0.100", "latestMajor", "latestMajor")]
    [InlineData("10.0.400", "disable", "disable")]
    public void A_global_json_whose_policy_no_pinned_sdk_satisfies_runs_nothing_and_names_both(string version, string? rollForward, string policy)
    {
        Project("App.csproj", "<TargetFramework>net9.0</TargetFramework>");
        GlobalJson(version, rollForward);

        Refusal().ShouldBe($"This project's global.json asks for SDK {version} with rollForward {policy}; {Supported}");
    }

    [Theory]
    [InlineData("major", true)]
    [InlineData("latestMajor", true)]
    [InlineData("latestMinor", false)]
    [InlineData("latestFeature", false)]
    [InlineData("disable", false)]
    public void A_global_json_rolls_tests_forward_onto_a_newer_runtime_only_when_its_policy_allows_a_major_roll(string rollForward, bool rolls)
    {
        Project("App.csproj", "<TargetFrameworks>net8.0;net10.0</TargetFrameworks>");
        GlobalJson(rollForward == "disable" ? "10.0.403" : "10.0.100", rollForward);

        var stack = Run();

        stack.Environment.GetValueOrDefault("DOTNET_ROLL_FORWARD").ShouldBe(rolls ? "Major" : null);
        stack.Note.ShouldNotBeNull().EndsWith("; net8.0 tests ran on the .NET 10 runtime (roll-forward).", StringComparison.Ordinal).ShouldBe(rolls);
    }

    [Fact]
    public void Without_global_json_a_project_runs_on_the_sdk_of_its_target_framework_and_says_none_was_declared()
    {
        Project("App.csproj", "<TargetFramework>net8.0</TargetFramework>");

        var stack = Run();

        stack.Image.ShouldBe(SandboxImages.DotNet8);
        stack.Environment["DOTNET_ROLL_FORWARD"].ShouldBe("Major");
        stack.Note.ShouldBe("No SDK was declared; ran on the .NET 8 SDK.");
    }

    [Theory]
    [InlineData("net8.0", "mkdir -p /work/cache/home/.local/share/NuGet/Migrations && touch /work/cache/home/.local/share/NuGet/Migrations/1 && dotnet restore 'App.csproj'")]
    [InlineData("net9.0", "dotnet restore 'App.csproj'")]
    [InlineData("net10.0", "dotnet restore 'App.csproj'")]
    public void Only_the_sdk_8_restore_first_marks_nuget_s_migration_done_in_the_fresh_home(string framework, string restore)
    {
        Project("App.csproj", $"<TargetFramework>{framework}</TargetFramework>");

        Run().RestoreCommand.ShouldBe(restore);
    }

    [Fact]
    public void Without_global_json_a_solution_runs_on_the_sdk_of_its_highest_target_framework_and_names_the_lower_ones()
    {
        Write("Shop.slnx", """<Solution><Folder Name="/src/"><Project Path="src/Shop/Shop.csproj" /></Folder><Project Path="tests/Shop.Tests/Shop.Tests.csproj" /></Solution>""");
        Project("src/Shop/Shop.csproj", "<TargetFramework>net8.0</TargetFramework>");
        Project("tests/Shop.Tests/Shop.Tests.csproj", "<TargetFrameworks>net9.0;net10.0</TargetFrameworks>");

        var stack = Run();

        stack.Image.ShouldBe(SandboxImages.DotNet);
        stack.Environment["DOTNET_ROLL_FORWARD"].ShouldBe("Major");
        stack.Note.ShouldBe("No SDK was declared; ran on the .NET 10 SDK; net8.0 and net9.0 tests ran on the .NET 10 runtime (roll-forward).");
    }

    [Fact]
    public void A_sln_lists_its_projects_with_backslashes_and_frameworks_that_are_not_dotnet_5_or_newer_are_ignored()
    {
        Write("Shop.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Shop", "src\Shop\Shop.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Legacy", "src\Legacy\Legacy.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            """);
        Project("src/Shop/Shop.csproj", "<TargetFramework>net9.0-windows</TargetFramework>");
        Project("src/Legacy/Legacy.csproj", "<TargetFrameworks>net48;netstandard2.0;netcoreapp3.1</TargetFrameworks>");

        var stack = Run();

        stack.Image.ShouldBe(SandboxImages.DotNet9);
        stack.Note.ShouldBe("No SDK was declared; ran on the .NET 9 SDK.");
    }

    [Fact]
    public void Target_frameworks_compare_as_numbers_so_net10_is_above_net9()
    {
        Project("A.csproj", "<TargetFramework>net10.0</TargetFramework>");
        Project("B.csproj", "<TargetFramework>net9.0</TargetFramework>");

        Run().Image.ShouldBe(SandboxImages.DotNet);
    }

    [Theory]
    [InlineData("net7.0")]
    [InlineData("net11.0")]
    public void A_target_framework_no_pinned_sdk_builds_runs_nothing_and_names_both(string framework)
    {
        Project("App.csproj", $"<TargetFramework>{framework}</TargetFramework>");

        Refusal().ShouldBe($"This project targets {framework}; Scout runs .NET 8, 9 and 10.");
    }

    [Theory]
    [InlineData("App.csproj", "<Project />")]
    [InlineData("App.csproj", "{}")]
    [InlineData("App.csproj", "<Project><PropertyGroup><TargetFramework>net8.0")]
    [InlineData("App.csproj", "<?xml version=\"1.0\"?><!DOCTYPE Project [<!ENTITY tfm \"net8.0\">]><Project><PropertyGroup><TargetFramework>&tfm;</TargetFramework></PropertyGroup></Project>")]
    [InlineData("App.csproj", "<Project><PropertyGroup><TargetFramework>$(Frameworks)</TargetFramework></PropertyGroup></Project>")]
    public void A_project_that_declares_no_readable_framework_runs_on_the_net_10_sdk_and_says_none_was_declared(string file, string content)
    {
        Write(file, content);

        var stack = Run();

        stack.Image.ShouldBe(SandboxImages.DotNet);
        stack.Note.ShouldBe("No SDK was declared; ran on the .NET 10 SDK.");
    }

    [Fact]
    public void A_root_project_without_a_framework_takes_the_one_its_directory_build_props_declares()
    {
        Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("Directory.Build.props", "<Project><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");

        var stack = Run();

        stack.Image.ShouldBe(SandboxImages.DotNet9);
        stack.Note.ShouldBe("No SDK was declared; ran on the .NET 9 SDK.");
    }

    [Fact]
    public void Each_listed_project_takes_the_nearest_directory_build_props_that_declares_a_framework_up_to_the_root()
    {
        Write("Shop.slnx", """<Solution><Project Path="src/Shop/Shop.csproj" /><Project Path="tests/Shop.Tests/Shop.Tests.csproj" /></Solution>""");
        Write("src/Shop/Shop.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("tests/Shop.Tests/Shop.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("src/Directory.Build.props", "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        Write("tests/Directory.Build.props", "<Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>");
        Write("Directory.Build.props", "<Project><PropertyGroup><TargetFrameworks>net9.0</TargetFrameworks></PropertyGroup></Project>");

        var stack = Run();

        stack.Image.ShouldBe(SandboxImages.DotNet9);
        stack.Note.ShouldBe("No SDK was declared; ran on the .NET 9 SDK; net8.0 tests ran on the .NET 9 runtime (roll-forward).");
    }

    [Fact]
    public void A_project_s_own_framework_wins_over_its_directory_build_props()
    {
        Project("App.csproj", "<TargetFramework>net8.0</TargetFramework>");
        Write("Directory.Build.props", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        var stack = Run();

        stack.Image.ShouldBe(SandboxImages.DotNet8);
        stack.Note.ShouldBe("No SDK was declared; ran on the .NET 8 SDK.");
    }

    [Fact]
    public void A_directory_build_props_above_the_snapshot_a_linked_one_an_import_or_an_entity_is_never_read()
    {
        File.WriteAllText(Path.Combine(_base, "Directory.Build.props"), "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        var outside = Path.Combine(_base, "outside.props");
        File.WriteAllText(outside, "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        Write("Shop.slnx", """<Solution><Project Path="a/A.csproj" /><Project Path="b/B.csproj" /><Project Path="c/C.csproj" /></Solution>""");
        Write("a/A.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("b/B.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("c/C.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.CreateSymbolicLink(Path.Combine(Root, "a", "Directory.Build.props"), outside);
        Write("b/Directory.Build.props", "<Project><Import Project=\"../eng/tfm.props\" /></Project>");
        Write("eng/tfm.props", "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        Write("c/Directory.Build.props", "<?xml version=\"1.0\"?><!DOCTYPE Project [<!ENTITY tfm \"net8.0\">]><Project><PropertyGroup><TargetFramework>&tfm;</TargetFramework></PropertyGroup></Project>");

        var stack = Run();

        stack.Image.ShouldBe(SandboxImages.DotNet);
        stack.Note.ShouldBe("No SDK was declared; ran on the .NET 10 SDK.");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "msbuild-sdks": { "Foo.Sdk": "1.0.0" } }""")]
    [InlineData("""{ "sdk": { "version": "latest" } }""")]
    public void A_global_json_without_a_readable_sdk_version_falls_back_to_the_target_framework(string content)
    {
        Project("App.csproj", "<TargetFramework>net9.0</TargetFramework>");
        Write("global.json", content);

        var stack = Run();

        stack.Image.ShouldBe(SandboxImages.DotNet9);
        stack.Note.ShouldBe("No SDK was declared; ran on the .NET 9 SDK.");
    }

    [Fact]
    public void A_solution_path_outside_the_snapshot_or_rooted_is_never_read()
    {
        var outside = Path.Combine(_base, "outside", "Evil.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        Write("Shop.slnx", $"""<Solution><Project Path="../outside/Evil.csproj" /><Project Path="{outside}" /></Solution>""");

        var stack = Run();

        stack.Image.ShouldBe(SandboxImages.DotNet);
        stack.Note.ShouldBe("No SDK was declared; ran on the .NET 10 SDK.");
    }

    [Fact]
    public void A_repository_that_marks_tests_Category_Docker_runs_without_them()
    {
        const string Marked = "public class DockerTests { [Fact, Trait(\"Category\", \"Docker\")] public void Runs() { } }";
        Write("App.slnx", "<Solution />");
        Write("tests/E2E/DockerTests.cs", Marked);
        Write("tests/E2E/MoreDockerTests.cs", "[Trait( \"Category\" , \"Docker\" )]\npublic class MoreDockerTests { }");
        Write("tests/Unit/UnitTests.cs", "[Trait(\"Category\", \"Unit\")] public class UnitTests { }");
        Write("bin/Generated.cs", Marked);
        Write("obj/Generated.cs", Marked);
        Write(".git/Stale.cs", Marked);
        Write("node_modules/pkg/Vendored.cs", Marked);
        Write("tests/Huge.cs", Marked + new string(' ', 1024 * 1024));

        var stack = Run();

        stack.TestCommand.ShouldBe("dotnet test 'App.slnx' --no-restore --filter 'Category!=Docker'");
        stack.LeftOut.ShouldBe("Left out the tests marked Category=Docker, found in 2 file(s): the container has no Docker daemon.");
        stack.Note.ShouldBe("No SDK was declared; ran on the .NET 10 SDK.");
    }

    [Fact]
    public void A_source_file_that_cannot_be_read_or_is_a_symbolic_link_declares_nothing()
    {
        const string Marked = "[Trait(\"Category\", \"Docker\")] public class DockerTests { }";
        var outside = Directory.CreateDirectory(Path.Combine(_base, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "Outside.cs"), Marked);
        Write("App.slnx", "<Solution />");
        Write("tests/Locked.cs", Marked);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(Root, "tests", "Locked.cs"), UnixFileMode.None);
        }
        File.CreateSymbolicLink(Path.Combine(Root, "tests", "Linked.cs"), Path.Combine(outside, "Outside.cs"));
        Directory.CreateSymbolicLink(Path.Combine(Root, "linked"), outside);

        var stack = Run();

        stack.TestCommand.ShouldBe("dotnet test 'App.slnx' --no-restore");
        stack.LeftOut.ShouldBeNull();
    }
}
