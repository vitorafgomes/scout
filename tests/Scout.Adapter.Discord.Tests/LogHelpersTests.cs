namespace Scout.Adapter.Discord.Tests;

public class LogHelpersTests
{
    [Fact]
    public void Null_Input_Returns_Empty_And_Not_Truncated()
    {
        var result = LogHelpers.Truncate(null, out var truncated);

        result.ShouldBe(string.Empty);
        truncated.ShouldBeFalse();
    }

    [Fact]
    public void Ascii_Shorter_Than_Max_Returns_Unchanged()
    {
        var input = new string('a', LogHelpers.MaxLoggedContentLength - 1);

        var result = LogHelpers.Truncate(input, out var truncated);

        result.ShouldBe(input);
        truncated.ShouldBeFalse();
    }

    [Fact]
    public void Ascii_Longer_Than_Max_Is_Truncated_With_Ellipsis()
    {
        var input = new string('a', LogHelpers.MaxLoggedContentLength + 50);

        var result = LogHelpers.Truncate(input, out var truncated);

        truncated.ShouldBeTrue();
        result.Length.ShouldBe(LogHelpers.MaxLoggedContentLength);
        result.EndsWith("...", StringComparison.Ordinal).ShouldBeTrue();
    }

    [Fact]
    public void Surrogate_Pair_Never_Splits_Across_The_Cut()
    {
        // Build a string that ends in an emoji exactly around the truncation
        // boundary. U+1F600 is a single rune encoded as a 2-char surrogate
        // pair in UTF-16.
        const string emoji = "\U0001F600";
        var filler = new string('a', LogHelpers.MaxLoggedContentLength - 3);
        // Ensure overall length forces truncation.
        var input = filler + emoji + emoji + emoji + emoji;

        var result = LogHelpers.Truncate(input, out var truncated);

        truncated.ShouldBeTrue();
        // The char immediately preceding the "..." must NOT be a lone high
        // surrogate. Either it is ASCII filler or it is the low surrogate
        // that closes a valid pair.
        var lastBeforeEllipsis = result[^4];
        char.IsHighSurrogate(lastBeforeEllipsis).ShouldBeFalse();
        result.EndsWith("...", StringComparison.Ordinal).ShouldBeTrue();
    }

    [Fact]
    public void Exact_Length_At_Max_Returns_Unchanged()
    {
        var input = new string('a', LogHelpers.MaxLoggedContentLength);

        var result = LogHelpers.Truncate(input, out var truncated);

        truncated.ShouldBeFalse();
        result.ShouldBe(input);
    }
}
