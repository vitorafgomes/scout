namespace Scout.Sandbox;

/// <summary>
/// A stack Scout knows how to test: the pinned image it runs in, the restore command (run with the network), the
/// test command (run without it), the variables that keep every cache under <c>/work</c>, what the result should
/// say about how it ran, the versions the repository declares, the tool and version the image carries, and the
/// sentence naming the tests its own test command leaves out, null when it leaves none out.
/// </summary>
internal sealed record SandboxStack(
    string Name,
    string Image,
    string RestoreCommand,
    string TestCommand,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyDictionary<string, string> OfflineEnvironment,
    string? Note = null,
    IReadOnlyList<SandboxDeclaredVersion>? Declared = null,
    string? Runs = null,
    string? LeftOut = null);

/// <summary>One pinned runtime image: the line a project names (<c>20</c>, <c>3.12</c>, <c>1.24</c>), the exact version the image carries, and the image by digest.</summary>
internal sealed record RuntimeImage(string Line, Version Version, string Image);

/// <summary>The images a run may use, official ones pinned by their multi-architecture index digest.</summary>
internal static class SandboxImages
{
    public const string DotNet = "mcr.microsoft.com/dotnet/sdk:10.0@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e";
    public const string Node = "node:22@sha256:7702d8aa4d5910871692af4480eb3ba86f23047a6417a44d857d06c95d060d3f";
    public const string Python = "python:3.13@sha256:ecbe213fb818d557fd19db4078244339e2e2e52874f9c81052d3094ea114d797";
    public const string Go = "golang:1.25@sha256:699337d620559a59b4a2bb298ad59611e535d2ee755a34cf2d2a98f37578dc80";
    public const string Bun = "oven/bun:1@sha256:1d22548f177f4ff38e0c15baa3c2c46c6c2060ba2d1bc573bb4720b407548f1d";
    public const string Maven8 = "maven:3-eclipse-temurin-8@sha256:c5f4e3545103d845324ab1c541b44bcb931ac28f205835ea1ce5cd9ccec1b2dd";
    public const string Maven11 = "maven:3-eclipse-temurin-11@sha256:433ab11f5f633557a7e5bf7a18c27be315abb44d2e466898a30bd15ee44f7243";
    public const string Maven17 = "maven:3-eclipse-temurin-17@sha256:bec7efd9938efefcf02de53581a19f6816fb6066169a8ea45f52763222ffeb48";
    public const string Maven21 = "maven:3-eclipse-temurin-21@sha256:9673eef9ed503146591667ffb92eaaa22f1b8899ed60d9df0df4da3f3a1214a6";
    public const string Maven25 = "maven:3-eclipse-temurin-25@sha256:047a82fc82c613266cfb1b66fd920be9c8ba51fd67edc68245e4542768f45e36";
    public const string Gradle7Jdk8 = "gradle:7-jdk8@sha256:ec581274b197d53b665373854ed1955a5d186b89b3a440bdac5cfad13532606a";
    public const string Gradle7Jdk11 = "gradle:7-jdk11@sha256:8a25d9da40e2ce1ae24d3ef93bf1c1cb00814c8d9d5611763b20e812b59c85fa";
    public const string Gradle7Jdk17 = "gradle:7-jdk17@sha256:4a69879ab8c693f03bfed585209f221e3d2be20a7cab1e39be7086e89be50ad9";
    public const string Gradle8Jdk8 = "gradle:8-jdk8@sha256:c58ba7d35df44b76998f4fa661331cefde0825b61344da4f2c6b7d216c56ff95";
    public const string Gradle8Jdk11 = "gradle:8-jdk11@sha256:bdd652d6a71711400950d214a3eaf701ff42997e564933265633dd81af000ba7";
    public const string Gradle8Jdk17 = "gradle:8-jdk17@sha256:2f977f0a5b95ade2eabcfcf4840ff4b29fc176c61ee56fd184df038f64b18f9e";
    public const string Gradle8Jdk21 = "gradle:8-jdk21@sha256:930141cf2b9d472f3fd024b2e711856bede114ce116e49fcdfb64427b875982d";
    public const string Gradle9Jdk17 = "gradle:9-jdk17@sha256:55af4a989cc9188c19668b32e52bbd2a0c801da3b849a6e8f5a25e2134845bf5";
    public const string Gradle9Jdk21 = "gradle:9-jdk21@sha256:85f8f572045bfb31874d66d7d8a2bd0c17d8cb9ad34b6a4d02f5d552b4b92c04";
    public const string Gradle9Jdk25 = "gradle:9-jdk25@sha256:e46e15353757ef9c9be0197a243653e88d25ebcd4c3c841057cf839c11fcf898";
    public const string DotNet8 = "mcr.microsoft.com/dotnet/sdk:8.0@sha256:341aa6e8bbaceb1194732328ca27c15bfa9c531687db83a66af3c0c1833f5534";
    public const string DotNet9 = "mcr.microsoft.com/dotnet/sdk:9.0@sha256:07ed28d9e7279248fcea27334880f6c415b42c631c39b8bc5b3caf13ec6fdaa2";
    public const string Node18 = "node:18@sha256:c6ae79e38498325db67193d391e6ec1d224d96c693a8a4d943498556716d3783";
    public const string Node20 = "node:20@sha256:8f693eaa7e0a8e71560c9a82b55fd54c2ae920a2ba5d2cde28bac7d1c01c9ba5";
    public const string Node24 = "node:24@sha256:8fa96264f302f58bbb13aa7339c541547b56975766ce956c875da149452a3e40";
    public const string Node26 = "node:26@sha256:438603bbb5cda404b9416f0b5c8026f61501ab911760d861ec28c36b5d675725";
    public const string Python310 = "python:3.10@sha256:c10791e6f69086d5216f7b5d3b839929c454e24305867ed9bc5152f0355b5423";
    public const string Python311 = "python:3.11@sha256:731670a425deabbc48d39f0ee3115cfa665b2a4002c19939ad3e88b2832b4549";
    public const string Python312 = "python:3.12@sha256:cad37dd20c7cac861af9003af8796d03ea7a998ec1e3a664429740fc9d7d2691";
    public const string Python314 = "python:3.14@sha256:d18055009f327b22993de14458aa5a6bac032cdf459ea2a2e9bbdc3d3d49e4fb";
    public const string Go123 = "golang:1.23@sha256:60deed95d3888cc5e4d9ff8a10c54e5edc008c6ae3fba6187be6fb592e19e8c0";
    public const string Go124 = "golang:1.24@sha256:d2d2bc1c84f7e60d7d2438a3836ae7d0c847f4888464e7ec9ba3a1339a1ee804";
    public const string Go126 = "golang:1.26@sha256:cfa27959837144bf4649802ba38e59daffd0dd2a7994aa83daaf113285b0f551";

    public static readonly IReadOnlyList<string> All =
    [
        DotNet, Node, Python, Go, Bun,
        Maven8, Maven11, Maven17, Maven21, Maven25,
        Gradle7Jdk8, Gradle7Jdk11, Gradle7Jdk17,
        Gradle8Jdk8, Gradle8Jdk11, Gradle8Jdk17, Gradle8Jdk21,
        Gradle9Jdk17, Gradle9Jdk21, Gradle9Jdk25,
        DotNet8, DotNet9,
        Node18, Node20, Node24, Node26,
        Python310, Python311, Python312, Python314,
        Go123, Go124, Go126,
    ];
}

/// <summary>
/// Picks the stack from the manifests at the snapshot's root, in the order .NET, Node, Python, Go, Java, or the one a
/// hint names, and builds its restore and test commands; never an image or a command from the repository's own files,
/// only the version the repository declares, mapped to a pinned image or refused with a sentence naming both.
/// </summary>
internal static class SandboxStacks
{
    public const string DotNet = "dotnet";
    public const string Node = "node";
    public const string Python = "python";
    public const string Go = "go";
    public const string Java = "java";

    public const string NoStackReason = "No .NET, Node, Python, Go or Java project was found at the repository's root.";

    internal const string Home = "/work/cache/home";

    private static readonly string[] Order = [DotNet, Node, Python, Go, Java];

    /// <summary>The stack to run, or null with the sentence that says why none runs.</summary>
    public static SandboxStack? Detect(string root, string? hint, out string reason) => Detect(root, hint, out reason, out _);

    /// <summary>The stack to run, or null with the sentence that says why none runs and the stack that refused, null when no manifest was found.</summary>
    public static SandboxStack? Detect(string root, string? hint, out string reason, out string? refused)
    {
        refused = null;
        reason = NoStackReason;
        string[] candidates = Order;
        if (!string.IsNullOrWhiteSpace(hint))
        {
            var wanted = hint.Trim().ToLowerInvariant();
            if (!Order.Contains(wanted))
            {
                reason = $"Scout runs .NET, Node, Python, Go or Java tests, not '{hint.Trim()}'.";
                return null;
            }

            candidates = [wanted];
            reason = $"No {Label(wanted)} project was found at the repository's root.";
        }

        foreach (var name in candidates)
        {
            var before = reason;
            var stack = name switch
            {
                DotNet => SandboxDotNet.Detect(root, ref reason),
                Node => SandboxNode.Detect(root, ref reason),
                Python => SandboxPython.Detect(root, ref reason),
                Go => SandboxGo.Detect(root, ref reason),
                _ => SandboxJava.Detect(root, ref reason),
            };
            if (stack is not null || reason != before)
            {
                refused = stack is null ? name : null;
                return stack;
            }
        }

        return null;
    }

    /// <summary>A value as one single-quoted word for <c>sh -c</c>.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>The stack's name as the owner reads it: .NET, Node, Python, Go or Java.</summary>
    public static string Label(string name) => name switch
    {
        DotNet => ".NET",
        Node => "Node",
        Python => "Python",
        Go => "Go",
        _ => "Java",
    };

    internal static bool Has(string root, string file) => File.Exists(Path.Combine(root, file));

    /// <summary>"a", "a and b", "a, b and c".</summary>
    internal static string Join(IEnumerable<string> values)
    {
        var list = values.Distinct().ToList();
        return list.Count == 1 ? list[0] : $"{string.Join(", ", list[..^1])} and {list[^1]}";
    }

    /// <summary>The first line of a root file that is neither blank nor a <c>#</c> comment, trimmed; null without one.</summary>
    internal static string? FirstLine(string root, string file) =>
        Has(root, file)
            ? File.ReadLines(Path.Combine(root, file)).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'))
            : null;

    /// <summary>A version of up to three numbers, the missing ones zero; null when it is not one.</summary>
    internal static Version? Numbers(string value)
    {
        var parts = value.Split('.');
        if (parts.Length is 0 or > 3 || parts.Any(p => p.Length is 0 or > 6 || !p.All(char.IsAsciiDigit)))
        {
            return null;
        }

        var n = parts.Select(p => int.Parse(p, System.Globalization.CultureInfo.InvariantCulture)).Concat([0, 0]).ToArray();
        return new Version(n[0], n[1], n[2]);
    }

    internal static string Declared(string tool, string source, string asked, RuntimeImage image) =>
        $"{source} asks for {tool} {asked}; ran on {tool} {image.Version}.";

    internal static string Undeclared(string tool, RuntimeImage image) =>
        $"No {tool} version was declared; ran on {tool} {image.Version}.";

    internal static string Unsupported(string tool, string asked, IEnumerable<RuntimeImage> images) =>
        $"This project asks for {tool} {asked}; Scout runs {tool} {Join(images.Select(i => i.Line))}.";
}
