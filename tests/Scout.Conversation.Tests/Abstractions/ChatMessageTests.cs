using Scout.Conversation.Abstractions;

namespace Scout.Conversation.Tests.Abstractions;

public sealed class ChatMessageTests
{
    [Theory]
    [InlineData("", "c", "t", "UserId")]
    [InlineData("u", "", "t", "ChannelId")]
    [InlineData("u", "c", "", "Text")]
    public void Ctor_Throws_When_Required_Field_Empty(
        string userId, string channelId, string text, string field)
    {
        Should.Throw<ArgumentException>(() => new ChatMessage(userId, channelId, text))
            .Message.ShouldContain(field);
    }

    [Fact]
    public void Ctor_Defaults_Timestamp_To_UtcNow_When_Null()
    {
        var before = DateTimeOffset.UtcNow;
        var message = new ChatMessage("u", "c", "t");

        message.Timestamp.ShouldBeGreaterThanOrEqualTo(before);
    }

    [Fact]
    public void Ctor_Preserves_Explicit_Timestamp()
    {
        var ts = new DateTimeOffset(2026, 5, 28, 10, 0, 0, TimeSpan.Zero);
        var message = new ChatMessage("u", "c", "t", ts);

        message.Timestamp.ShouldBe(ts);
    }
}
