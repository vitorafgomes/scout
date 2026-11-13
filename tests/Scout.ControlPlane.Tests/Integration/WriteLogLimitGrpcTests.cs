using Microsoft.Extensions.DependencyInjection;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Contracts;
using Scout.ControlPlane.Features;
using Shouldly;
using Xunit;
using DomainWriteLogEntry = Scout.ControlPlane.Actions.WriteLogEntry;
using GrpcFeatures = Scout.ControlPlane.Contracts.Features;

namespace Scout.ControlPlane.Tests.Integration;

/// <summary>
/// The limit of the write-log list over the real UDS host.
/// </summary>
[Collection("uds")]
public sealed class WriteLogLimitGrpcTests
{
    [Fact]
    public async Task ListWrites_Asked_For_Five_Hundred_Answers_The_Newest_Two_Hundred_And_Fifty_By_Default()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        var log = host.Services.GetRequiredService<IWriteLog>();
        var at = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 205; i++)
        {
            await log.AppendAsync(
                new DomainWriteLogEntry(at.AddSeconds(i), FeatureCatalog.YouTubeId, "videos.insert", $"video-{i}", "ok", "owner", null),
                CancellationToken.None);
        }
        using var channel = host.CreateChannel();
        var client = new GrpcFeatures.FeaturesClient(channel);

        var capped = await client.ListWritesAsync(new ListWritesRequest { Limit = 500 });
        var atTheCap = await client.ListWritesAsync(new ListWritesRequest { Limit = 200 });
        var underTheCap = await client.ListWritesAsync(new ListWritesRequest { Limit = 3 });
        var defaulted = await client.ListWritesAsync(new ListWritesRequest());

        var newestTwoHundred = Enumerable.Range(5, 200).Reverse().Select(i => $"video-{i}").ToArray();
        capped.Entries.Count.ShouldBe(200);
        capped.Entries.Select(e => e.Target).ShouldBe(newestTwoHundred);
        atTheCap.Entries.Select(e => e.Target).ShouldBe(newestTwoHundred);
        underTheCap.Entries.Select(e => e.Target).ShouldBe(["video-204", "video-203", "video-202"]);
        defaulted.Entries.Select(e => e.Target).ShouldBe(newestTwoHundred[..50]);
    }
}
