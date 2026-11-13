using Microsoft.Extensions.Time.Testing;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Memory;

namespace Scout.Conversation.Tests.Abstractions;

[Collection(ChatOriginKeptMarksCollection.Name)]
public sealed class ChatOriginTurnEndTests
{
    private static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(5);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero));

    private static string NewKey()
        => new ConversationKeyProvider().DeriveKey(new ChatMessage("U1", $"C{Guid.NewGuid():N}", "x"));

    private static (string? Origin, bool IsOwner, bool Remembers) Seen(string mark)
        => (ChatOrigin.Current, ChatOrigin.IsOwner, ChatOrigin.Remembers(mark));

    [Fact]
    public async Task A_Task_Started_Inside_An_Owner_Turn_Sees_No_Owner_No_Origin_And_No_Marks_Once_The_Turn_Has_Ended()
    {
        var turnEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<(string? Origin, bool IsOwner, bool Remembers)> captured;
        (string? Origin, bool IsOwner, bool Remembers) duringTurn;
        using (ChatOrigin.Enter("local:quick-ask", isOwner: true, _clock, NewKey()))
        {
            ChatOrigin.Remember("state:p1", TenMinutes);
            captured = Task.Run(async () =>
            {
                await turnEnded.Task;
                return Seen("state:p1");
            });
            duringTurn = Seen("state:p1");
        }

        turnEnded.SetResult();

        duringTurn.ShouldBe(("local:quick-ask", true, true));
        (await captured).ShouldBe(((string?)null, false, false), "a task the turn started keeps no owner authority, origin or mark after the turn");
    }

    [Fact]
    public void A_Nested_Turn_Ending_Gives_The_Outer_Turn_Back_To_The_Outer_Flow()
    {
        using var outer = ChatOrigin.Enter("discord:3333:1", isOwner: false, _clock, null);
        ChatOrigin.Remember("state:outer", TenMinutes);
        (string? Origin, bool IsOwner, bool Remembers) nested;
        using (ChatOrigin.Enter("local:quick-ask", isOwner: true, _clock, null))
        {
            nested = Seen("state:outer");
        }

        nested.ShouldBe(("local:quick-ask", true, false));
        Seen("state:outer").ShouldBe(("discord:3333:1", false, true), "the outer turn is the current one again after the nested turn ends");
    }

    [Fact]
    public async Task Two_Concurrent_Turns_Each_See_Only_Their_Own_Origin_Owner_And_Marks()
    {
        using var bothInTurn = new Barrier(2);

        (string? Origin, bool IsOwner, bool Remembers) Turn(string origin, bool isOwner, string mark)
        {
            using var turn = ChatOrigin.Enter(origin, isOwner, _clock, null);
            ChatOrigin.Remember(mark, TenMinutes);
            bothInTurn.SignalAndWait(BarrierTimeout).ShouldBeTrue();
            var seen = Seen("state:owner");
            bothInTurn.SignalAndWait(BarrierTimeout).ShouldBeTrue();
            return seen;
        }

        var seen = await Task.WhenAll(
            Task.Run(() => Turn("local:quick-ask", isOwner: true, "state:owner")),
            Task.Run(() => Turn("discord:3333:2", isOwner: false, "state:sender")));

        seen.ShouldBe([("local:quick-ask", true, true), ("discord:3333:2", false, false)]);
    }

    [Fact]
    public async Task A_Task_Captured_In_An_Ended_Turn_Does_Not_See_A_Later_Owner_Turn_Running_On_Another_Flow()
    {
        var laterTurnRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capturedRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<(string? Origin, bool IsOwner, bool Remembers)> captured;
        using (ChatOrigin.Enter("discord:3333:3", isOwner: false, _clock, null))
        {
            captured = Task.Run(async () =>
            {
                await laterTurnRunning.Task;
                var seen = Seen("state:later");
                capturedRead.SetResult();
                return seen;
            });
        }

        var later = Task.Run(async () =>
        {
            using var turn = ChatOrigin.Enter("local:quick-ask", isOwner: true, _clock, null);
            ChatOrigin.Remember("state:later", TenMinutes);
            laterTurnRunning.SetResult();
            await capturedRead.Task;
            return Seen("state:later");
        });

        (await captured).ShouldBe(((string?)null, false, false), "the captured task belongs to no turn, neither its ended one nor the later owner turn");
        (await later).ShouldBe(("local:quick-ask", true, true));
    }
}
