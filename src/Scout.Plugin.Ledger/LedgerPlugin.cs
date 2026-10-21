using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common;
using Scout.Plugin.Common.Abstractions;
using Scout.Plugin.Ledger.Abstractions;

namespace Scout.Plugin.Ledger;

/// <summary>
/// Personal money tracking exposed to the LLM as Semantic Kernel functions.
/// Descriptive-write only: every operation reads or appends to the Scout-local
/// per-year "ledger-{year}" collection and nothing external. Recording an expense
/// or income notes it locally; it never charges a card, moves money, or touches
/// any bank or account. Amounts are stored exactly as given and never converted
/// (currency conversion is the Currency plugin's job). Invalid model input returns
/// a short corrective sentence, and storage failures are caught and surfaced as a
/// clean error line, so a tool call never throws back into the kernel.
/// </summary>
public sealed class LedgerPlugin
{
    private const string ExpenseKind = "expense";
    private const string IncomeKind = "income";
    private const int MaxCount = 50;
    private const int DefaultCount = 10;
    private const int TopCategories = 3;

    private static readonly string[] KnownCurrencyCodes =
        ["GBP", "USD", "EUR", "BRL", "JPY", "CAD", "AUD", "CHF"];

    private static readonly IReadOnlyDictionary<string, int> MonthNames =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["january"] = 1, ["jan"] = 1, ["janeiro"] = 1,
            ["february"] = 2, ["feb"] = 2, ["fevereiro"] = 2,
            ["march"] = 3, ["mar"] = 3, ["marco"] = 3, ["março"] = 3,
            ["april"] = 4, ["apr"] = 4, ["abril"] = 4,
            ["may"] = 5, ["maio"] = 5,
            ["june"] = 6, ["jun"] = 6, ["junho"] = 6,
            ["july"] = 7, ["jul"] = 7, ["julho"] = 7,
            ["august"] = 8, ["aug"] = 8, ["agosto"] = 8,
            ["september"] = 9, ["sep"] = 9, ["sept"] = 9, ["setembro"] = 9,
            ["october"] = 10, ["oct"] = 10, ["outubro"] = 10,
            ["november"] = 11, ["nov"] = 11, ["novembro"] = 11,
            ["december"] = 12, ["dec"] = 12, ["dezembro"] = 12,
        };

    private readonly IScoutLocalStore _store;
    private readonly TimeProvider _clock;
    private readonly string _defaultCurrency;
    private readonly string _timeZone;

    /// <summary>Creates the plugin over the local store, an injected clock, and options.</summary>
    /// <param name="store">The Scout-local store. Must be non-null.</param>
    /// <param name="clock">Clock used to stamp entries and resolve the current month. Must be non-null.</param>
    /// <param name="options">Ledger options (default currency, time zone). Must be non-null.</param>
    public LedgerPlugin(IScoutLocalStore store, TimeProvider clock, IOptions<LedgerOptions> options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentNullException.ThrowIfNull(options);

        var value = options.Value;
        _defaultCurrency = string.IsNullOrWhiteSpace(value.DefaultCurrency)
            ? "GBP"
            : value.DefaultCurrency.Trim().ToUpperInvariant();
        _timeZone = string.IsNullOrWhiteSpace(value.TimeZone) ? "Europe/London" : value.TimeZone.Trim();
    }

    /// <summary>Record a personal expense in the local ledger.</summary>
    /// <param name="amount">The amount spent, parsed defensively.</param>
    /// <param name="category">What the expense was for.</param>
    /// <param name="note">Optional free-text note.</param>
    /// <param name="ct">Cancels the underlying store write.</param>
    /// <returns>A confirmation line, a corrective hint, or a clean error line.</returns>
    [KernelFunction("record_expense")]
    [Description("Record a personal expense in Scout's local ledger. This only notes it on this machine; nothing is charged to any card, bank or external account.")]
    public async Task<string> RecordExpenseAsync(
        [Description("The amount spent, e.g. \"£23\", \"23,50\" or \"R$ 120\".")] string amount,
        [Description("What the expense was for, e.g. \"groceries\".")] string category,
        [Description("Optional free-text note, e.g. \"weekly shop\". Leave empty for none.")] string note = "",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(amount))
        {
            return "Provide the amount spent, for example: record_expense amount=\"£23\" category=\"groceries\".";
        }

        if (string.IsNullOrWhiteSpace(category))
        {
            return "Provide a category for the expense, for example: record_expense amount=\"£23\" category=\"groceries\".";
        }

        if (!TryParseAmount(amount, _defaultCurrency, out var value, out var currency))
        {
            return "Could not read the amount. Use a number like \"£23\", \"23,50\" or \"R$ 120\".";
        }

        var label = category.Trim();
        var normalizedNote = string.IsNullOrWhiteSpace(note) ? string.Empty : note.Trim();

        try
        {
            var localNow = RelaxedDate.Now(_clock, _timeZone);
            var id = 0;
            await _store.MutateAsync<LedgerDocument>(CollectionFor(localNow.Year), doc =>
            {
                doc.Year = localNow.Year;
                id = doc.NextId++;
                doc.Entries.Add(new LedgerEntry
                {
                    Id = id,
                    Kind = ExpenseKind,
                    Amount = value,
                    Currency = currency,
                    Category = label,
                    Note = normalizedNote,
                    CreatedUtc = _clock.GetUtcNow(),
                });
            }, ct).ConfigureAwait(false);

            var noteSuffix = normalizedNote.Length == 0 ? string.Empty : $" Note: {normalizedNote}.";
            return $"Recorded expense #{id}: {currency} {Format(value)} ({label}).{noteSuffix}";
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Record incoming money in the local ledger.</summary>
    /// <param name="amount">The amount received, parsed defensively.</param>
    /// <param name="source">Where the money came from.</param>
    /// <param name="ct">Cancels the underlying store write.</param>
    /// <returns>A confirmation line, a corrective hint, or a clean error line.</returns>
    [KernelFunction("record_income")]
    [Description("Record incoming money in Scout's local ledger. This only notes it on this machine; nothing is transferred and no external account is changed.")]
    public async Task<string> RecordIncomeAsync(
        [Description("The amount received, e.g. \"£1500\", \"1.500,00\" or \"R$ 2000\".")] string amount,
        [Description("Where the money came from, e.g. \"salary\".")] string source,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(amount))
        {
            return "Provide the amount received, for example: record_income amount=\"£1500\" source=\"salary\".";
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            return "Provide the income source, for example: record_income amount=\"£1500\" source=\"salary\".";
        }

        if (!TryParseAmount(amount, _defaultCurrency, out var value, out var currency))
        {
            return "Could not read the amount. Use a number like \"£1500\", \"1.500,00\" or \"R$ 2000\".";
        }

        var label = source.Trim();

        try
        {
            var localNow = RelaxedDate.Now(_clock, _timeZone);
            var id = 0;
            await _store.MutateAsync<LedgerDocument>(CollectionFor(localNow.Year), doc =>
            {
                doc.Year = localNow.Year;
                id = doc.NextId++;
                doc.Entries.Add(new LedgerEntry
                {
                    Id = id,
                    Kind = IncomeKind,
                    Amount = value,
                    Currency = currency,
                    Source = label,
                    CreatedUtc = _clock.GetUtcNow(),
                });
            }, ct).ConfigureAwait(false);

            return $"Recorded income #{id}: {currency} {Format(value)} from {label}.";
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Summarise income and expenses for a single calendar month.</summary>
    /// <param name="month">Month to summarise; empty means the current month.</param>
    /// <param name="ct">Cancels the underlying store read.</param>
    /// <returns>A per-currency summary, an empty-state sentence, a corrective hint, or a clean error line.</returns>
    [KernelFunction("show_month_summary")]
    [Description("Summarise income, expenses and the net balance for a month from Scout's local ledger, with the top expense categories.")]
    public async Task<string> ShowMonthSummaryAsync(
        [Description("The month to summarise, e.g. \"2026-07\", \"July\" or \"julho\". Leave empty for the current month.")] string month = "",
        CancellationToken ct = default)
    {
        if (!TryResolveMonth(month, out var year, out var monthNumber))
        {
            return "Could not read the month. Use a format like \"2026-07\", a month name (July / julho), or leave it empty for this month.";
        }

        try
        {
            var document = await _store.ReadOrCreateAsync<LedgerDocument>(CollectionFor(year), ct).ConfigureAwait(false);
            var inMonth = document.Entries.Where(e => InMonth(e, year, monthNumber)).ToList();
            var monthLabel = MonthLabel(year, monthNumber);
            if (inMonth.Count == 0)
            {
                return $"No entries recorded for {monthLabel}.";
            }

            var lines = new List<string> { $"{monthLabel}: {inMonth.Count} entries." };
            var currencies = inMonth
                .Select(e => e.Currency)
                .Distinct()
                .OrderBy(c => c, StringComparer.Ordinal);

            foreach (var currency in currencies)
            {
                var income = inMonth.Where(e => e.Currency == currency && e.Kind == IncomeKind).Sum(e => e.Amount);
                var expenses = inMonth.Where(e => e.Currency == currency && e.Kind == ExpenseKind).Sum(e => e.Amount);
                var net = income - expenses;
                lines.Add($"{currency}: income {Format(income)}, expenses {Format(expenses)}, net {FormatSigned(net)}.");

                var categories = inMonth
                    .Where(e => e.Currency == currency && e.Kind == ExpenseKind)
                    .GroupBy(e => e.Category.Length == 0 ? "uncategorised" : e.Category)
                    .Select(g => (Category: g.Key, Total: g.Sum(x => x.Amount)))
                    .OrderByDescending(x => x.Total)
                    .ThenBy(x => x.Category, StringComparer.Ordinal)
                    .Take(TopCategories)
                    .ToList();

                if (categories.Count > 0)
                {
                    var rendered = string.Join(", ", categories.Select(c => $"{c.Category} {Format(c.Total)}"));
                    lines.Add($"{currency} top expenses: {rendered}.");
                }
            }

            return string.Join("\n", lines);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>List the most recent ledger entries, newest first.</summary>
    /// <param name="count">How many to show; parsed defensively and clamped to 1..50.</param>
    /// <param name="ct">Cancels the underlying store read.</param>
    /// <returns>One entry per line, an empty-state sentence, a corrective hint, or a clean error line.</returns>
    [KernelFunction("list_recent_entries")]
    [Description("List the most recent ledger entries, newest first, showing date, kind, amount and label.")]
    public async Task<string> ListRecentEntriesAsync(
        [Description("How many entries to show, from 1 to 50. Defaults to 10, e.g. \"5\".")] string count = "10",
        CancellationToken ct = default)
    {
        if (!TryParseCount(count, out var take))
        {
            return "Provide how many entries to show as a number, for example: list_recent_entries count=\"10\".";
        }

        try
        {
            var localNow = RelaxedDate.Now(_clock, _timeZone);
            var current = await _store.ReadOrCreateAsync<LedgerDocument>(CollectionFor(localNow.Year), ct).ConfigureAwait(false);

            var entries = new List<LedgerEntry>(current.Entries);
            if (entries.Count < take)
            {
                var previous = await _store
                    .ReadOrCreateAsync<LedgerDocument>(CollectionFor(localNow.Year - 1), ct)
                    .ConfigureAwait(false);
                entries.AddRange(previous.Entries);
            }

            var recent = entries
                .OrderByDescending(e => e.CreatedUtc)
                .ThenByDescending(e => e.Id)
                .Take(take)
                .ToList();
            return recent.Count == 0
                ? "No entries yet."
                : string.Join("\n", recent.Select(FormatEntryLine));
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    private string FormatEntryLine(LedgerEntry entry)
    {
        var date = ToLocal(entry.CreatedUtc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var label = entry.Kind == IncomeKind ? entry.Source : entry.Category;
        if (string.IsNullOrEmpty(label))
        {
            label = entry.Kind == IncomeKind ? "income" : "uncategorised";
        }

        var line = $"{date}  {entry.Kind}  {entry.Currency} {Format(entry.Amount)}  {label}";
        return entry.Note.Length == 0 ? line : $"{line}  ({entry.Note})";
    }

    private bool InMonth(LedgerEntry entry, int year, int month)
    {
        var local = ToLocal(entry.CreatedUtc);
        return local.Year == year && local.Month == month;
    }

    private DateTimeOffset ToLocal(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, ResolveZone(_timeZone));

    private bool TryResolveMonth(string input, out int year, out int month)
    {
        var today = RelaxedDate.Today(_clock, _timeZone);
        year = today.Year;
        month = today.Month;

        if (string.IsNullOrWhiteSpace(input))
        {
            return true;
        }

        var text = input.Trim();

        // Explicit "yyyy-MM" / "yyyy-M".
        var parts = text.Split('-');
        if (parts.Length == 2 &&
            parts[0].Length == 4 &&
            int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var explicitYear) &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var explicitMonth) &&
            explicitYear is >= 1 and <= 9999 &&
            explicitMonth is >= 1 and <= 12)
        {
            year = explicitYear;
            month = explicitMonth;
            return true;
        }

        // Bare month number for the current year.
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric))
        {
            if (numeric is >= 1 and <= 12)
            {
                month = numeric;
                return true;
            }

            return false;
        }

        // Month name (en/pt) for the current year.
        if (MonthNames.TryGetValue(text, out var named))
        {
            month = named;
            return true;
        }

        // Fall back to the shared relaxed date parser (today/tomorrow/friday/25-12-2026, ...).
        if (RelaxedDate.TryParse(text, _clock, _timeZone, out var date))
        {
            year = date.Year;
            month = date.Month;
            return true;
        }

        return false;
    }

    // Extract the amount and its currency from free text. Detects a leading symbol
    // or an ISO code, then normalises "." and "," so both "23,50" and "1.500,00"
    // read correctly. Never converts; the currency is stored as stated.
    private static bool TryParseAmount(string raw, string defaultCurrency, out decimal amount, out string currency)
    {
        amount = 0m;
        var detected = DetectCurrency(raw);
        currency = detected.Length == 0 ? defaultCurrency : detected;

        var cleaned = new string(raw.Where(c => char.IsAsciiDigit(c) || c is '.' or ',').ToArray());
        if (cleaned.Length == 0)
        {
            return false;
        }

        cleaned = NormalizeNumber(cleaned);
        if (!decimal.TryParse(cleaned, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        if (value <= 0m)
        {
            return false;
        }

        amount = decimal.Round(value, 2, MidpointRounding.AwayFromZero);
        return true;
    }

    private static string DetectCurrency(string raw)
    {
        if (raw.Contains("R$", StringComparison.OrdinalIgnoreCase))
        {
            return "BRL";
        }

        if (raw.Contains('£'))
        {
            return "GBP";
        }

        if (raw.Contains('€'))
        {
            return "EUR";
        }

        if (raw.Contains('$'))
        {
            return "USD";
        }

        var upper = raw.ToUpperInvariant();
        foreach (var code in KnownCurrencyCodes)
        {
            if (upper.Contains(code, StringComparison.Ordinal))
            {
                return code;
            }
        }

        return string.Empty;
    }

    // Reduce a digit-and-separator string to an invariant decimal string. When
    // both separators appear, the last one is the decimal point; a single
    // separator is treated as thousands when it groups exactly three digits.
    private static string NormalizeNumber(string s)
    {
        var hasComma = s.Contains(',');
        var hasDot = s.Contains('.');

        if (hasComma && hasDot)
        {
            return s.LastIndexOf(',') > s.LastIndexOf('.')
                ? s.Replace(".", string.Empty).Replace(',', '.')
                : s.Replace(",", string.Empty);
        }

        if (hasComma)
        {
            return NormalizeSingleSeparator(s, ',');
        }

        if (hasDot)
        {
            return NormalizeSingleSeparator(s, '.');
        }

        return s;
    }

    private static string NormalizeSingleSeparator(string s, char separator)
    {
        if (s.IndexOf(separator) != s.LastIndexOf(separator))
        {
            // More than one occurrence: a thousands separator (e.g. "1.000.000").
            return s.Replace(separator.ToString(), string.Empty);
        }

        var index = s.IndexOf(separator);
        var trailing = s.Length - index - 1;
        if (trailing == 3)
        {
            // A single separator grouping three digits: thousands (e.g. "1,000").
            return s.Remove(index, 1);
        }

        return string.Concat(s.AsSpan(0, index), ".", s.AsSpan(index + 1));
    }

    private static bool TryParseCount(string count, out int take)
    {
        take = DefaultCount;
        if (string.IsNullOrWhiteSpace(count))
        {
            return true;
        }

        if (!int.TryParse(count.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        take = Math.Clamp(value, 1, MaxCount);
        return true;
    }

    private static string CollectionFor(int year) =>
        "ledger-" + year.ToString(CultureInfo.InvariantCulture);

    private static string Format(decimal value) =>
        value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatSigned(decimal value) =>
        (value >= 0m ? "+" : "-") + Math.Abs(value).ToString("0.00", CultureInfo.InvariantCulture);

    private static string MonthLabel(int year, int month) =>
        new DateTime(year, month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    private static TimeZoneInfo ResolveZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static bool IsHandledFailure(Exception ex)
        => ex is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException;

    private static string ErrorLine(Exception ex) => ex switch
    {
        OperationCanceledException => "The ledger operation timed out. Try again.",
        JsonException => "Your local ledger file could not be read; it may be corrupted. Fix or remove it and try again.",
        _ => "Could not access local ledger storage. Try again.",
    };
}
