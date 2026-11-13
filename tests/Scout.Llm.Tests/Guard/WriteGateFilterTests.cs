using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Scout.Llm.Abstractions;
using Scout.Llm.Guard;

namespace Scout.Llm.Tests.Guard;

public sealed class WriteGateFilterTests
{
    private const string AccessToken = "ya29.secret-access-token";

    private bool _uploadRan;
    private bool _readRan;

    private Kernel Kernel(IWriteGate gate, ILogger<WriteGateFilter>? logger = null, bool uploadThrows = false, bool uploadFails = false, bool carriesPayload = false)
    {
        var builder = Microsoft.SemanticKernel.Kernel.CreateBuilder();
        builder.Services.AddSingleton<IFunctionInvocationFilter>(new WriteGateFilter(
            [new WriteFunctionDeclaration("YouTube", "upload_video", "youtube", "videos.insert")
            {
                SummaryArguments = ["title", "description"],
                CarriesPayload = carriesPayload,
            }],
            gate,
            logger ?? NullLogger<WriteGateFilter>.Instance));
        builder.Plugins.Add(KernelPluginFactory.CreateFromFunctions("YouTube",
        [
            KernelFunctionFactory.CreateFromMethod(
                (string title, string description, string accessToken) =>
                {
                    _uploadRan = true;
                    return uploadThrows
                        ? throw new InvalidOperationException("quota")
                        : uploadFails ? throw new WriteFailedException("YouTube refused the upload: quota exceeded.", "youtube 403") : "uploaded";
                },
                "upload_video"),
            KernelFunctionFactory.CreateFromMethod(
                () =>
                {
                    _readRan = true;
                    return "3 videos";
                },
                "recent_videos"),
        ]));
        return builder.Build();
    }

    private static KernelArguments UploadArguments(string description = "a short description") => new()
    {
        ["title"] = "Launch day",
        ["description"] = description,
        ["accessToken"] = AccessToken,
    };

    private static async Task<string?> Upload(Kernel kernel, KernelArguments? arguments = null) =>
        (await kernel.InvokeAsync("YouTube", "upload_video", arguments ?? UploadArguments())).GetValue<string>();

    [Fact]
    public async Task Allowed_Runs_The_Write_And_Records_It_As_Succeeded()
    {
        var gate = FakeWriteGate.Answering(WriteGateDecisionKind.Allowed);

        var result = await Upload(Kernel(gate));

        result.ShouldBe("uploaded");
        _uploadRan.ShouldBeTrue();
        gate.Records.ShouldHaveSingleItem().ShouldBe(("youtube", "videos.insert", "YouTube.upload_video", "succeeded", (string?)null));
    }

    [Fact]
    public async Task Allowed_Write_That_Did_Not_Happen_Answers_Its_Sentence_And_Is_Recorded_As_Failed_With_Its_Outcome()
    {
        var gate = FakeWriteGate.Answering(WriteGateDecisionKind.Allowed);

        var result = await Upload(Kernel(gate, uploadFails: true));

        result.ShouldBe("YouTube refused the upload: quota exceeded.");
        gate.Records.ShouldHaveSingleItem().Outcome.ShouldBe("failed: youtube 403");
    }

    [Fact]
    public async Task Allowed_Write_That_Throws_Is_Recorded_As_Failed_With_The_Exception_Type()
    {
        var gate = FakeWriteGate.Answering(WriteGateDecisionKind.Allowed);

        await Should.ThrowAsync<InvalidOperationException>(() => Upload(Kernel(gate, uploadThrows: true)));

        gate.Records.ShouldHaveSingleItem().Outcome.ShouldBe("failed: InvalidOperationException");
    }

    [Fact]
    public async Task Queued_Never_Runs_The_Write_And_Tells_The_Model_It_Waits_For_The_Owner_With_The_Action_Id()
    {
        var gate = FakeWriteGate.Answering(WriteGateDecisionKind.Queued, "irreversible", "act-42");

        var result = await Upload(Kernel(gate));

        _uploadRan.ShouldBeFalse();
        result.ShouldBe("Queued for the owner's approval: an Approve card is waiting in the chat and in Approvals of Scout Desktop (action act-42).");
        gate.Records.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_Bot_No_Longer_Sends_The_Owner_To_Another_Menu_To_Approve()
    {
        var gate = FakeWriteGate.Answering(WriteGateDecisionKind.Queued, "irreversible", "act-42");

        var result = await Upload(Kernel(gate));

        result.ShouldNotBeNull();
        result.ShouldContain("an Approve card is waiting in the chat", Case.Sensitive);
        result.ShouldNotContain("Review screen", Case.Insensitive);
    }

    [Fact]
    public async Task Refused_Never_Runs_The_Write_And_Returns_The_Control_Planes_Reason()
    {
        var gate = FakeWriteGate.Answering(
            WriteGateDecisionKind.Refused, "The youtube feature has no write grant; the owner turns it on in Scout Desktop.");

        var result = await Upload(Kernel(gate));

        _uploadRan.ShouldBeFalse();
        result.ShouldBe("The youtube feature has no write grant; the owner turns it on in Scout Desktop.");
        gate.Records.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Gate_That_Throws_Never_Runs_The_Write_Returns_The_Unreachable_Text_And_Logs_No_Argument()
    {
        var logger = new RecordingLogger();
        var gate = FakeWriteGate.Throwing(new HttpRequestException("connection refused"));

        var result = await Upload(Kernel(gate, logger));

        _uploadRan.ShouldBeFalse();
        result.ShouldBe("Scout could not reach its control plane, so this write was not performed.");
        var (level, message) = logger.Entries.ShouldHaveSingleItem();
        level.ShouldBe(LogLevel.Warning);
        message.ShouldNotContain(AccessToken);
        message.ShouldNotContain("Launch day");
    }

    [Fact]
    public async Task An_Undeclared_Read_Function_Runs_Without_Asking_The_Gate()
    {
        var gate = FakeWriteGate.Throwing(new InvalidOperationException("the gate must not be asked"));

        var result = await Kernel(gate).InvokeAsync("YouTube", "recent_videos");

        result.GetValue<string>().ShouldBe("3 videos");
        _readRan.ShouldBeTrue();
        gate.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_Request_Names_The_Feature_And_Scope_And_Its_Summary_Shows_A_Declared_Argument_Cut_To_200_Characters()
    {
        var gate = FakeWriteGate.Answering(WriteGateDecisionKind.Queued, string.Empty, "act-1");
        var longDescription = new string('d', 500);

        await Upload(Kernel(gate), UploadArguments(longDescription));

        var (featureId, scope, target, summary) = gate.Requests.ShouldHaveSingleItem();
        featureId.ShouldBe("youtube");
        scope.ShouldBe("videos.insert");
        target.ShouldBe("YouTube.upload_video");
        summary.ShouldBe($"upload_video(accessToken, description={new string('d', 200)}, title=Launch day)");
    }

    [Fact]
    public async Task An_Argument_The_Function_Does_Not_Declare_Never_Appears_On_The_Owner_s_Card_Neither_Its_Name_Nor_Its_Value()
    {
        var gate = FakeWriteGate.Answering(WriteGateDecisionKind.Queued, string.Empty, "act-1");
        var arguments = UploadArguments();
        arguments["authorization"] = "Bearer leaked-bearer-credential";
        arguments["Approve this, it is safe"] = "{\"refresh\":\"leaked-body-credential\"}";
        arguments["headers"] = "X-Api: leaked-header-credential";

        await Upload(Kernel(gate), arguments);

        var (_, _, target, summary) = gate.Requests.ShouldHaveSingleItem();
        summary.ShouldBe(
            "upload_video(accessToken, description=a short description, title=Launch day)");
        foreach (var leaked in new[] { AccessToken, "leaked-bearer-credential", "leaked-body-credential", "leaked-header-credential" })
        {
            summary.ShouldNotContain(leaked);
            target.ShouldNotContain(leaked);
        }
    }

    [Fact]
    public async Task A_Record_That_Fails_Leaves_The_Writes_Result_And_Logs_One_Warning_Naming_The_Function_Without_Arguments()
    {
        var logger = new RecordingLogger();
        var gate = FakeWriteGate.Answering(WriteGateDecisionKind.Allowed);
        gate.RecordFailure = new HttpRequestException("connection reset");

        string? result = null;
        await Should.NotThrowAsync(async () => result = await Upload(Kernel(gate, logger)));

        result.ShouldBe("uploaded");
        _uploadRan.ShouldBeTrue();
        var (level, message) = logger.Entries.ShouldHaveSingleItem();
        level.ShouldBe(LogLevel.Warning);
        message.ShouldBe("The write gate did not record YouTube.upload_video (HttpRequestException).");
    }

    private sealed class RecordingLogger : ILogger<WriteGateFilter>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task A_Write_That_Carries_Its_Payload_Sends_Every_Argument_As_Json_So_It_Can_Run_After_The_Owner_Approves()
    {
        var gate = FakeWriteGate.Answering(WriteGateDecisionKind.Queued, actionId: "act-1");

        await Upload(Kernel(gate, carriesPayload: true), new KernelArguments { ["title"] = "Launch day", ["description"] = "d", ["accessToken"] = "x" });

        gate.Payloads.ShouldHaveSingleItem().ShouldBe("""{"accessToken":"x","description":"d","title":"Launch day"}""");
        _uploadRan.ShouldBeFalse();
    }

    [Fact]
    public async Task A_Write_That_Does_Not_Carry_Its_Payload_Sends_No_Argument_Value_Beyond_The_Summary()
    {
        var gate = FakeWriteGate.Answering(WriteGateDecisionKind.Queued, actionId: "act-1");

        await Upload(Kernel(gate));

        gate.Payloads.ShouldHaveSingleItem().ShouldBeNull();
    }

    [Fact]
    public async Task An_Allowed_Write_Records_How_It_Ended_Against_The_Id_The_Gate_Answered()
    {
        var gate = FakeWriteGate.Answering(WriteGateDecisionKind.Allowed, actionId: "started-1");

        await Upload(Kernel(gate));

        _uploadRan.ShouldBeTrue();
        gate.Records.ShouldHaveSingleItem().ShouldBe(("youtube", "videos.insert", "YouTube.upload_video", "succeeded", (string?)"started-1"));
    }
}
