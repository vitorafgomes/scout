using Microsoft.Extensions.Options;
using Scout.Plugin.Common;
using Scout.Plugin.Common.Abstractions;
using Scout.Plugin.Notes;
using Scout.Plugin.Notes.Abstractions;
using Scout.Plugin.Notes.Tests.Fakes;

namespace Scout.Plugin.Notes.Tests;

public sealed class NotesPluginTests : IDisposable
{
    private static readonly TimeProvider Clock =
        new FixedTimeProvider(new DateTimeOffset(2026, 7, 3, 9, 30, 0, TimeSpan.Zero));

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "scout-notes-tests", Guid.NewGuid().ToString("N"));

    private IScoutLocalStore Store() =>
        new JsonFileScoutLocalStore(Options.Create(new ScoutLocalStoreOptions { RootDirectory = _dir }));

    private NotesPlugin Plugin(IScoutLocalStore store) => new(store, Clock);

    [Fact]
    public async Task AddNote_Saves_And_Returns_Confirmation_With_Tags()
    {
        var result = await Plugin(Store()).AddNoteAsync("Call the dentist", "health, urgent");

        result.ShouldBe("Note n1 saved (tags: health, urgent).");
    }

    [Fact]
    public async Task AddNote_Without_Tags_Reports_None()
    {
        var result = await Plugin(Store()).AddNoteAsync("Buy milk");

        result.ShouldBe("Note n1 saved (tags: none).");
    }

    [Fact]
    public async Task AddNote_Normalizes_Ragged_Tags()
    {
        var result = await Plugin(Store()).AddNoteAsync("x", " work ,, urgent , ");

        result.ShouldBe("Note n1 saved (tags: work, urgent).");
    }

    [Fact]
    public async Task AddNote_Increments_Id_Across_Calls()
    {
        var plugin = Plugin(Store());

        await plugin.AddNoteAsync("first");
        var second = await plugin.AddNoteAsync("second");

        second.ShouldBe("Note n2 saved (tags: none).");
    }

    [Fact]
    public async Task AddNote_Empty_Text_Returns_Corrective_Hint()
    {
        var result = await Plugin(Store()).AddNoteAsync("   ");

        result.ShouldContain("add_note");
        result.ShouldContain("text");
    }

    [Fact]
    public async Task SearchNotes_Matches_Text_CaseInsensitive()
    {
        var plugin = Plugin(Store());
        await plugin.AddNoteAsync("Dentist appointment", "health");
        await plugin.AddNoteAsync("Grocery list");

        var result = await plugin.SearchNotesAsync("dentist");

        result.ShouldContain("n1");
        result.ShouldContain("Dentist appointment");
        result.ShouldNotContain("Grocery");
    }

    [Fact]
    public async Task SearchNotes_Matches_Tags()
    {
        var plugin = Plugin(Store());
        await plugin.AddNoteAsync("Something to do", "work, urgent");

        var result = await plugin.SearchNotesAsync("URGENT");

        result.ShouldContain("n1");
    }

    [Fact]
    public async Task SearchNotes_NoMatch_Returns_Message()
    {
        var plugin = Plugin(Store());
        await plugin.AddNoteAsync("Buy milk");

        var result = await plugin.SearchNotesAsync("zebra");

        result.ShouldBe("No notes match \"zebra\".");
    }

    [Fact]
    public async Task SearchNotes_Empty_Query_Returns_Corrective_Hint()
    {
        var result = await Plugin(Store()).SearchNotesAsync("  ");

        result.ShouldContain("search_notes");
    }

    [Fact]
    public async Task ListRecent_Returns_Newest_First()
    {
        var plugin = Plugin(Store());
        await plugin.AddNoteAsync("oldest");
        await plugin.AddNoteAsync("middle");
        await plugin.AddNoteAsync("newest");

        var result = await plugin.ListRecentNotesAsync("2");
        var lines = result.Split('\n');

        lines.Length.ShouldBe(2);
        lines[0].ShouldContain("newest");
        lines[1].ShouldContain("middle");
    }

    [Fact]
    public async Task ListRecent_Empty_Returns_Message()
    {
        var result = await Plugin(Store()).ListRecentNotesAsync();

        result.ShouldBe("No notes yet.");
    }

    [Fact]
    public async Task ListRecent_Invalid_Count_Defaults_To_Five()
    {
        var plugin = Plugin(Store());
        for (var i = 0; i < 7; i++)
        {
            await plugin.AddNoteAsync($"note {i}");
        }

        var result = await plugin.ListRecentNotesAsync("not-a-number");

        result.Split('\n').Length.ShouldBe(5);
    }

    [Fact]
    public async Task ListRecent_Count_Below_One_Clamped_To_One()
    {
        var plugin = Plugin(Store());
        await plugin.AddNoteAsync("a");
        await plugin.AddNoteAsync("b");

        var result = await plugin.ListRecentNotesAsync("0");

        result.Split('\n').Length.ShouldBe(1);
        result.ShouldContain("b");
    }

    [Fact]
    public async Task DeleteNote_Removes_And_Confirms()
    {
        var plugin = Plugin(Store());
        await plugin.AddNoteAsync("Buy milk");

        var result = await plugin.DeleteNoteAsync("n1");

        result.ShouldBe("Note n1 deleted.");
        (await plugin.ListRecentNotesAsync()).ShouldBe("No notes yet.");
    }

    [Fact]
    public async Task DeleteNote_Accepts_Bare_Number()
    {
        var plugin = Plugin(Store());
        await plugin.AddNoteAsync("Buy milk");

        var result = await plugin.DeleteNoteAsync("1");

        result.ShouldBe("Note n1 deleted.");
    }

    [Fact]
    public async Task DeleteNote_NotFound_Lists_Existing_Ids()
    {
        var plugin = Plugin(Store());
        await plugin.AddNoteAsync("a");
        await plugin.AddNoteAsync("b");

        var result = await plugin.DeleteNoteAsync("n9");

        result.ShouldContain("No note \"n9\"");
        result.ShouldContain("n1");
        result.ShouldContain("n2");
    }

    [Fact]
    public async Task DeleteNote_NotFound_NoNotes_Reports_Empty()
    {
        var result = await Plugin(Store()).DeleteNoteAsync("n1");

        result.ShouldBe("No note \"n1\". You have no notes yet.");
    }

    [Fact]
    public async Task DeleteNote_Empty_Id_Returns_Corrective_Hint()
    {
        var result = await Plugin(Store()).DeleteNoteAsync("  ");

        result.ShouldContain("delete_note");
    }

    [Fact]
    public async Task AddNote_Store_Failure_Returns_Error_Line_Without_Throwing()
    {
        var store = Substitute.For<IScoutLocalStore>();
        store.MutateAsync(
                Arg.Any<string>(),
                Arg.Any<Action<NotesDocument>>(),
                Arg.Any<CancellationToken>())
            .Returns<NotesDocument>(_ => throw new IOException("disk gone"));

        var plugin = new NotesPlugin(store, Clock);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.AddNoteAsync("hi"));

        result.ShouldBe("Could not access local notes storage. Try again.");
    }

    [Fact]
    public async Task SearchNotes_Cancellation_Returns_Timeout_Line_Without_Throwing()
    {
        var store = Substitute.For<IScoutLocalStore>();
        store.ReadOrCreateAsync<NotesDocument>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<NotesDocument>(_ => throw new OperationCanceledException());

        var plugin = new NotesPlugin(store, Clock);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.SearchNotesAsync("x"));

        result.ShouldBe("The note operation timed out. Try again.");
    }

    [Fact]
    public async Task DeleteNote_Store_Failure_Returns_Error_Line_Without_Throwing()
    {
        var store = Substitute.For<IScoutLocalStore>();
        store.MutateAsync(
                Arg.Any<string>(),
                Arg.Any<Action<NotesDocument>>(),
                Arg.Any<CancellationToken>())
            .Returns<NotesDocument>(_ => throw new UnauthorizedAccessException("denied"));

        var plugin = new NotesPlugin(store, Clock);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.DeleteNoteAsync("n1"));

        result.ShouldBe("Could not access local notes storage. Try again.");
    }

    [Fact]
    public void Ctor_Throws_When_Store_Null()
    {
        Should.Throw<ArgumentNullException>(() => new NotesPlugin(null!, Clock));
    }

    [Fact]
    public void Ctor_Throws_When_Clock_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new NotesPlugin(Substitute.For<IScoutLocalStore>(), null!));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
