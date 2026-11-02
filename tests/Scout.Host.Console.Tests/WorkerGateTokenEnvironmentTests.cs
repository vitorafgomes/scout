using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.ControlPlane.Contracts;
using Scout.Llm.Abstractions;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

[Collection("process environment")]
public sealed class WorkerGateTokenEnvironmentTests
{
    private const string SocketPathKey = "SCOUT__WORKERGATE__SOCKETPATH";

    private static readonly string[] TokenKeys =
    [
        "SCOUT__WORKERGATE__TOKEN",
        "scout__workergate__token",
        "Scout:WorkerGate:Token",
        "DOTNET_Scout__WorkerGate__Token",
        "ASPNETCORE_SCOUT__WORKERGATE__TOKEN",
    ];

    [Fact]
    public async Task The_Worker_Gate_Token_Leaves_The_Process_Environment_After_Composition_And_The_Gate_Still_Sends_It()
    {
        var service = new GrpcWriteGateTests.FakeWorkerGate { Reply = new RequestWriteResponse { Decision = "allowed" } };
        await using var server = await GrpcWriteGateTests.WorkerGateServer.StartAsync(service);
        var token = $"env-token-{Guid.NewGuid():N}";
        var saved = TokenKeys.Append(SocketPathKey).ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var key in TokenKeys)
            {
                Environment.SetEnvironmentVariable(key, token);
            }

            Environment.SetEnvironmentVariable(SocketPathKey, server.SocketPath);
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();

            using var provider = new ServiceCollection().AddLogging().AddScoutHost(configuration).BuildServiceProvider();
            var childEnvironment = new ProcessStartInfo().Environment;
            await provider.GetRequiredService<IWriteGate>()
                .RequestAsync("youtube", "videos.insert", "YouTube.upload_video", "s", CancellationToken.None);

            TokenKeys.Select(Environment.GetEnvironmentVariable).ShouldAllBe(value => value == null);
            childEnvironment.Values.ShouldNotContain(token);
            Environment.GetEnvironmentVariable(SocketPathKey).ShouldBe(server.SocketPath);
            service.Authorizations.ShouldBe([$"Bearer {token}"]);
        }
        finally
        {
            foreach (var (key, value) in saved)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}
