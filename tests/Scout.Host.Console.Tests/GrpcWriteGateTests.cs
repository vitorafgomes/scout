using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Scout.ControlPlane.Contracts;
using Scout.Llm.Abstractions;
using Scout.Llm.Guard;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

public sealed class GrpcWriteGateTests
{
    private const string Token = "worker-session-token";

    [Theory]
    [InlineData("allowed", WriteGateDecisionKind.Allowed)]
    [InlineData("queued", WriteGateDecisionKind.Queued)]
    [InlineData("refused", WriteGateDecisionKind.Refused)]
    [InlineData("", WriteGateDecisionKind.Refused)]
    [InlineData("ALLOWED", WriteGateDecisionKind.Refused)]
    [InlineData("yes", WriteGateDecisionKind.Refused)]
    public async Task Only_Allowed_And_Queued_Are_Read_As_Such_And_Every_Other_Decision_Is_A_Refusal(
        string decision, WriteGateDecisionKind expected)
    {
        var service = new FakeWorkerGate { Reply = new RequestWriteResponse { Decision = decision, Reason = "because" } };
        await using var server = await WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);

        var result = await gate.RequestAsync("youtube", "videos.insert", "YouTube.upload_video", "s", CancellationToken.None);

        result.Kind.ShouldBe(expected);
        result.Reason.ShouldBe("because");
    }

    [Theory]
    [InlineData("queued", "act-9", WriteGateDecisionKind.Queued, "", "act-9")]
    [InlineData("nonsense", "act-9", WriteGateDecisionKind.Refused, "The control plane refused this write.", null)]
    public async Task A_Queued_Decision_Carries_The_Action_Id_And_A_Refusal_Without_Reason_Gets_A_Fixed_One(
        string decision, string actionId, WriteGateDecisionKind kind, string reason, string? expectedActionId)
    {
        var service = new FakeWorkerGate { Reply = new RequestWriteResponse { Decision = decision, ActionId = actionId } };
        await using var server = await WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);

        var result = await gate.RequestAsync("youtube", "videos.insert", "t", "s", CancellationToken.None);

        result.ShouldBe(new WriteGateDecision(kind, reason, expectedActionId));
    }

    [Fact]
    public async Task A_Feature_Claims_Its_Next_Approved_Action_By_Name_And_Reads_It_Back()
    {
        var service = new FakeWorkerGate
        {
            ActionReply = new NextApprovedActionResponse
            {
                Action = new PendingAction { Id = "act-7", FeatureId = "github", Action = "New issues", Target = "o/r", Summary = "Open an issue on o/r: t" },
            },
        };
        var empty = new FakeWorkerGate();
        await using var server = await WorkerGateServer.StartAsync(service);
        await using var emptyServer = await WorkerGateServer.StartAsync(empty);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);
        using var emptyGate = new GrpcWriteGate(emptyServer.SocketPath, Token);

        var claimed = await gate.ClaimNextAsync("github", CancellationToken.None);
        var none = await emptyGate.ClaimNextAsync("github", CancellationToken.None);

        claimed.ShouldBe(new ApprovedAction("act-7", "github", "New issues", "o/r", "Open an issue on o/r: t"));
        service.ClaimRequest.ShouldNotBeNull().FeatureId.ShouldBe("github");
        none.ShouldBeNull();
    }

    [Fact]
    public async Task The_Request_Reaches_The_Control_Plane_With_The_Write_And_The_Bearer_Token()
    {
        var service = new FakeWorkerGate { Reply = new RequestWriteResponse { Decision = "allowed" } };
        await using var server = await WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);

        await gate.RequestAsync("youtube", "videos.insert", "YouTube.upload_video", "upload_video(title=x)", CancellationToken.None);
        await gate.RecordAsync("youtube", "videos.insert", "YouTube.upload_video", "succeeded", "act-3", CancellationToken.None);

        service.Request.ShouldBe(new RequestWriteRequest
        {
            FeatureId = "youtube",
            Scope = "videos.insert",
            Target = "YouTube.upload_video",
            Summary = "upload_video(title=x)",
        });
        service.Record.ShouldBe(new RecordWriteRequest
        {
            FeatureId = "youtube",
            Scope = "videos.insert",
            Target = "YouTube.upload_video",
            Outcome = "succeeded",
            ActionId = "act-3",
        });
        service.Authorizations.ShouldBe(["Bearer worker-session-token", "Bearer worker-session-token"]);
    }

    [Fact]
    public async Task With_The_Control_Plane_Down_A_Declared_Write_Never_Runs_And_The_Model_Is_Told_Why()
    {
        var socketPath = Path.Combine(Path.GetTempPath(), $"scout-no-gate-{Guid.NewGuid():N}.sock");
        using var gate = new GrpcWriteGate(socketPath, Token);
        var probe = new WriteProbe(gate);

        var result = await probe.UploadAsync();

        probe.Ran.ShouldBeFalse();
        result.ShouldBe("Scout could not reach its control plane, so this write was not performed.");
    }

    [Fact]
    public async Task A_Control_Plane_That_Misses_The_Deadline_Means_The_Declared_Write_Never_Runs()
    {
        var service = new FakeWorkerGate
        {
            Reply = new RequestWriteResponse { Decision = "allowed" },
            Delay = TimeSpan.FromSeconds(5),
        };
        await using var server = await WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token, TimeSpan.FromMilliseconds(200));
        var probe = new WriteProbe(gate);

        var result = await probe.UploadAsync();

        probe.Ran.ShouldBeFalse();
        result.ShouldBe("Scout could not reach its control plane, so this write was not performed.");
    }

    [Fact]
    public async Task A_Record_The_Control_Plane_Refuses_Leaves_The_Writes_Result_And_Logs_One_Warning_Naming_The_Function()
    {
        var service = new FakeWorkerGate
        {
            Reply = new RequestWriteResponse { Decision = "allowed" },
            RecordFailure = new RpcException(new Status(StatusCode.FailedPrecondition, "videos.insert is irreversible")),
        };
        await using var server = await WorkerGateServer.StartAsync(service);
        using var gate = new GrpcWriteGate(server.SocketPath, Token);
        var logger = new RecordingLogger();
        var probe = new WriteProbe(gate, logger);

        string? result = null;
        await Should.NotThrowAsync(async () => result = await probe.UploadAsync());

        result.ShouldBe("uploaded");
        probe.Ran.ShouldBeTrue();
        service.Record.ShouldNotBeNull().Outcome.ShouldBe("succeeded");
        var warning = logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldBe("The write gate did not record YouTube.upload_video (RpcException).");
    }

    [Fact]
    public async Task The_Closed_Gate_Refuses_Every_Write_Naming_The_Missing_Control_Plane()
    {
        var probe = new WriteProbe(new ClosedWriteGate());

        var result = await probe.UploadAsync();

        probe.Ran.ShouldBeFalse();
        result.ShouldBe("Writes are off: Scout Desktop's control plane is not available.");
    }

    [Theory]
    [InlineData("/run/scout/cp.sock", "t0ken", typeof(GrpcWriteGate))]
    [InlineData("/run/scout/cp.sock", "", typeof(ClosedWriteGate))]
    [InlineData("", "t0ken", typeof(ClosedWriteGate))]
    [InlineData(null, null, typeof(ClosedWriteGate))]
    public void The_Host_Uses_The_Control_Planes_Gate_Only_When_Both_The_Socket_And_The_Token_Are_Set(
        string? socketPath, string? token, Type expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:WorkerGate:SocketPath"] = socketPath,
                ["Scout:WorkerGate:Token"] = token,
            })
            .Build();
        using var provider = new ServiceCollection().AddLogging().AddScoutHost(configuration).BuildServiceProvider();

        provider.GetRequiredService<IWriteGate>().ShouldBeOfType(expected);
    }

    private sealed class WriteProbe(IWriteGate gate, ILogger<WriteGateFilter>? logger = null)
    {
        public bool Ran { get; private set; }

        public async Task<string?> UploadAsync()
        {
            var builder = Kernel.CreateBuilder();
            builder.Services.AddSingleton<IFunctionInvocationFilter>(new WriteGateFilter(
                [new WriteFunctionDeclaration("YouTube", "upload_video", "youtube", "videos.insert")],
                gate,
                logger ?? NullLogger<WriteGateFilter>.Instance));
            builder.Plugins.Add(KernelPluginFactory.CreateFromFunctions("YouTube",
            [
                KernelFunctionFactory.CreateFromMethod(
                    (string title) =>
                    {
                        Ran = true;
                        return "uploaded";
                    },
                    "upload_video"),
            ]));
            var result = await builder.Build().InvokeAsync(
                "YouTube", "upload_video", new KernelArguments { ["title"] = "Launch day" });
            return result.GetValue<string>();
        }
    }

    internal sealed class FakeWorkerGate : WorkerGate.WorkerGateBase
    {
        public RequestWriteResponse Reply { get; set; } = new();

        public TimeSpan Delay { get; init; }

        public RpcException? RecordFailure { get; init; }

        public RequestWriteRequest? Request { get; private set; }

        public RecordWriteRequest? Record { get; private set; }

        public List<string?> Authorizations { get; } = [];

        public override async Task<RequestWriteResponse> RequestWrite(RequestWriteRequest request, ServerCallContext context)
        {
            Authorizations.Add(context.RequestHeaders.GetValue("authorization"));
            Request = request;
            await Task.Delay(Delay, context.CancellationToken);
            return Reply;
        }

        public NextApprovedActionRequest? ClaimRequest { get; private set; }

        public override Task<RecordWriteResponse> RecordWrite(RecordWriteRequest request, ServerCallContext context)
        {
            if (OperatorFailure is not null)
            {
                throw OperatorFailure;
            }

            Authorizations.Add(context.RequestHeaders.GetValue("authorization"));
            Record = request;
            return RecordFailure is null ? Task.FromResult(new RecordWriteResponse()) : throw RecordFailure;
        }

        public Mandate MandateReply { get; init; } = new();

        public NextQueuedProjectResponse NextReply { get; init; } = new();

        public RpcException? OperatorFailure { get; init; }

        public SaveOperatorProjectRequest? Saved { get; private set; }

        public override Task<Mandate> GetOperatorMandate(GetMandateRequest request, ServerCallContext context)
        {
            Authorizations.Add(context.RequestHeaders.GetValue("authorization"));
            return OperatorFailure is null ? Task.FromResult(MandateReply) : throw OperatorFailure;
        }

        public override Task<Project> SaveOperatorProject(SaveOperatorProjectRequest request, ServerCallContext context)
        {
            Authorizations.Add(context.RequestHeaders.GetValue("authorization"));
            Saved = request;
            if (OperatorFailure is not null)
            {
                throw OperatorFailure;
            }

            var saved = request.Project.Clone();
            saved.Id = string.IsNullOrEmpty(saved.Id) ? "cp-1" : saved.Id;
            return Task.FromResult(saved);
        }

        public override Task<NextQueuedProjectResponse> NextQueuedProject(NextQueuedProjectRequest request, ServerCallContext context)
        {
            Authorizations.Add(context.RequestHeaders.GetValue("authorization"));
            return OperatorFailure is null ? Task.FromResult(NextReply) : throw OperatorFailure;
        }

        public ListProjectsResponse ListReply { get; init; } = new();

        public ListProjectsRequest? Listed { get; private set; }

        public GetProjectRequest? Got { get; private set; }

        public override Task<Project> GetOperatorProject(GetProjectRequest request, ServerCallContext context)
        {
            Authorizations.Add(context.RequestHeaders.GetValue("authorization"));
            Got = request;
            return OperatorFailure is null
                ? Task.FromResult(ListReply.Projects.First(p => p.Id == request.Id))
                : throw OperatorFailure;
        }

        public override Task<ListProjectsResponse> ListOperatorProjects(ListProjectsRequest request, ServerCallContext context)
        {
            Authorizations.Add(context.RequestHeaders.GetValue("authorization"));
            Listed = request;
            return OperatorFailure is null ? Task.FromResult(ListReply) : throw OperatorFailure;
        }

        public string MediaPath { get; init; } = "/data/scout/operator/media/p1";

        public Readiness? Reported { get; private set; }

        public override Task<MediaDirectory> GetOperatorMediaDirectory(GetProjectRequest request, ServerCallContext context)
        {
            Authorizations.Add(context.RequestHeaders.GetValue("authorization"));
            Got = request;
            return OperatorFailure is null ? Task.FromResult(new MediaDirectory { Path = MediaPath }) : throw OperatorFailure;
        }

        public NextApprovedActionResponse ActionReply { get; init; } = new();

        public override Task<NextApprovedActionResponse> NextApprovedAction(NextApprovedActionRequest request, ServerCallContext context)
        {
            Authorizations.Add(context.RequestHeaders.GetValue("authorization"));
            ClaimRequest = request;
            return OperatorFailure is null ? Task.FromResult(ActionReply) : throw OperatorFailure;
        }

        public override Task<Readiness> ReportReadiness(Readiness request, ServerCallContext context)
        {
            Authorizations.Add(context.RequestHeaders.GetValue("authorization"));
            Reported = request;
            return OperatorFailure is null ? Task.FromResult(request) : throw OperatorFailure;
        }

        public CommentsView CommentsReply { get; init; } = new();

        public CommentsView? SavedComments { get; private set; }

        public override Task<CommentsView> GetComments(ListCommentsRequest request, ServerCallContext context)
        {
            Authorizations.Add(context.RequestHeaders.GetValue("authorization"));
            return OperatorFailure is null ? Task.FromResult(CommentsReply) : throw OperatorFailure;
        }

        public override Task<CommentsView> SaveComments(CommentsView request, ServerCallContext context)
        {
            Authorizations.Add(context.RequestHeaders.GetValue("authorization"));
            SavedComments = request;
            return OperatorFailure is null ? Task.FromResult(request.Clone()) : throw OperatorFailure;
        }

        public string VoiceSamplesPath { get; init; } = "/data/scout/operator/voice-samples";

        public override Task<MediaDirectory> GetVoiceSamplesDirectory(GetVoiceSamplesDirectoryRequest request, ServerCallContext context)
        {
            Authorizations.Add(context.RequestHeaders.GetValue("authorization"));
            return OperatorFailure is null ? Task.FromResult(new MediaDirectory { Path = VoiceSamplesPath }) : throw OperatorFailure;
        }

        public List<(string Rpc, Google.Protobuf.IMessage Request)> ChatRequests { get; } = [];

        public override Task<Project> ChatEditSceneText(EditSceneTextRequest request, ServerCallContext context)
            => ChatAsync(nameof(ChatEditSceneText), request, request.ProjectId, context);

        public override Task<Project> ChatRegenerateNarration(SceneRequest request, ServerCallContext context)
            => ChatAsync(nameof(ChatRegenerateNarration), request, request.ProjectId, context);

        public override Task<Project> ChatRegenerateScene(SceneRequest request, ServerCallContext context)
            => ChatAsync(nameof(ChatRegenerateScene), request, request.ProjectId, context);

        public override Task<Project> ChatSetSceneLocked(SetSceneLockedRequest request, ServerCallContext context)
            => ChatAsync(nameof(ChatSetSceneLocked), request, request.ProjectId, context);

        public override Task<Project> ChatReorderScenes(ReorderScenesRequest request, ServerCallContext context)
            => ChatAsync(nameof(ChatReorderScenes), request, request.ProjectId, context);

        public override Task<Project> ChatProduceProject(ProduceProjectRequest request, ServerCallContext context)
            => ChatAsync(nameof(ChatProduceProject), request, request.Id, context);

        public override Task<Project> ChatDelegateProject(DelegateProjectRequest request, ServerCallContext context)
            => ChatAsync(nameof(ChatDelegateProject), request, request.Id, context);

        public override Task<Project> ChatRequestPresenter(RequestPresenterRequest request, ServerCallContext context)
            => ChatAsync(nameof(ChatRequestPresenter), request, request.ProjectId, context);

        private Task<Project> ChatAsync(string rpc, Google.Protobuf.IMessage request, string projectId, ServerCallContext context)
        {
            Authorizations.Add(context.RequestHeaders.GetValue("authorization"));
            ChatRequests.Add((rpc, request));
            return OperatorFailure is null
                ? Task.FromResult(new Project { Id = projectId, Topic = "sourdough", Status = "production_queued", Origin = "desktop", Format = "own_pictures" })
                : throw OperatorFailure;
        }
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

    internal sealed class WorkerGateServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _directory;

        private WorkerGateServer(WebApplication app, string directory)
        {
            _app = app;
            _directory = directory;
        }

        public string SocketPath => Path.Combine(_directory, "gate.sock");

        public static async Task<WorkerGateServer> StartAsync<TService>(TService service)
            where TService : WorkerGate.WorkerGateBase
        {
            var directory = Directory.CreateTempSubdirectory("scout-gate-").FullName;
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls([]);
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenUnixSocket(
                Path.Combine(directory, "gate.sock"), listen => listen.Protocols = HttpProtocols.Http2));
            builder.Services.AddGrpc();
            builder.Services.AddSingleton(service);
            var app = builder.Build();
            app.MapGrpcService<TService>();
            await app.StartAsync();
            return new WorkerGateServer(app, directory);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
