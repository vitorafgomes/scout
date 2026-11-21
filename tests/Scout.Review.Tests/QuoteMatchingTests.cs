namespace Scout.Review.Tests;

/// <summary>The match forgives what does not change which code is meant, and stays strict about uniqueness.</summary>
public sealed class QuoteMatchingTests
{
    private const string Patch =
        "@@ -1,5 +1,7 @@ class Sender\n"
        + " public void Send(Message message)\n"
        + " {\n"
        + "-    if (message is null) return;\n"
        + "+    ArgumentNullException.ThrowIfNull(message);\n"
        + "+    _channel.BasicPublish(exchange: \"\", routingKey: _queue, body: message.Body, mandatory: true);\n"
        + "     Log(\"sent\");\n"
        + "+    Log(\"sent\");\n"
        + " }";

    private const string Removals =
        "@@ -10,7 +10,2 @@ class SenderTests\n"
        + "-[Fact]\n"
        + "-public void Send_fails_when_the_channel_is_closed()\n"
        + " {\n"
        + "-    Should.Throw<ChannelClosedException>(() => Send());\n"
        + " }\n"
        + "-[Fact]\n"
        + "-public void Other() { }";

    [Theory]
    [InlineData("ArgumentNullException.ThrowIfNull(message);", 3, "a line quoted without its indentation")]
    [InlineData("\t_channel.BasicPublish(exchange: \"\",   routingKey: _queue", 4, "a part of one line, its inner spacing wider")]
    [InlineData("ArgumentNullException.ThrowIfNull(message);\n\n_channel.BasicPublish(exchange: \"\", routingKey: _queue, body: message.Body, mandatory: true);", 3, "adjacent lines with a blank line the patch does not have")]
    [InlineData("ArgumentNullException.ThrowIfNull(message);\nnot_in_the_patch();", 3, "a quote whose first line of code alone is found once")]
    public void Q1_a_quote_without_its_indentation_or_a_part_of_one_line_is_placed_on_that_line_when_found_once(string quote, int line, string why) =>
        DiffQuote.Locate(Patch, quote).ShouldBe(new QuoteMatch(line, null), why);

    [Theory]
    [InlineData("Log(\"sent\");", 2, "a whole line found on two lines")]
    [InlineData("Log(", 2, "a part found in two lines")]
    public void Q1_a_quote_found_more_than_once_is_refused_with_the_count(string quote, int count, string why) =>
        DiffQuote.Locate(Patch, quote).ShouldBe(new QuoteMatch(null, DiffQuote.FoundTimes(count)), why);

    [Fact]
    public void Q1_a_quote_longer_than_its_line_by_a_trailing_comma_is_still_not_found() =>
        DiffQuote.Locate(Patch, "ArgumentNullException.ThrowIfNull(message);,")
            .ShouldBe(new QuoteMatch(null, DiffQuote.NotFound), "only a quote shorter than its line is forgiven, as a part of it");

    [Fact]
    public void Q2_a_quote_of_removed_lines_the_patch_separates_is_placed_where_its_first_line_was_removed() =>
        DiffQuote.Locate(Removals, "[Fact]\npublic void Send_fails_when_the_channel_is_closed()\nShould.Throw<ChannelClosedException>(() => Send());")
            .ShouldBe(new QuoteMatch(10, null), "removed lines in order inside one hunk, context between them, sit where the first was removed");
}
