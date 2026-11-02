using Microsoft.Extensions.Options;
using NSubstitute;
using Scout.Conversation;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Access;
using Scout.Conversation.Streaming;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

public sealed class StdoutPairingSignalTests
{
    private sealed class FinalTextSink : IResponseSink
    {
        public string? FinalText { get; private set; }
        public RenderCapabilities Capabilities { get; } = new() { FineGrainedEdit = true };
        public Task BeginAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpdateAsync(string cumulativeText, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CompleteAsync(string finalText, IReadOnlyList<Citation> citations, CancellationToken cancellationToken)
        {
            FinalText = finalText;
            return Task.CompletedTask;
        }

        public Task FailAsync(string finalTextSoFar, string reason, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private static ConversationConductor Conductor(TextWriter stdout) => new(
        Substitute.For<IConversationRouter>(),
        new StreamPump(new StreamPumpOptions()),
        access: Options.Create(new AccessOptions
        {
            RequireAllowList = true,
            PairingCode = "482913",
            PairingExpiresAt = new DateTimeOffset(2999, 1, 1, 0, 10, 0, TimeSpan.Zero),
        }),
        pairingSignal: new StdoutPairingSignal(stdout));

    private static async Task<string?> Send(ConversationConductor conductor, string text, bool direct)
    {
        var sink = new FinalTextSink();
        await conductor.HandleAsync(
            new ChatMessage("111", "c1", text, IsDirectMessage: direct), sink, CancellationToken.None);
        return sink.FinalText;
    }

    [Fact]
    public async Task Correct_Code_In_A_Direct_Message_Writes_One_Scout_Paired_Line_With_The_Sender_Id()
    {
        using var stdout = new StringWriter();
        var conductor = Conductor(stdout);

        var reply = await Send(conductor, "pair 482913", direct: true);

        reply.ShouldBe("Paired. Scout will answer you from now on.");
        stdout.ToString().ShouldBe("SCOUT_PAIRED=111" + Environment.NewLine);
    }

    [Fact]
    public async Task A_Wrong_Code_Writes_Nothing_To_Stdout()
    {
        using var stdout = new StringWriter();

        var reply = await Send(Conductor(stdout), "pair 111111", direct: true);

        reply.ShouldBe("That pairing code is not valid. Check the code in Scout's Settings and send it again.");
        stdout.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_Correct_Code_Outside_A_Direct_Message_Writes_One_Burned_Line_And_No_Paired_Line()
    {
        using var stdout = new StringWriter();

        var reply = await Send(Conductor(stdout), "pair 482913", direct: false);

        reply.ShouldBe("That pairing code is not valid. Check the code in Scout's Settings and send it again.");
        stdout.ToString().ShouldBe("SCOUT_PAIRING_BURNED" + Environment.NewLine);
    }

    [Fact]
    public async Task Reused_Code_Writes_Only_The_First_Line()
    {
        using var stdout = new StringWriter();
        var conductor = Conductor(stdout);

        await Send(conductor, "pair 482913", direct: true);
        var second = await Send(conductor, "pair 482913", direct: true);

        second.ShouldBe("That pairing code is not valid. Check the code in Scout's Settings and send it again.");
        stdout.ToString().ShouldBe("SCOUT_PAIRED=111" + Environment.NewLine);
    }
}
