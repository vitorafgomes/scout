using Scout.Adapter.Discord.Streaming;

namespace Scout.Adapter.Discord.Tests.Streaming;

public sealed class DiscordReplySplitterTests
{
    [Fact]
    public void A_Long_Reply_Splits_At_Paragraphs()
    {
        var parts = DiscordReplySplitter.Split(
            "alpha beta gamma\n\ndelta epsilon zeta\n\neta theta", partLength: 24, maxParts: 5);

        parts.ShouldBe(["alpha beta gamma", "delta epsilon zeta", "eta theta"]);
    }

    [Fact]
    public void A_Code_Fence_That_Fits_In_One_Part_Moves_Whole_To_The_Next_Part()
    {
        var parts = DiscordReplySplitter.Split(
            "Intro line here\n```cs\nvar a = 1;\n```\nafter", partLength: 30, maxParts: 5);

        parts.ShouldBe(["Intro line here", "```cs\nvar a = 1;\n```\nafter"]);
    }

    [Fact]
    public void A_Code_Fence_Longer_Than_A_Part_Is_Closed_And_Reopened_With_Its_Language()
    {
        var parts = DiscordReplySplitter.Split(
            "```py\nprint(1)\nprint(2)\nprint(3)\n```", partLength: 24, maxParts: 5);

        parts.ShouldBe(["```py\nprint(1)\nprint(2)\n```", "```py\nprint(3)\n```"]);
    }

    [Fact]
    public void A_Word_Is_Never_Cut_And_The_Last_Allowed_Part_Says_The_Reply_Was_Truncated()
    {
        var parts = DiscordReplySplitter.Split(
            "one two three four five six seven", partLength: 10, maxParts: 2);

        parts.ShouldBe(["one two", "three\n\n(reply truncated)"]);
    }

    [Fact]
    public void A_Reply_That_Fits_Is_One_Part_Unchanged()
    {
        DiscordReplySplitter.Split("short answer", partLength: 100, maxParts: 5).ShouldBe(["short answer"]);
    }
}
