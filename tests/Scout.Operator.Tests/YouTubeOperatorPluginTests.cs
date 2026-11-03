using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Conversation;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Streaming;
using Scout.Llm.Abstractions;
using Scout.Operator.Tests.Fakes;
using Scout.Plugin.YouTube.Abstractions;

namespace Scout.Operator.Tests;

public sealed class YouTubeOperatorPluginTests
{
    private readonly RecordingControlPlane _controlPlane = new();
    private readonly Channel<QueuedProject> _started = Channel.CreateUnbounded<QueuedProject>();

    private YouTubeOperatorPlugin Plugin(IOperatorControlPlane? controlPlane = null)
        => new(controlPlane ?? _controlPlane, _started);

    private static async IAsyncEnumerable<string> Reply(Func<Task<string>> call)
    {
        yield return await call();
    }

    private async Task<string?> AskInATurnFrom(string origin, string topic)
    {
        var clock = new FakeTimeProvider(Given.Now);
        var router = Substitute.For<IConversationRouter>();
        router.Route(Arg.Any<ChatMessage>()).Returns(_ => new ChatResponse(Reply(() => Plugin().MakeVideoAsync(topic))));
        var sink = Substitute.For<IResponseSink>();
        sink.Capabilities.Returns(new RenderCapabilities { FineGrainedEdit = true, MaxBodyLength = 4096 });
        string? final = null;
        sink.CompleteAsync(Arg.Do<string>(text => final = text), Arg.Any<IReadOnlyList<Citation>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var conductor = new ConversationConductor(router, new StreamPump(new StreamPumpOptions { TimeProvider = clock }), timeProvider: clock);

        await conductor.HandleAsync(
            new ChatMessage("U1", "C1", "make a video about sourdough", Given.Now) { OriginReference = origin },
            sink,
            CancellationToken.None);
        return final;
    }

    [Fact]
    public async Task Make_Video_Saves_A_New_Chat_Project_With_The_Turns_Origin_And_Hands_It_To_The_Worker()
    {
        var reply = await AskInATurnFrom("discord:3333:42", "  sourdough  ");

        var (saved, origin) = _controlPlane.Saves.ShouldHaveSingleItem();
        (saved.Id, saved.Topic, saved.Status, saved.Origin).ShouldBe((string.Empty, "sourdough", "researching", "chat"));
        origin.ShouldBe("discord:3333:42");
        _started.Reader.TryRead(out var handedOver).ShouldBeTrue();
        handedOver.ShouldBe(new QueuedProject(saved with { Id = "new-1" }, "discord:3333:42"));
        reply.ShouldBe("Started a video project on sourdough; I will post here when the script is ready.");
    }

    [Fact]
    public async Task Outside_A_Chat_Turn_The_Project_Starts_Without_An_Origin_And_The_Reply_Points_To_The_Desktop()
    {
        var reply = await Plugin().MakeVideoAsync("sourdough");

        _controlPlane.Saves.ShouldHaveSingleItem().Origin.ShouldBeNull();
        reply.ShouldBe("Started a video project on sourdough; follow it in Scout Desktop, Production.");
    }

    [Fact]
    public async Task When_The_Control_Plane_Fails_No_Project_Starts_And_The_Error_Says_Why()
    {
        var controlPlane = Substitute.For<IOperatorControlPlane>();
        controlPlane.GetMandateAsync(default).ReturnsForAnyArgs(Given.Mandate());
        controlPlane.SaveProjectAsync(default!, default, default)
            .ReturnsForAnyArgs<Task<OperatorProjectState>>(_ => throw new InvalidOperationException("Scout could not reach its control plane."));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Plugin(controlPlane).MakeVideoAsync("sourdough"));

        ex.Message.ShouldBe("Could not start a video project on sourdough: Scout could not reach its control plane.");
        _started.Reader.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_Topic_The_Mandate_Blocks_Starts_No_Project_And_The_Error_Says_Why()
    {
        var controlPlane = new RecordingControlPlane { Mandate = Given.Mandate(blocked: ["Gluten-Free"]) };

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Plugin(controlPlane).MakeVideoAsync(" An easy gluten-free loaf "));

        ex.Message.ShouldBe(
            "Could not start a video project on An easy gluten-free loaf: The channel mandate blocks the topic \"Gluten-Free\"; " +
            "choose another topic or change the mandate in Scout Desktop, Production.");
        controlPlane.Saves.ShouldBeEmpty();
        _started.Reader.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Status_Lists_The_Five_Latest_Projects_Of_The_Control_Plane_Newest_First_With_A_Failures_Reason()
    {
        foreach (var i in Enumerable.Range(1, 5))
        {
            _controlPlane.Store(Given.Project(id: $"p{i}", topic: $"topic {i}", status: "scripting"));
        }

        _controlPlane.Store(Given.Project(id: "p6", topic: "topic 6", status: "failed") with { Error = "YouTube is currently unavailable. Try again later." });

        var status = await Plugin().VideoStatusAsync();

        status.Split('\n').ShouldBe([
            "topic 6: failed (YouTube is currently unavailable. Try again later.)",
            "topic 5: scripting",
            "topic 4: scripting",
            "topic 3: scripting",
            "topic 2: scripting",
        ]);
    }

    [Fact]
    public async Task Status_Shows_How_Many_Scenes_Of_A_Producing_Project_Are_Ready()
    {
        _controlPlane.Store(Given.Producing(
            Given.Scene("s1", 1, status: "ready"),
            Given.Scene("s2", 2, status: "ready"),
            Given.Scene("s3", 3, status: "failed"),
            Given.Scene("s4", 4)));

        var status = await Plugin().VideoStatusAsync();

        status.ShouldBe("sourdough: producing (2 of 4 scenes ready, 1 failed)");
    }

    [Fact]
    public async Task Status_Says_So_When_No_Project_Exists()
        => (await Plugin().VideoStatusAsync()).ShouldBe("No video project exists yet.");

    [Fact]
    public async Task Status_Says_Why_When_The_Control_Plane_Cannot_List_The_Projects()
    {
        var controlPlane = Substitute.For<IOperatorControlPlane>();
        controlPlane.ListRecentAsync(default, default)
            .ReturnsForAnyArgs<Task<IReadOnlyList<OperatorProjectState>>>(_ => throw new InvalidOperationException("The owner has not granted YouTube writes."));

        var status = await Plugin(controlPlane).VideoStatusAsync();

        status.ShouldBe("Could not read the video projects: The owner has not granted YouTube writes.");
    }

    [Fact]
    public void The_Declared_Write_Names_The_Make_Video_Function_The_Plugin_Really_Has_With_Only_The_Topic_Shown()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IOperatorControlPlane>(_controlPlane);
        services.AddSingleton(Substitute.For<IYouTubeClient>());
        services.AddSingleton(Substitute.For<IChatCompletionService>());
        services.AddSingleton(Substitute.For<IOriginNotifier>());
        services.AddYouTubeOperator();
        using var provider = services.BuildServiceProvider();

        var declaration = provider.GetServices<WriteFunctionDeclaration>().ShouldHaveSingleItem();
        var plugin = provider.GetServices<KernelPlugin>().ShouldHaveSingleItem();

        (declaration.PluginName, declaration.FeatureId, declaration.Scope).ShouldBe((plugin.Name, "youtube", "Scout's data folder"));
        plugin.TryGetFunction(declaration.FunctionName, out var function).ShouldBeTrue();
        declaration.SummaryArguments.ShouldBe(["topic"]);
        function.Metadata.Parameters.Select(p => p.Name).ShouldBe(["topic"]);
    }
}
