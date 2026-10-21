using Microsoft.Extensions.Options;
using Scout.Plugin.Common;
using Scout.Plugin.Common.Abstractions;
using Scout.Plugin.Ledger;
using Scout.Plugin.Ledger.Abstractions;
using Scout.Plugin.Ledger.Tests.Fakes;

namespace Scout.Plugin.Ledger.Tests;

public sealed class LedgerPluginTests : IDisposable
{
    // 2026-07-04 09:30 UTC -> Europe/London (BST +1) is 10:30 on 2026-07-04, July.
    private static readonly TimeProvider Clock =
        new FixedTimeProvider(new DateTimeOffset(2026, 7, 4, 9, 30, 0, TimeSpan.Zero));

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "scout-ledger-tests", Guid.NewGuid().ToString("N"));

    private JsonFileScoutLocalStore Store() =>
        new(Options.Create(new ScoutLocalStoreOptions { RootDirectory = _dir }));

    private LedgerPlugin Plugin(IScoutLocalStore store) =>
        new(store, Clock, Options.Create(new LedgerOptions()));

    // record_expense ------------------------------------------------------

    [Fact]
    public async Task RecordExpense_Parses_Pound_Symbol_And_Confirms()
    {
        var result = await Plugin(Store()).RecordExpenseAsync("£23", "groceries");

        result.ShouldBe("Recorded expense #1: GBP 23.00 (groceries).");
    }

    [Fact]
    public async Task RecordExpense_Parses_Comma_Decimal_Using_Default_Currency()
    {
        var result = await Plugin(Store()).RecordExpenseAsync("23,50", "lunch");

        result.ShouldBe("Recorded expense #1: GBP 23.50 (lunch).");
    }

    [Fact]
    public async Task RecordExpense_Parses_Real_Symbol_As_BRL()
    {
        var result = await Plugin(Store()).RecordExpenseAsync("R$ 120", "mercado");

        result.ShouldBe("Recorded expense #1: BRL 120.00 (mercado).");
    }

    [Fact]
    public async Task RecordExpense_Parses_Thousands_Separator()
    {
        var result = await Plugin(Store()).RecordExpenseAsync("1,000", "rent");

        result.ShouldBe("Recorded expense #1: GBP 1000.00 (rent).");
    }

    [Fact]
    public async Task RecordExpense_Appends_Note()
    {
        var result = await Plugin(Store()).RecordExpenseAsync("£10", "coffee", "flat white");

        result.ShouldBe("Recorded expense #1: GBP 10.00 (coffee). Note: flat white.");
    }

    [Fact]
    public async Task RecordExpense_Increments_Id_Across_Calls()
    {
        var plugin = Plugin(Store());

        await plugin.RecordExpenseAsync("£1", "a");
        var second = await plugin.RecordExpenseAsync("£2", "b");

        second.ShouldBe("Recorded expense #2: GBP 2.00 (b).");
    }

    [Fact]
    public async Task RecordExpense_Empty_Amount_Returns_Corrective_Hint()
    {
        var result = await Plugin(Store()).RecordExpenseAsync("  ", "groceries");

        result.ShouldContain("record_expense");
        result.ShouldContain("amount");
    }

    [Fact]
    public async Task RecordExpense_Empty_Category_Returns_Corrective_Hint()
    {
        var result = await Plugin(Store()).RecordExpenseAsync("£23", "  ");

        result.ShouldContain("category");
    }

    [Fact]
    public async Task RecordExpense_Unparseable_Amount_Returns_Corrective_Hint()
    {
        var result = await Plugin(Store()).RecordExpenseAsync("abc", "groceries");

        result.ShouldBe("Could not read the amount. Use a number like \"£23\", \"23,50\" or \"R$ 120\".");
    }

    [Fact]
    public async Task RecordExpense_Zero_Amount_Returns_Corrective_Hint()
    {
        var result = await Plugin(Store()).RecordExpenseAsync("£0", "groceries");

        result.ShouldContain("Could not read the amount");
    }

    // record_income -------------------------------------------------------

    [Fact]
    public async Task RecordIncome_Parses_And_Confirms()
    {
        var result = await Plugin(Store()).RecordIncomeAsync("£1500", "salary");

        result.ShouldBe("Recorded income #1: GBP 1500.00 from salary.");
    }

    [Fact]
    public async Task RecordIncome_Parses_European_Grouping_And_Decimal()
    {
        var result = await Plugin(Store()).RecordIncomeAsync("1.500,00", "salary");

        result.ShouldBe("Recorded income #1: GBP 1500.00 from salary.");
    }

    [Fact]
    public async Task RecordIncome_Detects_Iso_Code()
    {
        var result = await Plugin(Store()).RecordIncomeAsync("USD 50", "refund");

        result.ShouldBe("Recorded income #1: USD 50.00 from refund.");
    }

    [Fact]
    public async Task RecordIncome_Empty_Amount_Returns_Corrective_Hint()
    {
        var result = await Plugin(Store()).RecordIncomeAsync("", "salary");

        result.ShouldContain("record_income");
    }

    [Fact]
    public async Task RecordIncome_Empty_Source_Returns_Corrective_Hint()
    {
        var result = await Plugin(Store()).RecordIncomeAsync("£1500", "  ");

        result.ShouldContain("source");
    }

    // show_month_summary --------------------------------------------------

    [Fact]
    public async Task ShowMonthSummary_Current_Month_Reports_Income_Expenses_Net()
    {
        var plugin = Plugin(Store());
        await plugin.RecordIncomeAsync("£1500", "salary");
        await plugin.RecordExpenseAsync("£320.50", "groceries");

        var result = await plugin.ShowMonthSummaryAsync();

        result.ShouldContain("July 2026: 2 entries.");
        result.ShouldContain("GBP: income 1500.00, expenses 320.50, net +1179.50.");
        result.ShouldContain("GBP top expenses: groceries 320.50.");
    }

    [Fact]
    public async Task ShowMonthSummary_Accepts_Explicit_Year_Month()
    {
        var plugin = Plugin(Store());
        await plugin.RecordExpenseAsync("£10", "coffee");

        var result = await plugin.ShowMonthSummaryAsync("2026-07");

        result.ShouldContain("July 2026: 1 entries.");
    }

    [Fact]
    public async Task ShowMonthSummary_Accepts_English_Month_Name()
    {
        var plugin = Plugin(Store());
        await plugin.RecordExpenseAsync("£10", "coffee");

        var result = await plugin.ShowMonthSummaryAsync("July");

        result.ShouldContain("July 2026");
    }

    [Fact]
    public async Task ShowMonthSummary_Accepts_Portuguese_Month_Name()
    {
        var plugin = Plugin(Store());
        await plugin.RecordExpenseAsync("£10", "coffee");

        var result = await plugin.ShowMonthSummaryAsync("julho");

        result.ShouldContain("July 2026");
    }

    [Fact]
    public async Task ShowMonthSummary_Empty_Month_Reports_No_Entries()
    {
        var result = await Plugin(Store()).ShowMonthSummaryAsync("2020-01");

        result.ShouldBe("No entries recorded for January 2020.");
    }

    [Fact]
    public async Task ShowMonthSummary_Negative_Net_Uses_Minus_Sign()
    {
        var plugin = Plugin(Store());
        await plugin.RecordExpenseAsync("£100", "rent");

        var result = await plugin.ShowMonthSummaryAsync();

        result.ShouldContain("net -100.00.");
    }

    [Fact]
    public async Task ShowMonthSummary_Separates_Currencies()
    {
        var plugin = Plugin(Store());
        await plugin.RecordExpenseAsync("£50", "groceries");
        await plugin.RecordExpenseAsync("R$ 120", "mercado");

        var result = await plugin.ShowMonthSummaryAsync();

        result.ShouldContain("BRL: income 0.00, expenses 120.00, net -120.00.");
        result.ShouldContain("GBP: income 0.00, expenses 50.00, net -50.00.");
    }

    [Fact]
    public async Task ShowMonthSummary_Ranks_Top_Expense_Categories()
    {
        var plugin = Plugin(Store());
        await plugin.RecordExpenseAsync("£10", "coffee");
        await plugin.RecordExpenseAsync("£200", "rent");
        await plugin.RecordExpenseAsync("£50", "groceries");

        var result = await plugin.ShowMonthSummaryAsync();

        result.ShouldContain("GBP top expenses: rent 200.00, groceries 50.00, coffee 10.00.");
    }

    [Fact]
    public async Task ShowMonthSummary_Unparseable_Month_Returns_Corrective_Hint()
    {
        var result = await Plugin(Store()).ShowMonthSummaryAsync("not-a-month");

        result.ShouldContain("Could not read the month");
    }

    [Fact]
    public async Task ShowMonthSummary_Out_Of_Range_Year_Returns_Corrective_Hint_Without_Throwing()
    {
        // "0000" is four digits and parses to 0, but is outside DateTime's 1..9999
        // range; it must not reach MonthLabel and throw into the kernel.
        var result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await Plugin(Store()).ShowMonthSummaryAsync("0000-07"));

        result.ShouldContain("Could not read the month");
    }

    // list_recent_entries -------------------------------------------------

    [Fact]
    public async Task ListRecent_Returns_Newest_First()
    {
        var plugin = Plugin(Store());
        await plugin.RecordExpenseAsync("£1", "first");
        await plugin.RecordIncomeAsync("£2", "second");
        await plugin.RecordExpenseAsync("£3", "third");

        var result = await plugin.ListRecentEntriesAsync("2");
        var lines = result.Split('\n');

        lines.Length.ShouldBe(2);
        lines[0].ShouldContain("third");
        lines[1].ShouldContain("second");
    }

    [Fact]
    public async Task ListRecent_Renders_Kind_Currency_And_Label()
    {
        var plugin = Plugin(Store());
        await plugin.RecordIncomeAsync("£1500", "salary");

        var result = await plugin.ListRecentEntriesAsync();

        result.ShouldBe("2026-07-04  income  GBP 1500.00  salary");
    }

    [Fact]
    public async Task ListRecent_Renders_Note_When_Present()
    {
        var plugin = Plugin(Store());
        await plugin.RecordExpenseAsync("£10", "coffee", "flat white");

        var result = await plugin.ListRecentEntriesAsync();

        result.ShouldBe("2026-07-04  expense  GBP 10.00  coffee  (flat white)");
    }

    [Fact]
    public async Task ListRecent_Empty_Returns_Message()
    {
        var result = await Plugin(Store()).ListRecentEntriesAsync();

        result.ShouldBe("No entries yet.");
    }

    [Fact]
    public async Task ListRecent_Invalid_Count_Returns_Corrective_Hint()
    {
        var result = await Plugin(Store()).ListRecentEntriesAsync("not-a-number");

        result.ShouldContain("list_recent_entries");
    }

    [Fact]
    public async Task ListRecent_Count_Above_Max_Is_Clamped()
    {
        var plugin = Plugin(Store());
        for (var i = 0; i < 3; i++)
        {
            await plugin.RecordExpenseAsync("£1", $"item{i}");
        }

        var result = await plugin.ListRecentEntriesAsync("999");

        result.Split('\n').Length.ShouldBe(3);
    }

    [Fact]
    public async Task ListRecent_Count_Below_One_Is_Clamped_To_One()
    {
        var plugin = Plugin(Store());
        await plugin.RecordExpenseAsync("£1", "a");
        await plugin.RecordExpenseAsync("£2", "b");

        var result = await plugin.ListRecentEntriesAsync("0");

        result.Split('\n').Length.ShouldBe(1);
        result.ShouldContain("b");
    }

    // Boundary failures ---------------------------------------------------

    [Fact]
    public async Task RecordExpense_Store_Write_Failure_Returns_Error_Line_Without_Throwing()
    {
        var store = Substitute.For<IScoutLocalStore>();
        store.MutateAsync(
                Arg.Any<string>(),
                Arg.Any<Action<LedgerDocument>>(),
                Arg.Any<CancellationToken>())
            .Returns<LedgerDocument>(_ => throw new IOException("disk full"));

        var plugin = new LedgerPlugin(store, Clock, Options.Create(new LedgerOptions()));

        var result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.RecordExpenseAsync("£23", "groceries"));

        result.ShouldBe("Could not access local ledger storage. Try again.");
    }

    [Fact]
    public async Task RecordIncome_Store_Unauthorized_Returns_Error_Line_Without_Throwing()
    {
        var store = Substitute.For<IScoutLocalStore>();
        store.MutateAsync(
                Arg.Any<string>(),
                Arg.Any<Action<LedgerDocument>>(),
                Arg.Any<CancellationToken>())
            .Returns<LedgerDocument>(_ => throw new UnauthorizedAccessException("denied"));

        var plugin = new LedgerPlugin(store, Clock, Options.Create(new LedgerOptions()));

        var result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.RecordIncomeAsync("£1500", "salary"));

        result.ShouldBe("Could not access local ledger storage. Try again.");
    }

    [Fact]
    public async Task ShowMonthSummary_Cancellation_Returns_Timeout_Line_Without_Throwing()
    {
        var store = Substitute.For<IScoutLocalStore>();
        store.ReadOrCreateAsync<LedgerDocument>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<LedgerDocument>(_ => throw new OperationCanceledException());

        var plugin = new LedgerPlugin(store, Clock, Options.Create(new LedgerOptions()));

        var result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.ShowMonthSummaryAsync());

        result.ShouldBe("The ledger operation timed out. Try again.");
    }

    [Fact]
    public async Task ShowMonthSummary_Corrupted_Json_File_Recovers_With_Error_Line()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "ledger-2026.json"), "{ this is not valid json ]");

        var result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await Plugin(Store()).ShowMonthSummaryAsync());

        result.ShouldBe("Your local ledger file could not be read; it may be corrupted. Fix or remove it and try again.");
    }

    [Fact]
    public async Task ListRecent_Corrupted_Json_File_Recovers_With_Error_Line()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "ledger-2026.json"), "not-json-at-all");

        var result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await Plugin(Store()).ListRecentEntriesAsync());

        result.ShouldBe("Your local ledger file could not be read; it may be corrupted. Fix or remove it and try again.");
    }

    // Persistence & construction -----------------------------------------

    [Fact]
    public async Task Entries_Persist_Across_Plugin_Instances()
    {
        var store = Store();
        await Plugin(store).RecordExpenseAsync("£5", "snack");

        var result = await Plugin(store).ListRecentEntriesAsync();

        result.ShouldContain("snack");
    }

    [Fact]
    public void Ctor_Throws_When_Store_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new LedgerPlugin(null!, Clock, Options.Create(new LedgerOptions())));
    }

    [Fact]
    public void Ctor_Throws_When_Clock_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new LedgerPlugin(Substitute.For<IScoutLocalStore>(), null!, Options.Create(new LedgerOptions())));
    }

    [Fact]
    public void Ctor_Throws_When_Options_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new LedgerPlugin(Substitute.For<IScoutLocalStore>(), Clock, null!));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
