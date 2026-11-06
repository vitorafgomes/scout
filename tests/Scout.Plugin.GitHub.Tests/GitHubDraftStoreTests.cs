using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub.Tests;

public sealed class GitHubDraftStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("scout-github-drafts-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static GitHubDraft Review(string id, DateTimeOffset at, string? actionId = null) => new(
        id, GitHubDraft.ReviewKind, "octocat", "hello", 42, string.Empty, "Ready after one fix.",
        [new ReviewComment("src/Checkout.cs", 88, "Summed before the discount.")], [], "discord:1/2", at, actionId);

    [Fact]
    public async Task A_Draft_Round_Trips_And_Is_Found_By_Its_Action()
    {
        var store = new GitHubDraftStore(_dir);
        var now = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
        await store.SaveAsync(Review("d1", now), CancellationToken.None);
        await store.SaveAsync(Review("d1", now, actionId: "act-9"), CancellationToken.None);
        await store.SaveAsync(Review("d2", now, actionId: "act-10"), CancellationToken.None);

        var byAction = await store.FindByActionAsync("act-9", CancellationToken.None);
        var byId = await store.FindAsync("d2", CancellationToken.None);

        byAction.ShouldNotBeNull().Id.ShouldBe("d1");
        byAction.Comments.Single().ShouldBe(new ReviewComment("src/Checkout.cs", 88, "Summed before the discount."));
        byAction.OriginReference.ShouldBe("discord:1/2");
        byAction.Target.ShouldBe("octocat/hello#42");
        byId.ShouldNotBeNull().ActionId.ShouldBe("act-10");
        (await store.FindByActionAsync("act-none", CancellationToken.None)).ShouldBeNull();
        Directory.GetFiles(_dir, "*.tmp").ShouldBeEmpty("a save leaves no temporary file behind");
    }

    [Fact]
    public async Task Two_Saves_Of_The_Same_Document_At_Once_Both_Land()
    {
        var store = new GitHubDraftStore(_dir);
        var now = DateTimeOffset.UtcNow;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => store.SaveAsync(Review("same", now, $"act-{i}"), CancellationToken.None)));

        (await store.FindAsync("same", CancellationToken.None)).ShouldNotBeNull().ActionId.ShouldStartWith("act-");
        Directory.GetFiles(_dir, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public async Task Purge_Deletes_Drafts_Older_Than_A_Week_And_Unreadable_Files_And_Keeps_The_Rest()
    {
        var store = new GitHubDraftStore(_dir);
        var now = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
        await store.SaveAsync(Review("old", now - TimeSpan.FromDays(8)), CancellationToken.None);
        await store.SaveAsync(Review("fresh", now - TimeSpan.FromDays(6)), CancellationToken.None);
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ not json");

        var purged = await store.PurgeAsync(now, CancellationToken.None);

        purged.ShouldBe(2);
        (await store.FindAsync("old", CancellationToken.None)).ShouldBeNull();
        (await store.FindAsync("fresh", CancellationToken.None)).ShouldNotBeNull();
        File.Exists(Path.Combine(_dir, "broken.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_Removes_The_File_And_A_Missing_Folder_Reads_As_Empty()
    {
        var store = new GitHubDraftStore(Path.Combine(_dir, "nested", "drafts"));
        (await store.FindByActionAsync("x", CancellationToken.None)).ShouldBeNull();
        (await store.PurgeAsync(DateTimeOffset.UtcNow, CancellationToken.None)).ShouldBe(0);

        await store.SaveAsync(Review("d", DateTimeOffset.UtcNow), CancellationToken.None);
        await store.DeleteAsync("d");

        (await store.FindAsync("d", CancellationToken.None)).ShouldBeNull();
    }
}
