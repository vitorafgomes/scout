using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Access;
using Scout.Conversation.Streaming;
using Scout.Conversation.Tests.Fakes;

namespace Scout.Conversation.Tests;

public sealed class ConversationConductorPairingTests
{
    private const string PairedReply = "Paired. Scout will answer you from now on.";
    private const string RefusedReply =
        "That pairing code is not valid. Check the code in Scout's Settings and send it again.";
    private const string NotAllowedReply = "Scout only answers its configured users.";
    private const string NoAllowedUsersReply =
        "Scout is not set up for anyone yet. If you are the owner, open the Scout desktop app, go to Settings, "
        + "choose Pair from chat and send pair followed by the 6-digit code here in a direct message.";
    private const string Code = "482913";

    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly RenderCapabilities FineGrained = new() { FineGrainedEdit = true, MaxBodyLength = 4096 };

    private sealed class RecordingPairingSignal : IPairingSignal
    {
        public List<string> UserIds { get; } = [];
        public int BurnedCount { get; private set; }

        public void Paired(string userId) => UserIds.Add(userId);

        public void Burned() => BurnedCount++;
    }

    private sealed class FailingPairingSignal : IPairingSignal
    {
        public void Paired(string userId) => throw new IOException("Broken pipe");

        public void Burned() => throw new IOException("Broken pipe");
    }

    private sealed class FailingOncePairingSignal : IPairingSignal
    {
        public List<string> Reports { get; } = [];

        public Action? DuringFirstReport { get; set; }

        public void Paired(string userId)
        {
            Reports.Add(userId);
            if (Reports.Count == 1)
            {
                DuringFirstReport?.Invoke();
                throw new IOException("Broken pipe");
            }
        }

        public void Burned()
        {
        }
    }

    private static AccessOptions Pending(DateTimeOffset? expiresAt = null) => new()
    {
        RequireAllowList = true,
        PairingCode = Code,
        PairingExpiresAt = expiresAt ?? Now.AddMinutes(10),
    };

    private static (ConversationConductor Conductor, IConversationRouter Router) Conductor(
        AccessOptions access,
        IPairingSignal? signal,
        FakeTimeProvider? time = null,
        ILogger<ConversationConductor>? logger = null)
    {
        var router = Substitute.For<IConversationRouter>();
        var clock = time ?? new FakeTimeProvider(Now);
        var pump = new StreamPump(new StreamPumpOptions { TimeProvider = clock });
        return (new ConversationConductor(router, pump, logger, Options.Create(access), signal, clock), router);
    }

    private static async Task<RecordingResponseSink> Send(
        ConversationConductor conductor, string userId, string text, bool direct = true)
    {
        var sink = new RecordingResponseSink(FineGrained);
        await conductor.HandleAsync(
            new ChatMessage(userId, "c1", text, IsDirectMessage: direct), sink, CancellationToken.None);
        return sink;
    }

    [Theory]
    [InlineData("pair 482913")]
    [InlineData("  pair 482913\n")]
    [InlineData("Pair 482913")]
    [InlineData("PAIR   482913")]
    public async Task Correct_Code_In_A_Direct_Message_Replies_Paired_And_Reports_The_Sender(string text)
    {
        var signal = new RecordingPairingSignal();
        var (conductor, router) = Conductor(Pending(), signal);

        var sink = await Send(conductor, "111", text);

        sink.Completed.ShouldBeTrue();
        sink.FinalText.ShouldBe(PairedReply);
        signal.UserIds.ShouldBe(["111"]);
        router.DidNotReceive().Route(Arg.Any<ChatMessage>());
    }

    [Fact]
    public async Task Correct_Code_Outside_A_Direct_Message_Is_Refused_And_Nothing_Is_Reported()
    {
        var signal = new RecordingPairingSignal();
        var (conductor, router) = Conductor(Pending(), signal);

        var sink = await Send(conductor, "111", "pair 482913", direct: false);

        sink.FinalText.ShouldBe(RefusedReply);
        signal.UserIds.ShouldBeEmpty();
        router.DidNotReceive().Route(Arg.Any<ChatMessage>());
    }

    [Fact]
    public async Task Correct_Code_Outside_A_Direct_Message_Burns_It_So_The_Same_Code_Is_Refused_In_A_Direct_Message()
    {
        var signal = new RecordingPairingSignal();
        var (conductor, _) = Conductor(Pending(), signal);

        var exposed = await Send(conductor, "111", "pair 482913", direct: false);
        var later = await Send(conductor, "111", "pair 482913");

        exposed.FinalText.ShouldBe(RefusedReply);
        later.FinalText.ShouldBe(RefusedReply);
        signal.UserIds.ShouldBeEmpty();
        signal.BurnedCount.ShouldBe(1);
    }

    [Fact]
    public async Task Five_Wrong_Codes_From_Any_Sender_Burn_The_Code()
    {
        var signal = new RecordingPairingSignal();
        var (conductor, _) = Conductor(Pending(), signal);
        string[] replies =
        [
            (await Send(conductor, "901", "pair 000001")).FinalText!,
            (await Send(conductor, "902", "pair 000002", direct: false)).FinalText!,
            (await Send(conductor, "903", "pair 000003")).FinalText!,
            (await Send(conductor, "904", "pair 000004")).FinalText!,
            (await Send(conductor, "905", "pair 000005")).FinalText!,
        ];

        var correct = await Send(conductor, "111", "pair 482913");

        replies.ShouldAllBe(reply => reply == RefusedReply);
        correct.FinalText.ShouldBe(RefusedReply);
        signal.UserIds.ShouldBeEmpty();
        signal.BurnedCount.ShouldBe(1);
    }

    [Fact]
    public async Task Four_Wrong_Codes_Still_Let_The_Correct_Code_Pair()
    {
        var signal = new RecordingPairingSignal();
        var (conductor, _) = Conductor(Pending(), signal);
        for (var i = 1; i <= 4; i++)
        {
            await Send(conductor, "901", $"pair 00000{i}");
        }

        var correct = await Send(conductor, "111", "pair 482913");

        correct.FinalText.ShouldBe(PairedReply);
        signal.UserIds.ShouldBe(["111"]);
        signal.BurnedCount.ShouldBe(0);
    }

    [Fact]
    public async Task When_Reporting_The_Burn_Fails_The_Reply_Is_Still_The_Same_Refusal()
    {
        var (conductor, _) = Conductor(Pending(), new FailingPairingSignal());

        var sink = await Send(conductor, "111", "pair 482913", direct: false);

        sink.Completed.ShouldBeTrue();
        sink.FinalText.ShouldBe(RefusedReply);
    }

    [Fact]
    public async Task Wrong_Code_Is_Refused_And_Nothing_Is_Reported()
    {
        var signal = new RecordingPairingSignal();
        var (conductor, _) = Conductor(Pending(), signal);

        var sink = await Send(conductor, "111", "pair 482914");

        sink.FinalText.ShouldBe(RefusedReply);
        signal.UserIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task Expired_Code_Is_Refused_And_Nothing_Is_Reported()
    {
        var signal = new RecordingPairingSignal();
        var time = new FakeTimeProvider(Now);
        var (conductor, _) = Conductor(Pending(Now.AddMinutes(10)), signal, time);
        time.Advance(TimeSpan.FromMinutes(10));

        var sink = await Send(conductor, "111", "pair 482913");

        sink.FinalText.ShouldBe(RefusedReply);
        signal.UserIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task Code_Is_Accepted_Only_Once()
    {
        var signal = new RecordingPairingSignal();
        var (conductor, _) = Conductor(Pending(), signal);

        var first = await Send(conductor, "111", "pair 482913");
        var second = await Send(conductor, "333", "pair 482913");

        first.FinalText.ShouldBe(PairedReply);
        second.FinalText.ShouldBe(RefusedReply);
        signal.UserIds.ShouldBe(["111"]);
    }

    [Fact]
    public async Task Without_A_Configured_Code_A_Pairing_Message_Is_Refused()
    {
        var signal = new RecordingPairingSignal();
        var (conductor, _) = Conductor(new AccessOptions { RequireAllowList = true }, signal);

        var sink = await Send(conductor, "111", "pair 482913");

        sink.FinalText.ShouldBe(RefusedReply);
        signal.UserIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_A_Host_Pairing_Signal_The_Correct_Code_Is_Refused()
    {
        var (conductor, _) = Conductor(Pending(), signal: null);

        var sink = await Send(conductor, "111", "pair 482913");

        sink.FinalText.ShouldBe(RefusedReply);
    }

    [Theory]
    [InlineData("pair 48291")]
    [InlineData("please pair 482913")]
    [InlineData("pair 482913 now")]
    [InlineData("pair\t482913")]
    [InlineData("4829130")]
    public async Task Text_That_Is_Not_Exactly_Pair_And_Six_Digits_Goes_Through_The_Allow_List(string text)
    {
        var signal = new RecordingPairingSignal();
        var (conductor, router) = Conductor(Pending(), signal);

        var sink = await Send(conductor, "111", text);

        sink.FinalText.ShouldBe(NoAllowedUsersReply);
        signal.UserIds.ShouldBeEmpty();
        router.DidNotReceive().Route(Arg.Any<ChatMessage>());
    }

    [Fact]
    public async Task A_Paired_User_Not_Yet_In_The_List_Is_Still_Refused_By_The_Allow_List()
    {
        var signal = new RecordingPairingSignal();
        var access = Pending();
        access.AllowedUserIds = ["999"];
        var (conductor, _) = Conductor(access, signal);

        await Send(conductor, "111", "pair 482913");
        var next = await Send(conductor, "111", "list my clusters");

        next.FinalText.ShouldBe(NotAllowedReply);
    }

    [Fact]
    public async Task When_Reporting_The_Pairing_Fails_The_User_Is_Not_Told_Paired()
    {
        var (conductor, _) = Conductor(Pending(), new FailingPairingSignal());

        var sink = await Send(conductor, "111", "pair 482913");

        sink.Failed.ShouldBeTrue();
        sink.Completed.ShouldBeFalse();
        sink.FinalText.ShouldNotBe(PairedReply);
    }

    [Fact]
    public async Task A_Failed_Pairing_Notification_Leaves_The_Code_Usable_So_A_Retry_Pairs()
    {
        var signal = new FailingOncePairingSignal();
        var (conductor, _) = Conductor(Pending(), signal);

        var first = await Send(conductor, "111", "pair 482913");
        var retry = await Send(conductor, "111", "pair 482913");
        var other = await Send(conductor, "222", "pair 482913");

        first.Failed.ShouldBeTrue();
        first.FinalText.ShouldNotBe(PairedReply);
        retry.FinalText.ShouldBe(PairedReply);
        other.FinalText.ShouldBe(RefusedReply);
        signal.Reports.ShouldBe(["111", "111"]);
    }

    [Fact]
    public async Task A_Pairing_Attempt_While_A_Report_Is_In_Flight_Is_Refused_And_Never_Reported()
    {
        var signal = new FailingOncePairingSignal();
        var (conductor, _) = Conductor(Pending(), signal);
        RecordingResponseSink? during = null;
        signal.DuringFirstReport = () => during = Send(conductor, "333", "pair 482913").GetAwaiter().GetResult();

        await Send(conductor, "111", "pair 482913");

        during!.FinalText.ShouldBe(RefusedReply);
        signal.Reports.ShouldBe(["111"]);
    }

    [Fact]
    public async Task Pairing_Logs_Never_Carry_The_Code_The_Text_Or_The_Sender()
    {
        var logger = new RecordingLogger<ConversationConductor>();
        var (conductor, _) = Conductor(Pending(), new RecordingPairingSignal(), logger: logger);

        await Send(conductor, "111", "pair 482913");
        await Send(conductor, "222", "pair 650021");

        logger.Entries.Count.ShouldBe(2);
        logger.Entries.ShouldAllBe(e => e.Message.Contains("c1"));
        logger.Entries.ShouldAllBe(e =>
            !e.Message.Contains(Code) && !e.Message.Contains("650021")
            && !e.Message.Contains("111") && !e.Message.Contains("222"));
    }

    [Theory]
    [InlineData("482913")]
    [InlineData("  482913\n")]
    public async Task BareCodeInDirectMessage_Pairs(string text)
    {
        var signal = new RecordingPairingSignal();
        var (conductor, router) = Conductor(Pending(), signal);

        var sink = await Send(conductor, "111", text);

        sink.FinalText.ShouldBe(PairedReply);
        signal.UserIds.ShouldBe(["111"]);
        router.DidNotReceive().Route(Arg.Any<ChatMessage>());
    }

    [Fact]
    public async Task BareWrongCodes_BurnAfterFive()
    {
        var signal = new RecordingPairingSignal();
        var (conductor, _) = Conductor(Pending(), signal);
        var replies = new List<string>();
        for (var i = 1; i <= 5; i++)
        {
            replies.Add((await Send(conductor, "901", $"00000{i}")).FinalText!);
        }

        var correct = await Send(conductor, "111", Code);

        replies.ShouldAllBe(reply => reply == RefusedReply);
        correct.FinalText.ShouldBe(NoAllowedUsersReply);
        signal.UserIds.ShouldBeEmpty();
        signal.BurnedCount.ShouldBe(1);
    }

    [Fact]
    public async Task BareCodeOutsideDirectMessage_IsNotAPairingAttempt()
    {
        var signal = new RecordingPairingSignal();
        var (conductor, router) = Conductor(Pending(), signal);
        var inChannel = new List<string>();
        for (var i = 1; i <= 5; i++)
        {
            inChannel.Add((await Send(conductor, "111", $"10000{i}", direct: false)).FinalText!);
        }

        var inDirectMessage = await Send(conductor, "111", Code);

        inChannel.ShouldAllBe(reply => reply == NoAllowedUsersReply);
        signal.BurnedCount.ShouldBe(0);
        inDirectMessage.FinalText.ShouldBe(PairedReply);
        signal.UserIds.ShouldBe(["111"]);
        router.DidNotReceive().Route(Arg.Any<ChatMessage>());
    }

    [Fact]
    public async Task BareCorrectCodeOutsideDirectMessage_BurnsIt()
    {
        var signal = new RecordingPairingSignal();
        var (conductor, router) = Conductor(Pending(), signal);

        var inChannel = await Send(conductor, "111", Code, direct: false);
        var inDirectMessage = await Send(conductor, "111", Code);

        inChannel.FinalText.ShouldBe(RefusedReply);
        signal.BurnedCount.ShouldBe(1);
        inDirectMessage.FinalText.ShouldBe(NoAllowedUsersReply);
        signal.UserIds.ShouldBeEmpty();
        router.DidNotReceive().Route(Arg.Any<ChatMessage>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BareDigitsWithoutActivePairing_RouteNormally(bool expired)
    {
        var signal = new RecordingPairingSignal();
        var access = expired ? Pending(Now.AddMinutes(-1)) : new AccessOptions { RequireAllowList = true };
        access.AllowedUserIds = ["111"];
        var (conductor, router) = Conductor(access, signal);
        router.Route(Arg.Any<ChatMessage>()).Returns(_ => new ChatResponse(Tokens("answer about 482913")));

        var sink = await Send(conductor, "111", Code);

        sink.FinalText.ShouldBe("answer about 482913");
        router.Received(1).Route(Arg.Is<ChatMessage>(m => m.Text == Code));
        signal.UserIds.ShouldBeEmpty();
        signal.BurnedCount.ShouldBe(0);
    }

    [Fact]
    public async Task EmptyAllowList_RefusalExplainsHowToPair()
    {
        var (conductor, router) = Conductor(new AccessOptions { RequireAllowList = true }, new RecordingPairingSignal());

        var sink = await Send(conductor, "111", "hello", direct: false);

        sink.Completed.ShouldBeTrue();
        sink.FinalText.ShouldBe(
            "Scout is not set up for anyone yet. If you are the owner, open the Scout desktop app, go to Settings, "
            + "choose Pair from chat and send pair followed by the 6-digit code here in a direct message.");
        router.DidNotReceive().Route(Arg.Any<ChatMessage>());
    }

    private static async IAsyncEnumerable<string> Tokens(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }
}
