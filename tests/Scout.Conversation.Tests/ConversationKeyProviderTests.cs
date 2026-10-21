using Scout.Conversation.Abstractions;
using Scout.Conversation.Memory;

namespace Scout.Conversation.Tests;

public sealed class ConversationKeyProviderTests
{
    private static readonly ConversationKeyProvider Provider = new();

    [Fact]
    public void DeriveKey_Is_Stable_For_Same_Channel_And_User()
    {
        var first = Provider.DeriveKey(new ChatMessage("u1", "c1", "hello"));
        var second = Provider.DeriveKey(new ChatMessage("u1", "c1", "different text"));

        first.ShouldBe(second);
    }

    [Fact]
    public void DeriveKey_Distinguishes_Users_In_Same_Channel()
    {
        var a = Provider.DeriveKey(new ChatMessage("u1", "c1", "hi"));
        var b = Provider.DeriveKey(new ChatMessage("u2", "c1", "hi"));

        a.ShouldNotBe(b);
    }

    [Fact]
    public void DeriveKey_Distinguishes_Channels_For_Same_User()
    {
        var a = Provider.DeriveKey(new ChatMessage("u1", "c1", "hi"));
        var b = Provider.DeriveKey(new ChatMessage("u1", "c2", "hi"));

        a.ShouldNotBe(b);
    }

    [Fact]
    public void DeriveKey_Cannot_Collide_Across_Id_Boundary()
    {
        // Without a separator "c1" + "u12" would equal "c1u" + "12". The unit
        // separator (U+001F) between ids makes these distinct.
        var a = Provider.DeriveKey(new ChatMessage("u12", "c1", "hi"));
        var b = Provider.DeriveKey(new ChatMessage("12", "c1u", "hi"));

        a.ShouldNotBe(b);
    }

    [Fact]
    public void DeriveKey_Throws_When_Message_Null()
    {
        Should.Throw<ArgumentNullException>(() => Provider.DeriveKey(null!));
    }
}
