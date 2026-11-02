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

    [Fact]
    public void A_Voice_Message_May_Have_Empty_Text()
    {
        var audio = new AudioAttachment("audio/ogg", 10, TimeSpan.FromSeconds(1), _ => Task.FromResult<Stream>(Stream.Null));

        var message = new ChatMessage("u", "c", string.Empty, null, audio);

        message.Text.ShouldBe(string.Empty);
        message.Audio.ShouldBeSameAs(audio);
    }
}
