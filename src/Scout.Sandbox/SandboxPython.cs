using System.Globalization;
using System.Text.RegularExpressions;

namespace Scout.Sandbox;

/// <summary>
/// The Python stack, <c>requirements.txt</c> or <c>pyproject.toml</c> at the root, installed into a venv under
/// <c>/work</c> and tested with pytest, on the Python the project declares: <c>.python-version</c>, then
/// <c>requires-python</c> in <c>pyproject.toml</c> (the newest pinned image inside it), then <c>runtime.txt</c>; Python
/// 3.13 when nothing is declared; a version no image has is refused.
/// </summary>
internal static partial class SandboxPython
{
    internal static readonly IReadOnlyList<RuntimeImage> Images =
    [
        new("3.10", new Version(3, 10, 22), SandboxImages.Python310),
        new("3.11", new Version(3, 11, 18), SandboxImages.Python311),
        new("3.12", new Version(3, 12, 16), SandboxImages.Python312),
        new("3.13", new Version(3, 13, 18), SandboxImages.Python),
        new("3.14", new Version(3, 14, 10), SandboxImages.Python314),
    ];

    private const string DefaultPython = "3.13";
    private const string Venv = "/work/cache/venv";

    [GeneratedRegex(@"^(\d{1,2})\.(\d{1,3})(?:\.\d{1,4})?$")]
    private static partial Regex Pin();

    [GeneratedRegex(@"^\s*requires-python\s*=\s*[""']([^""'\r\n]{1,100})[""']\s*(?:#.*)?$")]
    private static partial Regex RequiresPython();

    [GeneratedRegex(@"^\s*(~=|===|==|!=|<=|>=|<|>)\s*(\d{1,4}(?:\.\d{1,4}){0,2})(\.\*)?\s*$")]
    private static partial Regex Clause();

    /// <summary>The Python stack, or null: <paramref name="reason"/> untouched without a Python manifest, else the refusal.</summary>
    public static SandboxStack? Detect(string root, ref string reason)
    {
        var requirements = SandboxStacks.Has(root, "requirements.txt");
        var project = SandboxStacks.Has(root, "pyproject.toml");
        if (!requirements && !project)
        {
            return null;
        }

        string note;
        RuntimeImage? image;
        SandboxDeclaredVersion[] declared = [];
        if (Declared(root, project) is var (source, asked, chosen))
        {
            if (chosen is null)
            {
                reason = SandboxStacks.Unsupported("Python", asked, Images);
                return null;
            }

            image = chosen;
            note = SandboxStacks.Declared("Python", source, asked, chosen);
            declared = [new(source, $"Python {asked}")];
        }
        else
        {
            image = Images.First(i => i.Line == DefaultPython);
            note = SandboxStacks.Undeclared("Python", image);
        }

        var install = $"{Venv}/bin/pip install pytest"
            + (requirements ? " -r requirements.txt" : string.Empty)
            + (project ? " -e ." : string.Empty);

        return new SandboxStack(
            SandboxStacks.Python,
            image.Image,
            $"python -m venv {Venv} && {install}",
            $"{Venv}/bin/python -m pytest",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = SandboxStacks.Home,
                ["PIP_CACHE_DIR"] = "/work/cache/pip",
                ["PIP_DISABLE_PIP_VERSION_CHECK"] = "1",
            },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["PIP_NO_INDEX"] = "1" },
            note,
            declared,
            $"Python {image.Version}");
    }

    private static (string Source, string Asked, RuntimeImage? Image)? Declared(string root, bool project)
    {
        if (SandboxStacks.FirstLine(root, ".python-version") is { } line && Pin().IsMatch(line))
        {
            return (".python-version", line, PinImage(line));
        }

        if (project
            && File.ReadLines(Path.Combine(root, "pyproject.toml")).Select(l => RequiresPython().Match(l)).FirstOrDefault(m => m.Success) is { } requires
            && Specifier(requires.Groups[1].Value.Trim()) is { } allows)
        {
            return ("pyproject.toml's requires-python", requires.Groups[1].Value.Trim(), Images.LastOrDefault(i => allows(i.Version)));
        }

        var runtime = SandboxStacks.FirstLine(root, "runtime.txt");
        return runtime is not null && runtime.StartsWith("python-", StringComparison.Ordinal) && Pin().IsMatch(runtime[7..])
            ? ("runtime.txt", runtime[7..], PinImage(runtime[7..]))
            : null;
    }

    private static RuntimeImage? PinImage(string value)
    {
        var match = Pin().Match(value);
        return Images.FirstOrDefault(i => i.Line == $"{int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)}.{int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)}");
    }

    /// <summary>A PEP 440 version specifier set as a test on a version, or null when it is not one this parser reads.</summary>
    internal static Func<Version, bool>? Specifier(string text)
    {
        var tests = new List<Func<Version, bool>>();
        foreach (var clause in text.Split(','))
        {
            var match = Clause().Match(clause);
            if (!match.Success)
            {
                return null;
            }

            var parts = match.Groups[2].Value.Split('.').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            var wildcard = match.Groups[3].Success;
            var exact = SandboxStacks.Numbers(match.Groups[2].Value)!;
            bool Prefix(Version v) => v.Major == parts[0] && (parts.Length < 2 || v.Minor == parts[1]) && (parts.Length < 3 || v.Build == parts[2]);
            Func<Version, bool>? test = match.Groups[1].Value switch
            {
                "==" or "===" => wildcard ? Prefix : v => v == exact,
                "!=" => wildcard ? v => !Prefix(v) : v => v != exact,
                ">=" when !wildcard => v => v >= exact,
                "<=" when !wildcard => v => v <= exact,
                ">" when !wildcard => v => v > exact,
                "<" when !wildcard => v => v < exact,
                "~=" when !wildcard && parts.Length >= 2 => v => v >= exact && v.Major == parts[0] && (parts.Length < 3 || v.Minor == parts[1]),
                _ => null,
            };
            if (test is null)
            {
                return null;
            }

            tests.Add(test);
        }

        return v => tests.All(test => test(v));
    }
}
