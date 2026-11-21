namespace Scout.Audit.Tests;

/// <summary>The definitions of the names a finding's quote calls, found by plain text in the job's snapshot.</summary>
public sealed class DefinitionReaderTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_folder.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>The measured shape: a refusal helper marked as never returning, in another file, three call sites, and a test that calls it too.</summary>
    private void MeasuredShape()
    {
        Write("src/Shop/Guard.cs", "namespace Shop;\n\npublic static class Guard\n{\n    [DoesNotReturn]\n    public static void Refuse(string reason) =>\n        throw new InvalidOperationException(reason);\n}\n");
        Write("src/Shop/Orders.cs",
            "namespace Shop;\n\npublic sealed class Orders\n{\n    public Order Find(int id)\n    {\n        var order = _store.Get(id);\n        if (order is null) Guard.Refuse(\"no order\");\n        return order;\n    }\n\n    public Order Pay(int id)\n    {\n        var order = _store.Get(id);\n        if (order is null)\n        {\n            Guard.Refuse(\"no order\");\n        }\n\n        return order;\n    }\n\n    public Order Ship(int id) => _store.Get(id) ?? Refuse(\"no order\");\n}\n");
        Write("tests/Shop.Tests/GuardTests.cs", "public class GuardTests\n{\n    public static void Refuse(string reason) { }\n    [Fact] public void Refuses() => Guard.Refuse(\"x\");\n}\n");
    }

    [Fact]
    public void D2_A_helper_s_declaration_is_found_in_another_file_with_the_lines_above_it_and_no_call_site_is_taken_for_a_definition()
    {
        MeasuredShape();

        var found = DefinitionReader.For(_folder.Path, ["Refuse"]);

        var refuse = found.Names.ShouldHaveSingleItem();
        refuse.Candidates.Select(c => (c.Path, c.Line)).ShouldBe([("src/Shop/Guard.cs", 6)], "the declaration, never a call site, and never the test file's");
        found.Text().ShouldBe(
            "--- src/Shop/Guard.cs:6\nnamespace Shop;\n\npublic static class Guard\n{\n    [DoesNotReturn]\n    public static void Refuse(string reason) =>\n        throw new InvalidOperationException(reason);\n}\n",
            "the declaration comes with the lines above it, its never-returns attribute among them, and the lines after it");
        (refuse.NotShown, refuse.NotFound, found.Unreadable, found.Cut).ShouldBe((0, false, false, false), "one declaration, nothing left out, nothing cut");
    }

    [Fact]
    public void D3_more_candidates_than_shown_and_nothing_found_are_both_said()
    {
        foreach (var n in Enumerable.Range(1, 5))
        {
            Write($"src/M{n}/Helper.cs", $"public static class H{n}\n{{\n    public static int Helper(int x) => x + {n};\n}}\n");
        }

        var found = DefinitionReader.For(_folder.Path, ["Helper", "Missing"]);

        found.Names.Select(n => (n.Name, n.Candidates.Count, n.NotShown, n.NotFound)).ShouldBe(
            [("Helper", 3, 2, false), ("Missing", 0, 0, true)], "three candidates in path order, the other two counted, and a name with none said as not found");
        found.Names[0].Candidates.Select(c => c.Path).ShouldBe(["src/M1/Helper.cs", "src/M2/Helper.cs", "src/M3/Helper.cs"], "the candidates shown are the first in path order");
        found.Text().ShouldSatisfyAllConditions(
            "the text says both, never leaves them silent",
            t => t.ShouldContain("2 more definitions of Helper were not shown.", Case.Sensitive),
            t => t.ShouldContain("Definition not found for Missing.", Case.Sensitive));
    }

    [Fact]
    public void D4_a_candidate_is_cut_at_3000_characters_and_a_finding_s_definitions_at_12000()
    {
        var body = string.Concat(Enumerable.Range(1, 30).Select(n => $"    // {new string('x', 200)}\n"));
        foreach (var n in Enumerable.Range(1, 8))
        {
            Write($"src/N{n}.cs", $"public static class C{n}\n{{\n    public static void Name{n}(int x)\n    {{\n{body}    }}\n}}\n");
        }

        Write("src/N0.cs", "public static class C0\n{\n    public static void Name0(int x) { }\n}\n");

        var found = DefinitionReader.For(_folder.Path, [.. Enumerable.Range(0, 9).Select(n => $"Name{n}")]);

        var candidates = found.Names.SelectMany(n => n.Candidates).ToList();
        candidates.ShouldAllBe(c => c.Text.Length <= DefinitionReader.MaxCandidateCharacters, "each candidate is cut at 3,000 characters");
        candidates.Sum(c => c.Text.Length).ShouldBeLessThanOrEqualTo(DefinitionReader.MaxFindingCharacters, "a finding's definitions stay within 12,000 characters");
        found.Cut.ShouldBeTrue("the definitions past the bound are said to be cut");
        (DefinitionReader.MaxCandidateCharacters, DefinitionReader.MaxFindingCharacters).ShouldBe((3000, 12000), "the bounds are 3,000 characters per candidate and 12,000 per finding");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    public void A_snapshot_that_cannot_be_read_says_so_and_looks_nothing_up(string how)
    {
        var found = DefinitionReader.For(how == "null" ? null : Path.Combine(_folder.Path, "gone"), ["Refuse"]);

        (found.Unreadable, found.Names.Count).ShouldBe((true, 0), "nothing is looked up in a snapshot that cannot be read");
        found.Text().ShouldBe("The repository's files could not be read, so no definition was looked up.", "an unreadable snapshot is said, never left silent");
    }

    [Theory]
    [InlineData("def refuse(reason):", true)]
    [InlineData("export function refuse(reason: string): never {", true)]
    [InlineData("func refuse(reason string) {", true)]
    [InlineData("func (g *Guard) refuse(reason string) {", true)]
    [InlineData("fn refuse(reason: &str) -> ! {", true)]
    [InlineData("    private static <T> T refuse(String reason) {", true)]
    [InlineData("    refuse(reason: string): never {", true)]
    [InlineData("    public static int refuse(int x) => x + 1;", true)]
    [InlineData("    guard.refuse(reason);", false)]
    [InlineData("    return refuse(reason);", false)]
    [InlineData("    var x = refuse(reason)", false)]
    [InlineData("    await refuse(reason)", false)]
    [InlineData("    items.map(x => refuse(x))", false)]
    [InlineData("    if (bad) refuse(reason)", false)]
    [InlineData("    refuse(reason);", false)]
    [InlineData("    // public void refuse(string reason)", false)]
    [InlineData("    throw refuse(reason)", false)]
    [InlineData("            ? refuse(reason)", false)]
    [InlineData("            : refuse(reason)", false)]
    [InlineData("    var x = ok ? value : refuse(reason)", false)]
    [InlineData("    public Guard(string reason) : refuse(reason)", false)]
    [InlineData("    retry: refuse(reason)", false)]
    [InlineData("    case 1: refuse(reason)", false)]
    [InlineData("    public string? refuse(string reason)", true)]
    [InlineData("    public static Task<int?> refuse(string reason) {", true)]
    [InlineData("    def refuse(self, reason):", true)]
    [InlineData("    async refuse(reason: string): Promise<never> {", true)]
    public void The_declaration_rule_reads_a_line_as_a_declaration_or_not(string line, bool declaration) =>
        DefinitionReader.Declares(line, "refuse").ShouldBe(declaration, $"'{line}' {(declaration ? "declares" : "does not declare")} refuse");

    [Theory]
    [InlineData("                ? NotFound(new { message = error.Message })")]
    [InlineData("            ? NotFound(new { message = $\"No queue named {name}.\" })")]
    public void A_conditional_branch_measured_in_a_review_is_not_a_declaration(string line) =>
        DefinitionReader.Declares(line, "NotFound").ShouldBeFalse($"'{line}' is a conditional's branch that calls NotFound, not its declaration");

    /// <summary>
    /// A file the lookup lists but cannot read: a socket under a source file's name, since a link is skipped and file
    /// permissions mean nothing to root in the test container; reading it fails with an IOException.
    /// </summary>
    private static System.Net.Sockets.Socket Unreadable(string path)
    {
        var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
        socket.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
        return socket;
    }

    [Fact]
    public void A_file_of_the_snapshot_that_cannot_be_read_makes_the_lookup_unreadable()
    {
        MeasuredShape();
        using var socket = Unreadable(Path.Combine(_folder.Path, "src", "Shop", "Sock.cs"));

        var found = DefinitionReader.For(_folder.Path, ["Refuse"]);

        (found.Unreadable, found.Names.Count).ShouldBe((true, 0), "a file the lookup cannot read makes the whole lookup unreadable, never a partial answer");
    }

    [Fact]
    public void A_test_file_that_cannot_be_read_makes_the_test_lookup_not_read()
    {
        MeasuredShape();
        Write("tests/Shop.Tests/GuardTests.cs", "public class GuardTests\n{\n    [Fact] public void Refuses() => Guard.Refuse(\"x\");\n}\n");
        using var socket = Unreadable(Path.Combine(_folder.Path, "tests", "Shop.Tests", "SockTests.cs"));

        DefinitionReader.TestsFor(_folder.Path, "src/Shop/Guard.cs", ["Refuse"])
            .ShouldBe(FoundDefinitions.NotRead, "a test file the lookup cannot read is said, never left silent");
    }
}
