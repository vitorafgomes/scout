namespace Scout.Sandbox.Tests;

public sealed class SandboxJavaTests : IDisposable
{
    private const string Supported = "Scout runs Java 8, 11, 17, 21 and 25.";

    private readonly string _root = Directory.CreateTempSubdirectory("scout-java-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string name, string content)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void Pom(string properties = "", string build = "") =>
        Write("pom.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <modelVersion>4.0.0</modelVersion>
              <groupId>fixture</groupId>
              <artifactId>fixture</artifactId>
              <version>1.0.0</version>
              <properties>{properties}</properties>
              <build>{build}</build>
            </project>
            """);

    private static string CompilerPlugin(string configuration) =>
        $"<plugins><plugin><groupId>org.apache.maven.plugins</groupId><artifactId>maven-compiler-plugin</artifactId><configuration>{configuration}</configuration></plugin></plugins>";

    private void Wrapper(string version) =>
        Write("gradle/wrapper/gradle-wrapper.properties", $"distributionBase=GRADLE_USER_HOME\ndistributionUrl=https\\://services.gradle.org/distributions/gradle-{version}-bin.zip\nzipStorePath=wrapper/dists\n");

    private SandboxStack Run()
    {
        var stack = SandboxStacks.Detect(_root, null, out var reason);
        return stack.ShouldNotBeNull(reason);
    }

    private string Refusal()
    {
        SandboxStacks.Detect(_root, null, out var reason).ShouldBeNull();
        return reason;
    }

    public static TheoryData<string, string, string[], int> MavenDeclarations => new()
    {
        { "<maven.compiler.release>17</maven.compiler.release>", "", [], 17 },
        { "<maven.compiler.release>17</maven.compiler.release><maven.compiler.source>11</maven.compiler.source><java.version>21</java.version>", "", [], 17 },
        { "<maven.compiler.source>11</maven.compiler.source><maven.compiler.target>17</maven.compiler.target>", "", [], 11 },
        { "<maven.compiler.target>1.8</maven.compiler.target>", "", [], 8 },
        { "<java.version>21</java.version>", CompilerPlugin("<release>11</release>"), [], 11 },
        { "<java.version>17</java.version>", "", [".java-version=21"], 17 },
        { "<jdk>11</jdk><java.version>17</java.version>", CompilerPlugin("<release>${jdk}</release>"), [], 11 },
        { "<maven.compiler.release>${java.version}</maven.compiler.release><java.version>11</java.version>", "", [], 11 },
        { "<maven.compiler.release>${parent.java}</maven.compiler.release><java.version>17</java.version>", "", [], 17 },
        { "", "", [".java-version=temurin-11.0.2"], 11 },
        { "", "", [".java-version=25", ".sdkmanrc=java=17.0.9-tem"], 25 },
        { "", "", [".sdkmanrc=# sdk env\njava=1.8.0_402-zulu\nmaven=3.9.6"], 8 },
    };

    [Theory]
    [MemberData(nameof(MavenDeclarations))]
    public void A_maven_project_runs_on_the_jdk_it_declares_first_match_wins(string properties, string build, string[] files, int java)
    {
        Pom(properties, build);
        foreach (var file in files)
        {
            var at = file.IndexOf('=', StringComparison.Ordinal);
            Write(file[..at], file[(at + 1)..]);
        }

        var stack = Run();

        stack.Name.ShouldBe("java");
        stack.Image.ShouldStartWith($"maven:3-eclipse-temurin-{java}@sha256:");
        stack.Note.ShouldBe($"Ran on Java {java} with Maven 3.10.0.");
    }

    [Fact]
    public void A_maven_project_that_declares_no_java_version_runs_on_java_21_and_says_so()
    {
        Pom();

        var stack = Run();

        stack.Image.ShouldBe(SandboxImages.Maven21);
        stack.Note.ShouldBe("No Java version was declared; ran on Java 21 with Maven 3.10.0.");
    }

    [Fact]
    public void A_malformed_pom_is_read_as_declaring_no_version_and_still_runs_with_maven()
    {
        Write("pom.xml", "<project><properties><maven.compiler.release>17</maven.compiler.release>");

        var stack = Run();

        stack.Image.ShouldBe(SandboxImages.Maven21);
        stack.Note.ShouldBe("No Java version was declared; ran on Java 21 with Maven 3.10.0.");
    }

    [Fact]
    public void A_pom_with_a_document_type_declaration_is_never_expanded()
    {
        Write("pom.xml", """
            <?xml version="1.0"?>
            <!DOCTYPE project [<!ENTITY version "17">]>
            <project><properties><maven.compiler.release>&version;</maven.compiler.release></properties></project>
            """);

        Run().Note.ShouldBe("No Java version was declared; ran on Java 21 with Maven 3.10.0.");
    }

    [Theory]
    [InlineData("<maven.compiler.release>23</maven.compiler.release>", 23)]
    [InlineData("<maven.compiler.source>1.6</maven.compiler.source>", 6)]
    public void A_maven_project_that_asks_for_an_unsupported_java_runs_nothing_and_names_both(string properties, int java)
    {
        Pom(properties);

        Refusal().ShouldBe($"This project asks for Java {java}; {Supported}");
    }

    [Fact]
    public void Maven_restores_with_the_network_without_running_a_test_and_tests_offline_with_its_caches_under_work()
    {
        Pom("<maven.compiler.release>17</maven.compiler.release>");

        var stack = Run();

        stack.RestoreCommand.ShouldBe("mvn -B -ntp test -Dtest='*#scoutRestoreSelectsNoTest' -Dsurefire.failIfNoSpecifiedTests=false");
        stack.TestCommand.ShouldBe("mvn -B -ntp -o test");
        stack.Environment["HOME"].ShouldBe("/work/cache/home");
        stack.Environment["MAVEN_OPTS"].ShouldBe("-Dmaven.repo.local=/work/cache/m2 -Duser.home=/work/cache/home");
        stack.Environment["MAVEN_CONFIG"].ShouldBe("/work/cache/home/.m2");
    }

    public static TheoryData<string, string, string, int> GradleDeclarations => new()
    {
        { "build.gradle", "java {\n    toolchain {\n        languageVersion = JavaLanguageVersion.of(17)\n    }\n}\nsourceCompatibility = '11'", "", 17 },
        { "build.gradle.kts", "java { toolchain { languageVersion.set(JavaLanguageVersion.of(\"11\")) } }", "", 11 },
        { "build.gradle.kts", "kotlin { jvmToolchain(17) }", "", 17 },
        { "build.gradle", "sourceCompatibility = '1.8'\ntargetCompatibility = '11'", "", 8 },
        { "build.gradle", "java { sourceCompatibility = JavaVersion.VERSION_11 }", "", 11 },
        { "build.gradle.kts", "java { targetCompatibility = JavaVersion.VERSION_1_8 }", ".java-version=17", 8 },
        { "build.gradle", "sourceCompatibility = 17", "", 17 },
        { "build.gradle", "plugins { id 'java' }", ".java-version=11.0.22", 11 },
        { "settings.gradle", "rootProject.name = 'fixture'", ".sdkmanrc=java=17.0.9-tem", 17 },
    };

    [Theory]
    [MemberData(nameof(GradleDeclarations))]
    public void A_gradle_project_runs_on_the_jdk_it_declares_first_match_wins(string buildFile, string build, string file, int java)
    {
        Write(buildFile, build);
        if (file.Length > 0)
        {
            var at = file.IndexOf('=', StringComparison.Ordinal);
            Write(file[..at], file[(at + 1)..]);
        }

        var stack = Run();

        stack.Name.ShouldBe("java");
        stack.Image.ShouldStartWith($"gradle:8-jdk{java}@sha256:");
        stack.Note.ShouldBe($"Ran on Java {java} with Gradle 8.14.6 from the image, not the repository's wrapper; no Gradle version was declared.");
    }

    [Theory]
    [InlineData("build.gradle")]
    [InlineData("build.gradle.kts")]
    [InlineData("settings.gradle")]
    [InlineData("settings.gradle.kts")]
    public void A_gradle_project_that_declares_nothing_runs_gradle_8_on_java_21_and_says_so(string manifest)
    {
        Write(manifest, string.Empty);

        var stack = Run();

        stack.Image.ShouldBe(SandboxImages.Gradle8Jdk21);
        stack.Note.ShouldBe("No Java version was declared; ran on Java 21 with Gradle 8.14.6 from the image, not the repository's wrapper; no Gradle version was declared.");
    }

    [Fact]
    public void A_gradle_wrapper_picks_the_gradle_major_and_the_note_names_the_version_it_asked_for()
    {
        Write("build.gradle", "java { toolchain { languageVersion = JavaLanguageVersion.of(17) } }");
        Wrapper("7.6.1");

        var stack = Run();

        stack.Image.ShouldBe(SandboxImages.Gradle7Jdk17);
        stack.Note.ShouldBe("Ran on Java 17 with Gradle 7.6.6 from the image, not the repository's wrapper; the wrapper asks for Gradle 7.6.1.");
    }

    [Fact]
    public void A_gradle_9_wrapper_with_no_java_declared_runs_on_java_21()
    {
        Write("build.gradle.kts", string.Empty);
        Wrapper("9.1.0");

        var stack = Run();

        stack.Image.ShouldBe(SandboxImages.Gradle9Jdk21);
        stack.Note.ShouldBe("No Java version was declared; ran on Java 21 with Gradle 9.8.2 from the image, not the repository's wrapper; the wrapper asks for Gradle 9.1.0.");
    }

    [Fact]
    public void A_gradle_7_wrapper_with_no_java_declared_runs_on_its_newest_jdk_and_says_so()
    {
        Write("build.gradle", string.Empty);
        Wrapper("7.6");

        var stack = Run();

        stack.Image.ShouldBe(SandboxImages.Gradle7Jdk17);
        stack.Note.ShouldBe("No Java version was declared; ran on Java 17 with Gradle 7.6.6 from the image, not the repository's wrapper; the wrapper asks for Gradle 7.6.");
    }

    [Fact]
    public void The_wrapper_url_never_reaches_the_note_only_its_version()
    {
        Write("build.gradle", string.Empty);
        Write("gradle/wrapper/gradle-wrapper.properties", "distributionUrl=https\\://mirror.example.invalid/dists/gradle-8.5-all.zip\n");

        var stack = Run();

        stack.Image.ShouldBe(SandboxImages.Gradle8Jdk21);
        stack.Note.ShouldNotBeNull().ShouldEndWith("the wrapper asks for Gradle 8.5.");
        stack.Note.ShouldNotContain("mirror");
    }

    [Theory]
    [InlineData("6.9.4", "sourceCompatibility = '11'", "This project asks for Gradle 6; Scout runs Gradle 7, 8 and 9.")]
    [InlineData("8.5", "java { toolchain { languageVersion = JavaLanguageVersion.of(25) } }", "This project asks for Gradle 8 on Java 25; Scout runs Gradle 8 on Java 8, 11, 17 and 21.")]
    [InlineData("9.0.0", "sourceCompatibility = '1.8'", "This project asks for Gradle 9 on Java 8; Scout runs Gradle 9 on Java 17, 21 and 25.")]
    [InlineData("8.5", "sourceCompatibility = 23", "This project asks for Java 23; Scout runs Java 8, 11, 17, 21 and 25.")]
    public void A_gradle_project_that_asks_for_an_unsupported_version_runs_nothing_and_names_both(string wrapper, string build, string reason)
    {
        Write("build.gradle", build);
        Wrapper(wrapper);

        Refusal().ShouldBe(reason);
    }

    [Fact]
    public void Gradle_restores_the_test_runtime_with_the_network_through_the_image_gradle_and_tests_offline_never_the_wrapper()
    {
        Write("build.gradle", string.Empty);
        Write("gradlew", "#!/bin/sh\necho wrapper");
        Wrapper("8.5");

        var stack = Run();

        stack.RestoreCommand.ShouldBe("printf '%s' 'allprojects { tasks.register(\"scoutResolve\") { inputs.files(project.provider { project.configurations.findByName(\"testRuntimeClasspath\") ?: project.files() }); doLast { } } }' > /work/cache/scout-restore.gradle && gradle --no-daemon -I /work/cache/scout-restore.gradle testClasses scoutResolve");
        stack.TestCommand.ShouldBe("gradle --no-daemon --offline test");
        stack.RestoreCommand.ShouldNotContain("gradlew");
        stack.TestCommand.ShouldNotContain("gradlew");
        stack.Environment["HOME"].ShouldBe("/work/cache/home");
        stack.Environment["GRADLE_USER_HOME"].ShouldBe("/work/cache/gradle");
    }

    [Fact]
    public async Task The_plugin_s_command_for_a_ci_gradlew_test_runs_the_image_s_gradle_offline_and_the_note_that_says_so_is_true()
    {
        Write("build.gradle", "java { toolchain { languageVersion = JavaLanguageVersion.of(17) } }");
        Write("gradlew", "#!/bin/sh\necho wrapper");
        Wrapper("8.5");
        var work = Directory.CreateTempSubdirectory("scout-java-work-").FullName;
        var processes = new FakeProcessRunner();
        try
        {
            var runner = new DockerSandboxRunner(new SandboxOptions { WorkDirectory = work }, new Microsoft.Extensions.Time.Testing.FakeTimeProvider(), processes, []);

            var result = await runner.RunTestsAsync(new SandboxRequest(_root, null, "gradle --no-daemon --offline test"), CancellationToken.None);

            result.Command.ShouldBe("gradle --no-daemon --offline test");
            processes.Test.Arguments[^1].ShouldBe("gradle --no-daemon --offline test");
            FakeProcessRunner.HasPair(processes.Test, "--network", "none").ShouldBeTrue();
            processes.Runs.SelectMany(call => call.Arguments).ShouldAllBe(argument => !argument.Contains("gradlew"));
            result.Note.ShouldBe("Ran on Java 17 with Gradle 8.14.6 from the image, not the repository's wrapper; the wrapper asks for Gradle 8.5.");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void A_pom_beside_a_gradle_build_runs_with_maven()
    {
        Write("build.gradle", "sourceCompatibility = '11'");
        Pom("<maven.compiler.release>17</maven.compiler.release>");

        Run().Image.ShouldBe(SandboxImages.Maven17);
    }
}
