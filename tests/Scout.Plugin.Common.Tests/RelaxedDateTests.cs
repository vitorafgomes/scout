using Scout.Plugin.Common;
using Shouldly;
using Xunit;

namespace Scout.Plugin.Common.Tests;

public sealed class RelaxedDateTests
{
    // Friday 2026-07-03, 12:00 UTC.
    private static readonly TimeProvider Clock =
        new FixedClock(new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero));

    private const string Tz = "Europe/London";

    [Theory]
    [InlineData("2026-12-25", 2026, 12, 25)]
    [InlineData("25/12/2026", 2026, 12, 25)]
    [InlineData("25-12-2026", 2026, 12, 25)]
    [InlineData("9/3/2026", 2026, 3, 9)]
    public void TryParse_AcceptsExplicitFormats(string input, int y, int m, int d)
    {
        RelaxedDate.TryParse(input, Clock, Tz, out var date).ShouldBeTrue();
        date.ShouldBe(new DateOnly(y, m, d));
    }

    [Theory]
    [InlineData("today", 2026, 7, 3)]
    [InlineData("hoje", 2026, 7, 3)]
    [InlineData("tomorrow", 2026, 7, 4)]
    [InlineData("amanhã", 2026, 7, 4)]
    [InlineData("yesterday", 2026, 7, 2)]
    [InlineData("ontem", 2026, 7, 2)]
    public void TryParse_AcceptsRelativeWords(string input, int y, int m, int d)
    {
        RelaxedDate.TryParse(input, Clock, Tz, out var date).ShouldBeTrue();
        date.ShouldBe(new DateOnly(y, m, d));
    }

    [Theory]
    // From Friday 2026-07-03, the next occurrence (today excluded).
    [InlineData("friday", 2026, 7, 10)]
    [InlineData("sexta", 2026, 7, 10)]
    [InlineData("monday", 2026, 7, 6)]
    [InlineData("segunda", 2026, 7, 6)]
    [InlineData("domingo", 2026, 7, 5)]
    public void TryParse_AcceptsWeekdayNames(string input, int y, int m, int d)
    {
        RelaxedDate.TryParse(input, Clock, Tz, out var date).ShouldBeTrue();
        date.ShouldBe(new DateOnly(y, m, d));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date")]
    [InlineData("32/13/2026")]
    public void TryParse_RejectsGarbage(string? input)
    {
        RelaxedDate.TryParse(input, Clock, Tz, out _).ShouldBeFalse();
    }

    [Fact]
    public void Today_UsesTheClockAndZone()
    {
        RelaxedDate.Today(Clock, Tz).ShouldBe(new DateOnly(2026, 7, 3));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
