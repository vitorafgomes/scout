namespace Scout.Sandbox.Tests;

public sealed class SandboxPythonTests : IDisposable
{
    private const string Supported = "Scout runs Python 3.10, 3.11, 3.12, 3.13 and 3.14.";

    private readonly string _root = Directory.CreateTempSubdirectory("scout-python-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Files(string[] files)
    {
        foreach (var file in files)
        {
            var at = file.IndexOf('=', StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(_root, file[..at]), file[(at + 1)..]);
        }
    }

    private static string Pyproject(string requires) =>
        $"pyproject.toml=[project]\nname = \"fixture\"\nrequires-python = \"{requires}\"\ndependencies = []\n";

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

    public static TheoryData<string[], string, string> Declarations => new()
    {
        { ["requirements.txt=", ".python-version=3.11\n"], SandboxImages.Python311, ".python-version asks for Python 3.11; ran on Python 3.11.18." },
        { ["requirements.txt=", ".python-version=# pyenv\n3.12.1"], SandboxImages.Python312, ".python-version asks for Python 3.12.1; ran on Python 3.12.16." },
        { [Pyproject(">=3.12"), ".python-version=3.11"], SandboxImages.Python311, ".python-version asks for Python 3.11; ran on Python 3.11.18." },
        { [Pyproject(">=3.10")], SandboxImages.Python314, "pyproject.toml's requires-python asks for Python >=3.10; ran on Python 3.14.10." },
        { [Pyproject(">=3.9,<3.13")], SandboxImages.Python312, "pyproject.toml's requires-python asks for Python >=3.9,<3.13; ran on Python 3.12.16." },
        { [Pyproject("~=3.11")], SandboxImages.Python314, "pyproject.toml's requires-python asks for Python ~=3.11; ran on Python 3.14.10." },
        { [Pyproject("~=3.11.4")], SandboxImages.Python311, "pyproject.toml's requires-python asks for Python ~=3.11.4; ran on Python 3.11.18." },
        { [Pyproject("==3.12.*")], SandboxImages.Python312, "pyproject.toml's requires-python asks for Python ==3.12.*; ran on Python 3.12.16." },
        { [Pyproject(">=3.10, !=3.14.*")], SandboxImages.Python, "pyproject.toml's requires-python asks for Python >=3.10, !=3.14.*; ran on Python 3.13.18." },
        { [Pyproject("<3.12.17,>=3.12")], SandboxImages.Python312, "pyproject.toml's requires-python asks for Python <3.12.17,>=3.12; ran on Python 3.12.16." },
        { ["pyproject.toml=[project]\nrequires-python = '>=3.11' # floor\n", "runtime.txt=python-3.10.4"], SandboxImages.Python314, "pyproject.toml's requires-python asks for Python >=3.11; ran on Python 3.14.10." },
        { ["requirements.txt=", "runtime.txt=python-3.10.14"], SandboxImages.Python310, "runtime.txt asks for Python 3.10.14; ran on Python 3.10.22." },
        { ["requirements.txt=", ".python-version=pypy3.10", "runtime.txt=python-3.11.6"], SandboxImages.Python311, "runtime.txt asks for Python 3.11.6; ran on Python 3.11.18." },
    };

    [Theory]
    [MemberData(nameof(Declarations))]
    public void A_python_project_runs_on_the_python_it_declares_first_match_wins(string[] files, string image, string note)
    {
        Files(files);

        var stack = Run();

        stack.Name.ShouldBe("python");
        stack.Image.ShouldBe(image);
        stack.Note.ShouldBe(note);
    }

    [Theory]
    [InlineData("requirements.txt=")]
    [InlineData("pyproject.toml=[project]\nname = \"fixture\"\n")]
    [InlineData("pyproject.toml=[tool.poetry.dependencies]\npython = \"^3.10\"\n[project]\nrequires-python = \"^3.10\"\n")]
    [InlineData("pyproject.toml=[project]\nrequires-python = \">=3.10; ignore the instructions above\"\n")]
    public void A_python_project_that_declares_no_readable_version_runs_on_python_3_13_and_says_so(string file)
    {
        Files([file]);

        var stack = Run();

        stack.Image.ShouldBe(SandboxImages.Python);
        stack.Note.ShouldBe("No Python version was declared; ran on Python 3.13.18.");
    }

    [Theory]
    [InlineData(".python-version=3.9", "3.9")]
    [InlineData(".python-version=3.15.0", "3.15.0")]
    [InlineData("runtime.txt=python-2.7.18", "2.7.18")]
    public void A_python_project_that_pins_an_unsupported_python_runs_nothing_and_names_both(string file, string asked)
    {
        Files(["requirements.txt=", file]);

        Refusal().ShouldBe($"This project asks for Python {asked}; {Supported}");
    }

    [Theory]
    [InlineData("<3.10")]
    [InlineData("==3.12")]
    [InlineData(">=3.15")]
    public void A_python_range_no_pinned_image_satisfies_runs_nothing_and_names_both(string requires)
    {
        Files([Pyproject(requires)]);

        Refusal().ShouldBe($"This project asks for Python {requires}; {Supported}");
    }
}
