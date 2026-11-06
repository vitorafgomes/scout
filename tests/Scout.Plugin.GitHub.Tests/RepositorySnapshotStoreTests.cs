using Microsoft.Extensions.Time.Testing;
using Scout.Plugin.GitHub.Abstractions;
using Scout.Plugin.GitHub.Tests.Fakes;

namespace Scout.Plugin.GitHub.Tests;

public sealed class RepositorySnapshotStoreTests : IDisposable
{
    private static readonly RepositoryCommit Main = new("main", "abc1234def5678", IsDefaultBranch: true);

    private readonly string _dir = Directory.CreateTempSubdirectory("scout-snapshots-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 6, 15, 0, 0, TimeSpan.Zero));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private RepositorySnapshotStore Store(long maxBytes = 0) => new(_dir, maxBytes, _time);

    private static byte[] Tree() => TarGzFixture.Build(
        TarGzFixture.Text("src/Checkout.cs", "class Checkout { void Total() { } }\n"),
        TarGzFixture.Text("README.md", "# Hello\n"),
        TarGzFixture.Text("snapshot.json", "{\"theirs\":true}"),
        ("img/logo.png", [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]),
        TarGzFixture.Text(".git/config", "[core]\n"),
        TarGzFixture.Text("/octocat-hello-abc1234/../escape.txt", "no"),
        TarGzFixture.Text("/etc/passwd", "no"));

    [Fact]
    public async Task A_Tarball_Is_Extracted_As_Plain_Files_Only_Never_Git_Nor_A_Path_That_Climbs_Out()
    {
        var store = Store();

        var snapshot = await store.ExtractAsync("octocat", "hello", Main, new MemoryStream(Tree()), CancellationToken.None);

        snapshot.FileCount.ShouldBe(4);
        snapshot.Root.ShouldBe(Path.Combine(_dir, "octocat__hello", Main.Sha));
        RepositorySnapshotStore.Files(snapshot).Select(f => f.Path).ShouldBe(["README.md", "img/logo.png", "snapshot.json", "src/Checkout.cs"]);
        File.ReadAllText(Path.Combine(snapshot.Root, "snapshot.json")).ShouldBe("{\"theirs\":true}");
        File.Exists(snapshot.Root + ".json").ShouldBeTrue();
        Directory.Exists(Path.Combine(snapshot.Root, ".git")).ShouldBeFalse();
        File.Exists(Path.Combine(_dir, "escape.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(_dir, "octocat__hello", "escape.txt")).ShouldBeFalse();
        Directory.GetDirectories(Path.Combine(_dir, "octocat__hello")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_Snapshot_Is_Found_By_Its_Default_Branch_Its_Reference_Or_Its_Sha_And_Read_Back()
    {
        var store = Store();
        await store.ExtractAsync("octocat", "hello", Main, new MemoryStream(Tree()), CancellationToken.None);

        var byDefault = store.Find("octocat", "hello", "").ShouldNotBeNull();
        store.Find("octocat", "hello", "MAIN").ShouldNotBeNull().Sha.ShouldBe(Main.Sha);
        store.Find("octocat", "hello", Main.Sha).ShouldNotBeNull();
        store.Find("octocat", "hello", "feat/x").ShouldBeNull();
        store.Find("octocat", "other", "").ShouldBeNull();
        byDefault.Label.ShouldBe("octocat/hello at main (abc1234)");
        RepositorySnapshotStore.ReadText(byDefault, "src/Checkout.cs", out var size).ShouldBe("class Checkout { void Total() { } }\n");
        size.ShouldBe(36);
        RepositorySnapshotStore.ReadText(byDefault, "img/logo.png", out var binary).ShouldBeNull();
        binary.ShouldBe(6);
        RepositorySnapshotStore.ReadText(byDefault, "missing.cs", out var none).ShouldBeNull();
        none.ShouldBe(0);
        RepositorySnapshotStore.ReadText(byDefault, "../" + Main.Sha + ".json", out _).ShouldBeNull();
        RepositorySnapshotStore.ListDirectory(byDefault, "src").ShouldBe(["Checkout.cs"]);
        RepositorySnapshotStore.ListDirectory(byDefault, "").ShouldBe(["README.md", "img", "snapshot.json", "src"]);
        RepositorySnapshotStore.ListDirectory(byDefault, "src/Checkout.cs").ShouldBeNull();
    }

    [Fact]
    public async Task A_Search_Reads_Every_Text_File_And_Returns_The_Matching_Lines_Under_A_Folder_Or_An_Extension()
    {
        var store = Store();
        var snapshot = await store.ExtractAsync("octocat", "hello", Main, new MemoryStream(Tree()), CancellationToken.None);

        var all = RepositorySnapshotStore.Search(snapshot, "total", "", "");
        var underSrc = RepositorySnapshotStore.Search(snapshot, "hello", "src", "");
        var markdown = RepositorySnapshotStore.Search(snapshot, "hello", "", "md");

        all.TotalCount.ShouldBe(1);
        var hit = all.Hits.ShouldHaveSingleItem();
        hit.Path.ShouldBe("src/Checkout.cs");
        hit.Fragments.ShouldBe(["class Checkout { void Total() { } }"]);
        underSrc.TotalCount.ShouldBe(0);
        markdown.Hits.Select(h => h.Path).ShouldBe(["README.md"]);
    }

    [Fact]
    public async Task A_Tree_Over_The_Cap_Is_Refused_And_Leaves_Nothing_Behind()
    {
        var store = Store(maxBytes: 20);

        var ex = await Should.ThrowAsync<SnapshotRefusedException>(() => store.ExtractAsync("octocat", "hello", Main, new MemoryStream(Tree()), CancellationToken.None));

        ex.Message.ShouldContain("over the snapshot cap");
        Directory.Exists(Path.Combine(_dir, "octocat__hello", Main.Sha)).ShouldBeFalse();
        Directory.GetDirectories(Path.Combine(_dir, "octocat__hello")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_New_Snapshot_Replaces_The_Same_Commit_And_Purges_The_Repository_Snapshots_Older_Than_Seven_Days()
    {
        var store = Store();
        var old = new RepositoryCommit("v1", "0ld0ld0ld", IsDefaultBranch: false);
        var oldSnapshot = await store.ExtractAsync("octocat", "hello", old, new MemoryStream(Tree()), CancellationToken.None);
        Directory.SetLastWriteTimeUtc(oldSnapshot.Root, _time.GetUtcNow().UtcDateTime.AddDays(-8));
        var recent = new RepositoryCommit("feat/x", "f3a7f3a7", IsDefaultBranch: false);
        var recentSnapshot = await store.ExtractAsync("octocat", "hello", recent, new MemoryStream(Tree()), CancellationToken.None);
        Directory.SetLastWriteTimeUtc(recentSnapshot.Root, _time.GetUtcNow().UtcDateTime.AddDays(-1));

        var inFlight = Path.Combine(_dir, "octocat__hello", "0ther0ther.extracting");
        Directory.CreateDirectory(inFlight);
        _time.Advance(TimeSpan.FromMinutes(1));

        await store.ExtractAsync("octocat", "hello", Main, new MemoryStream(Tree()), CancellationToken.None);
        var again = await store.ExtractAsync("octocat", "hello", Main, new MemoryStream(TarGzFixture.Build(TarGzFixture.Text("only.txt", "x"))), CancellationToken.None);

        Directory.GetDirectories(Path.Combine(_dir, "octocat__hello")).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(["0ther0ther.extracting", Main.Sha, recent.Sha]);
        File.Exists(oldSnapshot.Root + ".json").ShouldBeFalse();
        RepositorySnapshotStore.Files(again).Select(f => f.Path).ShouldBe(["only.txt"]);
        store.FindBySha("octocat", "hello", old.Sha).ShouldBeNull();
        store.Find("octocat", "hello", "feat/x").ShouldNotBeNull();
        store.FindNewest("octocat", "hello").ShouldNotBeNull().Sha.ShouldBe(Main.Sha);
    }

    [Theory]
    [InlineData("top/src/A.cs", "src/A.cs")]
    [InlineData("top/.git/config", null)]
    [InlineData("top/../x", null)]
    [InlineData("top/a/./b", null)]
    [InlineData("top", null)]
    [InlineData("top/c:/x", null)]
    [InlineData("etc/passwd", null)]
    public void An_Entry_Path_Loses_The_Top_Folder_And_Is_Refused_When_It_Is_Not_A_Plain_Relative_Path_Under_It(string entry, string? expected) =>
        RepositorySnapshotStore.RelativePath(entry, "top").ShouldBe(expected);

    [Fact]
    public async Task A_Snapshot_Found_By_Its_Commit_Can_Be_Relabelled_As_Another_Reference_Of_The_Same_Tree()
    {
        var store = Store();
        var tagged = await store.ExtractAsync("octocat", "hello", new RepositoryCommit("v1.2", Main.Sha, IsDefaultBranch: false), new MemoryStream(Tree()), CancellationToken.None);
        store.Find("octocat", "hello", "").ShouldBeNull();

        var relabelled = await RepositorySnapshotStore.RelabelAsync(tagged, Main, CancellationToken.None);

        relabelled.Reference.ShouldBe("main");
        store.Find("octocat", "hello", "").ShouldNotBeNull().Root.ShouldBe(tagged.Root);
        store.Find("octocat", "hello", "v1.2").ShouldBeNull();
        RepositorySnapshotStore.ReadText(relabelled, "README.md", out _).ShouldBe("# Hello\n");
    }

    [Fact]
    public async Task A_Description_With_A_Short_Or_Missing_Commit_Is_Ignored_Rather_Than_Served()
    {
        var store = Store();
        var snapshot = await store.ExtractAsync("octocat", "hello", Main, new MemoryStream(Tree()), CancellationToken.None);
        await File.WriteAllTextAsync(snapshot.Root + ".json", """{"owner":"octocat","repo":"hello","reference":"main","sha":"ab","isDefaultBranch":true,"takenAt":"2026-11-06T15:00:00+00:00","fileCount":1,"bytes":1}""");
        await File.WriteAllTextAsync(Path.Combine(_dir, "octocat__hello", "junk.json"), "not json");

        store.Find("octocat", "hello", "").ShouldBeNull();
        store.FindNewest("octocat", "hello").ShouldBeNull();
    }
}
