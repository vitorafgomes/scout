using Scout.ControlPlane.Contracts;
using Scout.Llm.Abstractions;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

public sealed class GrpcWriteGatePayloadTests
{
    private const string Token = "worker-session-token";

    private static IWriteGate ThroughTheInterface(IWriteGate gate) => gate;

    [Fact]
    public async Task The_Function_s_Arguments_Reach_The_Control_Plane_As_The_Request_s_Payload()
    {
        var service = new GrpcWriteGateTests.FakeWorkerGate { Reply = new RequestWriteResponse { Decision = "queued", ActionId = "act-1" } };
        await using var server = await GrpcWriteGateTests.WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);

        await ThroughTheInterface(gate).RequestAsync("github", "Pull request verdicts", "GitHubActions.give_pull_request_verdict", "s", """{"number":"42"}""", CancellationToken.None);

        service.Request.ShouldNotBeNull().Payload.ShouldBe("""{"number":"42"}""");
    }

    [Fact]
    public async Task A_Request_Without_A_Payload_Sends_None()
    {
        var service = new GrpcWriteGateTests.FakeWorkerGate { Reply = new RequestWriteResponse { Decision = "allowed" } };
        await using var server = await GrpcWriteGateTests.WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);

        await gate.RequestAsync("github", "New issues", "t", "s", CancellationToken.None);

        service.Request.ShouldNotBeNull().HasPayload.ShouldBeFalse();
    }

    [Fact]
    public async Task The_Result_Of_A_Recorded_Action_Reaches_The_Control_Plane()
    {
        var service = new GrpcWriteGateTests.FakeWorkerGate();
        await using var server = await GrpcWriteGateTests.WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);

        await ThroughTheInterface(gate).RecordAsync("github", "Pull request verdicts", "octocat/hello#42", "succeeded", "act-1", "https://github.com/octocat/hello/pull/42#pullrequestreview-88", CancellationToken.None);

        var record = service.Record.ShouldNotBeNull();
        (record.Outcome, record.ActionId, record.Result).ShouldBe(("succeeded", "act-1", "https://github.com/octocat/hello/pull/42#pullrequestreview-88"));
    }

    [Fact]
    public async Task A_Claimed_Action_Comes_Back_With_Its_Payload()
    {
        var service = new GrpcWriteGateTests.FakeWorkerGate
        {
            ActionReply = new NextApprovedActionResponse
            {
                Action = new PendingAction { Id = "act-8", FeatureId = "github", Action = "Pull request verdicts", Target = "octocat/hello#42", Summary = "Approve octocat/hello#42" },
                Payload = """{"owner":"octocat"}""",
            },
        };
        await using var server = await GrpcWriteGateTests.WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);

        var claimed = await gate.ClaimNextAsync("github", CancellationToken.None);

        claimed.ShouldNotBeNull().Payload.ShouldBe("""{"owner":"octocat"}""");
    }
}
