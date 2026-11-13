namespace Scout.Sandbox.Tests;

public sealed class SandboxDetectionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("scout-sandbox-detect-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string file, string text)
    {
        var path = Path.Combine(_root, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public static TheoryData<string, (string File, string Text)[], string, string, string, (string Source, string Version)[]> Stacks => new()
    {
        {
            "Node from .nvmrc",
            [("package.json", "{}"), ("package-lock.json", "{}"), (".nvmrc", "20\n")],
            "Node", SandboxImages.Node20, "Node 20.20.2", [(".nvmrc", "Node 20")]
        },
        {
            "Bun from packageManager",
            [("package.json", "{\"packageManager\":\"bun@1.1.38\"}"), ("bun.lock", "{}")],
            "Node", SandboxImages.Bun, "Bun 1.4.4", [("package.json's packageManager", "Bun 1.1.38")]
        },
        {
            "Python with nothing declared",
            [("requirements.txt", "pytest\n")],
            "Python", SandboxImages.Python, "Python 3.13.18", []
        },
        {
            "Go from the toolchain line",
            [("go.mod", "module example.com/m\n\ngo 1.24\n\ntoolchain go1.24.3\n")],
            "Go", SandboxImages.Go124, "Go 1.24.13", [("go.mod's toolchain", "Go 1.24.3")]
        },
        {
            ".NET from global.json",
            [("App.csproj", "<Project><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>"), ("global.json", "{\"sdk\":{\"version\":\"9.0.300\"}}")],
            ".NET", SandboxImages.DotNet9, "SDK 9.0.320", [("global.json", "SDK 9.0.300 with rollForward latestPatch")]
        },
        {
            ".NET from the target frameworks",
            [("App.csproj", "<Project><PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup></Project>")],
            ".NET", SandboxImages.DotNet9, "SDK 9.0.320", [("App.csproj", "net8.0 and net9.0")]
        },
        {
            "Java with Maven from .java-version",
            [("pom.xml", "<project></project>"), (".java-version", "17\n")],
            "Java", SandboxImages.Maven17, "Java 17 with Maven 3.10.0", [(".java-version", "Java 17")]
        },
        {
            "Java with Gradle from the build and the wrapper",
            [("build.gradle.kts", "java { toolchain { languageVersion = JavaLanguageVersion.of(21) } }"), ("gradle/wrapper/gradle-wrapper.properties", "distributionUrl=https\\://services.gradle.org/distributions/gradle-8.10.2-bin.zip\n")],
            "Java", SandboxImages.Gradle8Jdk21, "Java 21 with Gradle 8.14.6",
            [("build.gradle.kts", "Java 21"), ("gradle/wrapper/gradle-wrapper.properties", "Gradle 8.10.2")]
        },
    };

    [Theory]
    [MemberData(nameof(Stacks))]
    public void The_detection_names_the_stack_the_pinned_image_the_version_in_it_and_each_declared_version_with_its_file(
        string name, (string File, string Text)[] files, string stack, string image, string runs, (string Source, string Version)[] declared)
    {
        foreach (var (file, text) in files)
        {
            Write(file, text);
        }

        var detection = DockerSandboxRunner.Detect(_root);

        detection.Stack.ShouldBe(stack, name);
        detection.Image.ShouldBe(image, name);
        detection.Runs.ShouldBe(runs, name);
        detection.Declared.Select(d => (d.Source, d.Version)).ShouldBe(declared, name);
        detection.Refusal.ShouldBeNull(name);
    }

    [Fact]
    public void A_version_Scout_does_not_run_names_the_stack_that_refused_and_the_refusal_and_no_image()
    {
        Write("package.json", "{}");
        Write(".nvmrc", "16\n");

        var detection = DockerSandboxRunner.Detect(_root);

        detection.ShouldSatisfyAllConditions(
            () => detection.Stack.ShouldBe("Node"),
            () => detection.Image.ShouldBeNull(),
            () => detection.Runs.ShouldBeNull(),
            () => detection.Declared.ShouldBeEmpty(),
            () => detection.Refusal.ShouldBe("This project asks for Node 16; Scout runs Node 18, 20, 22, 24 and 26."));
    }

    [Fact]
    public void A_repository_with_no_manifest_has_no_stack_and_says_none_was_found()
    {
        Write("README.md", "# hello\n");

        var detection = DockerSandboxRunner.Detect(_root);

        (detection.Stack, detection.Image, detection.Refusal).ShouldBe(((string?)null, (string?)null, (string?)SandboxStacks.NoStackReason));
    }
}
