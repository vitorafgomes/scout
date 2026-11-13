using Microsoft.Extensions.Time.Testing;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Memory;

namespace Scout.Conversation.Tests.Abstractions;

[Collection(ChatOriginKeptMarksCollection.Name)]
public sealed class ChatOriginMarksTests
{
    private static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero));

    private static string NewKey()
        => new ConversationKeyProvider().DeriveKey(new ChatMessage("U1", $"C{Guid.NewGuid():N}", "x"));

    private static string NewDiscordOrigin() => $"discord:3333:{Guid.NewGuid():N}";

    private bool InTurn(string? conversationKey, Func<bool> during, string? origin = null)
    {
        using var turn = ChatOrigin.Enter(origin ?? NewDiscordOrigin(), isOwner: true, _clock, conversationKey);
        return during();
    }

    private void RememberInTurn(string? conversationKey, string mark, string? origin = null)
        => InTurn(conversationKey, () =>
        {
            ChatOrigin.Remember(mark, TenMinutes);
            return true;
        }, origin);

    [Fact]
    public void A_Mark_Is_Seen_In_A_Later_Turn_With_The_Same_Conversation_Key_And_A_Different_Origin_Within_Ten_Minutes()
    {
        var key = NewKey();
        RememberInTurn(key, "state:p1", "discord:3333:1001");

        _clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));

        InTurn(key, () => ChatOrigin.Remembers("state:p1"), "discord:3333:1002").ShouldBeTrue();
        InTurn(key, () => ChatOrigin.Remembers("state:p2"), "discord:3333:1002").ShouldBeFalse();
    }

    [Fact]
    public void A_Mark_Is_Not_Seen_In_A_Turn_With_Another_Conversation_Key_Even_From_The_Same_Origin()
    {
        RememberInTurn(NewKey(), "state:p1", "discord:3333:1001");

        InTurn(NewKey(), () => ChatOrigin.Remembers("state:p1"), "discord:3333:1001").ShouldBeFalse();
    }

    [Fact]
    public void A_Mark_Is_Kept_For_A_Conversation_Whose_Turns_Carry_No_Origin()
    {
        var key = NewKey();
        using (ChatOrigin.Enter(null, isOwner: true, _clock, key))
        {
            ChatOrigin.Remember("state:p1", TenMinutes);
        }

        using var later = ChatOrigin.Enter(null, isOwner: true, _clock, key);
        ChatOrigin.Remembers("state:p1").ShouldBeTrue();
    }

    [Fact]
    public void A_Mark_Is_Not_Seen_After_Ten_Minutes()
    {
        var key = NewKey();
        RememberInTurn(key, "state:p1");

        _clock.Advance(TenMinutes + TimeSpan.FromSeconds(1));

        InTurn(key, () => ChatOrigin.Remembers("state:p1")).ShouldBeFalse();
    }

    [Fact]
    public void Without_A_Conversation_Key_A_Mark_Lasts_Only_For_Its_Turn()
    {
        var sameTurn = InTurn(null, () =>
        {
            ChatOrigin.Remember("state:p1", TenMinutes);
            return ChatOrigin.Remembers("state:p1");
        });

        sameTurn.ShouldBeTrue();
        InTurn(null, () => ChatOrigin.Remembers("state:p1")).ShouldBeFalse();
        ChatOrigin.Remembers("state:p1").ShouldBeFalse();
    }

    [Fact]
    public void The_Kept_Marks_Never_Exceed_256_And_The_One_Closest_To_Expiring_Gives_Way()
    {
        var key = NewKey();
        foreach (var i in Enumerable.Range(0, ChatOrigin.KeptCap + 44))
        {
            RememberInTurn(key, $"state:p{i}");
            ChatOrigin.KeptCount.ShouldBeLessThanOrEqualTo(ChatOrigin.KeptCap);
            _clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        ChatOrigin.KeptCount.ShouldBe(ChatOrigin.KeptCap);
        InTurn(key, () => ChatOrigin.Remembers("state:p0")).ShouldBeFalse();
        InTurn(key, () => ChatOrigin.Remembers($"state:p{ChatOrigin.KeptCap + 43}")).ShouldBeTrue();
    }
}
