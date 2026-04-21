using Scout.Core.Chat;

namespace Scout.Core.Tests.Chat;

public class ChatMessageTests
{
    [Fact]
    public void ChatMessage_Requires_NonEmpty_Text()
    {
        Action create = () => new ChatMessage(
            UserId: "u1",
            ChannelId: "c1",
            Text: string.Empty);

        create.Should().Throw<ArgumentException>()
            .WithParameterName(nameof(ChatMessage.Text));
    }

    [Fact]
    public void ChatMessage_Requires_NonEmpty_UserId()
    {
        Action create = () => new ChatMessage(UserId: "", ChannelId: "c1", Text: "hi");

        create.Should().Throw<ArgumentException>()
            .WithParameterName(nameof(ChatMessage.UserId));
    }

    [Fact]
    public void ChatMessage_Requires_NonEmpty_ChannelId()
    {
        Action create = () => new ChatMessage(UserId: "u1", ChannelId: "", Text: "hi");

        create.Should().Throw<ArgumentException>()
            .WithParameterName(nameof(ChatMessage.ChannelId));
    }

    [Fact]
    public void ChatMessage_Defaults_Timestamp_To_Utc_Now()
    {
        var before = DateTimeOffset.UtcNow;
        var msg = new ChatMessage("u1", "c1", "hello");
        var after = DateTimeOffset.UtcNow;

        msg.Timestamp.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void ChatMessage_Honors_Explicit_Timestamp()
    {
        var fixedAt = new DateTimeOffset(2026, 4, 21, 10, 0, 0, TimeSpan.Zero);

        var msg = new ChatMessage("u1", "c1", "hello", Timestamp: fixedAt);

        msg.Timestamp.Should().Be(fixedAt);
    }
}
