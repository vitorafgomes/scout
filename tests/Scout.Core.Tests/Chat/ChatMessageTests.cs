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
            .WithMessage("*Text*");
    }

    [Fact]
    public void ChatMessage_Requires_NonEmpty_UserId()
    {
        Action create = () => new ChatMessage(UserId: "", ChannelId: "c1", Text: "hi");

        create.Should().Throw<ArgumentException>()
            .WithMessage("*UserId*");
    }

    [Fact]
    public void ChatMessage_Requires_NonEmpty_ChannelId()
    {
        Action create = () => new ChatMessage(UserId: "u1", ChannelId: "", Text: "hi");

        create.Should().Throw<ArgumentException>()
            .WithMessage("*ChannelId*");
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

    [Fact]
    public void ChatResponse_Plain_Produces_Text_Only_Response()
    {
        var response = ChatResponse.Plain("done");

        response.Text.Should().Be("done");
        response.Citations.Should().BeNull();
    }
}
