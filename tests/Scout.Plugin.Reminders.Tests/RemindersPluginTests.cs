using System.Globalization;
using Microsoft.Extensions.Options;
using Scout.Plugin.Common;
using Scout.Plugin.Common.Abstractions;
using Scout.Plugin.Reminders;
using Scout.Plugin.Reminders.Abstractions;
using Scout.Plugin.Reminders.Tests.Fakes;

namespace Scout.Plugin.Reminders.Tests;

public sealed class RemindersPluginTests : IDisposable
{
    private static readonly TimeProvider Clock =
        new FixedTimeProvider(new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero));

    private static readonly RemindersOptions Options = new() { TimeZone = "UTC" };

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "scout-reminders-tests", Guid.NewGuid().ToString("N"));

    private IScoutLocalStore Store() =>
        new JsonFileScoutLocalStore(Microsoft.Extensions.Options.Options.Create(
            new ScoutLocalStoreOptions { RootDirectory = _dir }));

    private RemindersPlugin Plugin(IScoutLocalStore store) => new(store, Clock, Options);

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task AddReminder_With_Date_And_Time_Confirms_With_Due()
    {
        RelaxedDate.TryParse("friday", Clock, "UTC", out var friday).ShouldBeTrue();

        var result = await Plugin(Store()).AddReminderAsync("Call the dentist", "friday 18:00");

        result.ShouldBe($"Reminder r1 saved for {Iso(friday)} 18:00.");
    }

    [Fact]
    public async Task AddReminder_Undated_Confirms_Without_Due()
    {
        var result = await Plugin(Store()).AddReminderAsync("Buy milk");

        result.ShouldBe("Reminder r1 saved.");
    }

    [Fact]
    public async Task AddReminder_Daily_Recurrence_Anchors_To_Today()
    {
        var today = RelaxedDate.Today(Clock, "UTC");

        var result = await Plugin(Store()).AddReminderAsync("Take vitamins", "every day 08:00");

        result.ShouldBe($"Reminder r1 saved for {Iso(today)} 08:00 (daily).");
    }

    [Fact]
    public async Task AddReminder_Portuguese_Daily_Recurrence()
    {
        var today = RelaxedDate.Today(Clock, "UTC");

        var result = await Plugin(Store()).AddReminderAsync("Beber agua", "todos os dias 09:00");

        result.ShouldBe($"Reminder r1 saved for {Iso(today)} 09:00 (daily).");
    }

    [Fact]
    public async Task AddReminder_Weekly_Recurrence_Uses_Weekday_Anchor()
    {
        RelaxedDate.TryParse("friday", Clock, "UTC", out var friday).ShouldBeTrue();

        var result = await Plugin(Store()).AddReminderAsync("Team sync", "every friday 15:30");

        result.ShouldBe($"Reminder r1 saved for {Iso(friday)} 15:30 (weekly).");
    }

    [Fact]
    public async Task AddReminder_Portuguese_Relative_Day()
    {
        var tomorrow = RelaxedDate.Today(Clock, "UTC").AddDays(1);

        var result = await Plugin(Store()).AddReminderAsync("Ligar para o banco", "amanha");

        result.ShouldBe($"Reminder r1 saved for {Iso(tomorrow)}.");
    }

    [Fact]
    public async Task AddReminder_Day_Month_Without_Year()
    {
        var result = await Plugin(Store()).AddReminderAsync("Wrap gifts", "25 Dec");

        result.ShouldBe("Reminder r1 saved for 2026-12-25.");
    }

    [Fact]
    public async Task AddReminder_Unparseable_When_Returns_Corrective_Hint()
    {
        var result = await Plugin(Store()).AddReminderAsync("Do the thing", "sometime soon-ish");

        result.ShouldContain("sometime soon-ish");
        result.ShouldContain("tomorrow");
    }

    [Fact]
    public async Task AddReminder_Empty_Text_Returns_Corrective_Hint()
    {
        var result = await Plugin(Store()).AddReminderAsync("   ", "tomorrow");

        result.ShouldContain("add_reminder");
        result.ShouldContain("text");
    }

    [Fact]
    public async Task AddReminder_Increments_Id_Across_Calls()
    {
        var plugin = Plugin(Store());

        await plugin.AddReminderAsync("first", "tomorrow");
        var second = await plugin.AddReminderAsync("second", "tomorrow");

        second.ShouldContain("r2");
    }

    [Fact]
    public async Task ListReminders_Empty_Open_Returns_Explicit_Sentence()
    {
        var result = await Plugin(Store()).ListRemindersAsync();

        result.ShouldBe("No open reminders.");
    }

    [Fact]
    public async Task ListReminders_Empty_All_Returns_Explicit_Sentence()
    {
        var result = await Plugin(Store()).ListRemindersAsync("all");

        result.ShouldBe("No reminders yet.");
    }

    [Fact]
    public async Task ListReminders_Empty_Done_Returns_Explicit_Sentence()
    {
        var result = await Plugin(Store()).ListRemindersAsync("done");

        result.ShouldBe("No completed reminders.");
    }

    [Fact]
    public async Task ListReminders_Open_Excludes_Completed()
    {
        var plugin = Plugin(Store());
        await plugin.AddReminderAsync("Open one", "tomorrow");
        await plugin.AddReminderAsync("Done one", "tomorrow");
        await plugin.CompleteReminderAsync("r2");

        var result = await plugin.ListRemindersAsync("open");

        result.ShouldContain("Open one");
        result.ShouldNotContain("Done one");
    }

    [Fact]
    public async Task ListReminders_Sorts_By_Due_Soonest_First()
    {
        var plugin = Plugin(Store());
        await plugin.AddReminderAsync("Later", "friday");
        await plugin.AddReminderAsync("Sooner", "tomorrow");

        var lines = (await plugin.ListRemindersAsync("open")).Split('\n');

        lines[0].ShouldContain("Sooner");
        lines[1].ShouldContain("Later");
    }

    [Fact]
    public async Task ListReminders_Shows_Recurrence_Marker()
    {
        var plugin = Plugin(Store());
        await plugin.AddReminderAsync("Standup", "every day 09:00");

        var result = await plugin.ListRemindersAsync("open");

        result.ShouldContain("[daily]");
    }

    [Fact]
    public async Task ListReminders_Done_Marks_Completed()
    {
        var plugin = Plugin(Store());
        await plugin.AddReminderAsync("Finish report", "tomorrow");
        await plugin.CompleteReminderAsync("r1");

        var result = await plugin.ListRemindersAsync("done");

        result.ShouldContain("Finish report");
        result.ShouldContain("(done)");
    }

    [Fact]
    public async Task ListReminders_Invalid_Filter_Returns_Corrective_Hint()
    {
        var result = await Plugin(Store()).ListRemindersAsync("banana");

        result.ShouldContain("open");
        result.ShouldContain("done");
        result.ShouldContain("all");
    }

    [Fact]
    public async Task CompleteReminder_Marks_Done_And_Confirms()
    {
        var plugin = Plugin(Store());
        await plugin.AddReminderAsync("Pay rent", "tomorrow");

        var result = await plugin.CompleteReminderAsync("r1");

        result.ShouldBe("Reminder r1 marked done.");
    }

    [Fact]
    public async Task CompleteReminder_Accepts_Bare_Number()
    {
        var plugin = Plugin(Store());
        await plugin.AddReminderAsync("Pay rent", "tomorrow");

        var result = await plugin.CompleteReminderAsync("1");

        result.ShouldBe("Reminder r1 marked done.");
    }

    [Fact]
    public async Task CompleteReminder_Already_Done_Reports_It()
    {
        var plugin = Plugin(Store());
        await plugin.AddReminderAsync("Pay rent", "tomorrow");
        await plugin.CompleteReminderAsync("r1");

        var result = await plugin.CompleteReminderAsync("r1");

        result.ShouldBe("Reminder r1 was already done.");
    }

    [Fact]
    public async Task CompleteReminder_NotFound_Lists_Existing_Ids()
    {
        var plugin = Plugin(Store());
        await plugin.AddReminderAsync("a", "tomorrow");

        var result = await plugin.CompleteReminderAsync("r9");

        result.ShouldContain("No reminder \"r9\"");
        result.ShouldContain("r1");
    }

    [Fact]
    public async Task CompleteReminder_NotFound_NoReminders_Reports_Empty()
    {
        var result = await Plugin(Store()).CompleteReminderAsync("r1");

        result.ShouldBe("No reminder \"r1\". You have no reminders yet.");
    }

    [Fact]
    public async Task CompleteReminder_Empty_Id_Returns_Corrective_Hint()
    {
        var result = await Plugin(Store()).CompleteReminderAsync("  ");

        result.ShouldContain("complete_reminder");
    }

    [Fact]
    public async Task DeleteReminder_Removes_And_Confirms()
    {
        var plugin = Plugin(Store());
        await plugin.AddReminderAsync("Temporary", "tomorrow");

        var result = await plugin.DeleteReminderAsync("r1");

        result.ShouldBe("Reminder r1 deleted.");
        (await plugin.ListRemindersAsync("all")).ShouldBe("No reminders yet.");
    }

    [Fact]
    public async Task DeleteReminder_Accepts_Bare_Number()
    {
        var plugin = Plugin(Store());
        await plugin.AddReminderAsync("Temporary", "tomorrow");

        var result = await plugin.DeleteReminderAsync("1");

        result.ShouldBe("Reminder r1 deleted.");
    }

    [Fact]
    public async Task DeleteReminder_NotFound_Lists_Existing_Ids()
    {
        var plugin = Plugin(Store());
        await plugin.AddReminderAsync("a", "tomorrow");
        await plugin.AddReminderAsync("b", "tomorrow");

        var result = await plugin.DeleteReminderAsync("r9");

        result.ShouldContain("No reminder \"r9\"");
        result.ShouldContain("r1");
        result.ShouldContain("r2");
    }

    [Fact]
    public async Task DeleteReminder_Empty_Id_Returns_Corrective_Hint()
    {
        var result = await Plugin(Store()).DeleteReminderAsync("  ");

        result.ShouldContain("delete_reminder");
    }

    [Fact]
    public async Task ListReminders_Corrupted_Store_Recovers_To_Empty()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "reminders.json"), "{ this is not valid json");

        var result = await Plugin(Store()).ListRemindersAsync("open");

        result.ShouldBe("No open reminders.");
    }

    [Fact]
    public async Task AddReminder_Store_Write_Failure_Returns_Error_Line_Without_Throwing()
    {
        var store = Substitute.For<IScoutLocalStore>();
        store.MutateAsync(
                Arg.Any<string>(),
                Arg.Any<Action<RemindersDocument>>(),
                Arg.Any<CancellationToken>())
            .Returns<RemindersDocument>(_ => throw new IOException("disk full"));

        var plugin = new RemindersPlugin(store, Clock, Options);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.AddReminderAsync("hi", "tomorrow"));

        result.ShouldBe("Could not access local reminders storage. Try again.");
    }

    [Fact]
    public async Task ListReminders_Cancellation_Returns_Timeout_Line_Without_Throwing()
    {
        var store = Substitute.For<IScoutLocalStore>();
        store.ReadOrCreateAsync<RemindersDocument>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<RemindersDocument>(_ => throw new OperationCanceledException());

        var plugin = new RemindersPlugin(store, Clock, Options);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.ListRemindersAsync("open"));

        result.ShouldBe("The reminders operation timed out. Try again.");
    }

    [Fact]
    public async Task CompleteReminder_Store_Failure_Returns_Error_Line_Without_Throwing()
    {
        var store = Substitute.For<IScoutLocalStore>();
        store.MutateAsync(
                Arg.Any<string>(),
                Arg.Any<Action<RemindersDocument>>(),
                Arg.Any<CancellationToken>())
            .Returns<RemindersDocument>(_ => throw new UnauthorizedAccessException("denied"));

        var plugin = new RemindersPlugin(store, Clock, Options);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.CompleteReminderAsync("r1"));

        result.ShouldBe("Could not access local reminders storage. Try again.");
    }

    [Fact]
    public void Ctor_Throws_When_Store_Null()
    {
        Should.Throw<ArgumentNullException>(() => new RemindersPlugin(null!, Clock, Options));
    }

    [Fact]
    public void Ctor_Throws_When_Clock_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new RemindersPlugin(Substitute.For<IScoutLocalStore>(), null!, Options));
    }

    [Fact]
    public void Ctor_Throws_When_Options_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new RemindersPlugin(Substitute.For<IScoutLocalStore>(), Clock, null!));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
