namespace Scout.Sandbox.Tests;

public sealed class SandboxGoTests : IDisposable
{
    private const string Supported = "Scout runs Go 1.23.12, 1.24.13, 1.25.14 and 1.26.10.";

    private readonly string _root = Directory.CreateTempSubdirectory("scout-go-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void GoMod(string directives) => File.WriteAllText(Path.Combine(_root, "go.mod"), $"module example.com/fixture\n\n{directives}\n");

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

    public static TheoryData<string, string, string> Declarations => new()
    {
        { "go 1.24", SandboxImages.Go124, "go.mod asks for Go 1.24 or newer; ran on Go 1.24.13." },
        { "go 1.21", SandboxImages.Go123, "go.mod asks for Go 1.21 or newer; ran on Go 1.23.12." },
        { "go 1.23rc1", SandboxImages.Go123, "go.mod asks for Go 1.23 or newer; ran on Go 1.23.12." },
        { "go 1.24.20", SandboxImages.Go, "go.mod asks for Go 1.24.20 or newer; ran on Go 1.25.14." },
        { "go 1.26.0 // pinned", SandboxImages.Go126, "go.mod asks for Go 1.26.0 or newer; ran on Go 1.26.10." },
        { "go 1.22\n\ntoolchain go1.24.3", SandboxImages.Go124, "go.mod's toolchain asks for Go 1.24.3; ran on Go 1.24.13." },
        { "toolchain go1.26.1", SandboxImages.Go126, "go.mod's toolchain asks for Go 1.26.1; ran on Go 1.26.10." },
    };

    [Theory]
    [MemberData(nameof(Declarations))]
    public void A_go_module_runs_on_its_toolchain_or_the_oldest_pinned_go_at_or_above_its_go_line(string directives, string image, string note)
    {
        GoMod(directives);

        var stack = Run();

        stack.Name.ShouldBe("go");
        stack.Image.ShouldBe(image);
        stack.Note.ShouldBe(note);
        stack.Environment["GOTOOLCHAIN"].ShouldBe("local");
    }

    [Theory]
    [InlineData("")]
    [InlineData("go one-point-twenty\ntoolchain default")]
    public void A_go_module_that_declares_no_go_version_runs_on_go_1_25_and_says_so(string directives)
    {
        GoMod(directives);

        var stack = Run();

        stack.Image.ShouldBe(SandboxImages.Go);
        stack.Note.ShouldBe("No Go version was declared; ran on Go 1.25.14.");
    }

    [Theory]
    [InlineData("go 1.27", "1.27 or newer")]
    [InlineData("go 1.26.11", "1.26.11 or newer")]
    [InlineData("go 1.21\ntoolchain go1.22.5", "1.22.5")]
    [InlineData("go 1.24.20\ntoolchain go1.24.15", "1.24.20 or newer")]
    public void A_go_module_no_pinned_go_satisfies_runs_nothing_and_names_both(string directives, string asked)
    {
        GoMod(directives);

        Refusal().ShouldBe($"This project asks for Go {asked}; {Supported}");
    }
}
