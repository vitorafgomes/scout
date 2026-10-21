using Microsoft.Extensions.Options;
using Scout.Plugin.Common;
using Scout.Plugin.Common.Abstractions;
using Scout.Plugin.Parcels;
using Scout.Plugin.Parcels.Abstractions;
using Scout.Plugin.Parcels.Tests.Fakes;

namespace Scout.Plugin.Parcels.Tests;

public sealed class ParcelsPluginTests : IDisposable
{
    private static readonly TimeProvider Clock =
        new FixedTimeProvider(new DateTimeOffset(2026, 7, 3, 9, 30, 0, TimeSpan.Zero));

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "scout-parcels-tests", Guid.NewGuid().ToString("N"));

    // Fix the time zone to UTC so "watching since" dates are deterministic.
    private static ParcelsOptions Options() => new()
    {
        ApiKey = "test-key",
        TimeZone = "UTC",
    };

    private static IParcelsClient Client() => Substitute.For<IParcelsClient>();

    private IScoutLocalStore Store() =>
        new JsonFileScoutLocalStore(
            Microsoft.Extensions.Options.Options.Create(
                new ScoutLocalStoreOptions { RootDirectory = _dir }));

    private ParcelsPlugin Plugin(IParcelsClient client) => new(client, Store(), Clock, Options());

    private static ParcelTracking Tracking(string number, string status, string? eta, params TrackingEvent[] events)
        => new(number, status, eta, events);

    [Fact]
    public async Task TrackPackage_Formats_Status_Estimated_And_Events()
    {
        var client = Client();
        client.TrackAsync("LX123456789GB", Arg.Any<CancellationToken>())
            .Returns(Tracking(
                "LX123456789GB",
                "in_transit",
                "2026-07-10T00:00:00",
                new TrackingEvent("2026-07-05T10:00:00", "Departed facility", "London, GB"),
                new TrackingEvent("2026-07-04T18:00:00", "Info received", "GB")));

        var result = await Plugin(client).TrackPackageAsync("lx123456789gb", CancellationToken.None);

        result.ShouldContain("Tracking LX123456789GB: in transit.");
        result.ShouldContain("Estimated delivery: 2026-07-10.");
        result.ShouldContain("2026-07-05 Departed facility - London, GB");
        result.ShouldContain("2026-07-04 Info received - GB");
    }

    [Fact]
    public async Task TrackPackage_Uppercases_Number_Before_Query()
    {
        var client = Client();
        client.TrackAsync("LX123456789GB", Arg.Any<CancellationToken>())
            .Returns(Tracking("LX123456789GB", "delivered", null));

        await Plugin(client).TrackPackageAsync("lx123456789gb", CancellationToken.None);

        await client.Received(1).TrackAsync("LX123456789GB", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TrackPackage_No_Events_Reports_None()
    {
        var client = Client();
        client.TrackAsync("LX123456789GB", Arg.Any<CancellationToken>())
            .Returns(Tracking("LX123456789GB", "pending", null));

        var result = await Plugin(client).TrackPackageAsync("LX123456789GB", CancellationToken.None);

        result.ShouldContain("No detailed tracking events yet.");
    }

    [Fact]
    public async Task TrackPackage_Unknown_Returns_NotFound_Message()
    {
        var client = Client();
        client.TrackAsync("LX000000000GB", Arg.Any<CancellationToken>())
            .Returns((ParcelTracking?)null);

        var result = await Plugin(client).TrackPackageAsync("LX000000000GB", CancellationToken.None);

        result.ShouldBe("No tracking information found for LX000000000GB.");
    }

    [Fact]
    public async Task TrackPackage_Invalid_Number_Returns_Teaching()
    {
        var client = Client();

        var result = await Plugin(client).TrackPackageAsync("??", CancellationToken.None);

        result.ShouldContain("Provide a tracking number");
        await client.DidNotReceiveWithAnyArgs().TrackAsync(default!, default);
    }

    [Fact]
    public async Task TrackPackage_Empty_Number_Returns_Teaching()
    {
        var client = Client();

        var result = await Plugin(client).TrackPackageAsync("   ", CancellationToken.None);

        result.ShouldContain("Provide a tracking number");
        await client.DidNotReceiveWithAnyArgs().TrackAsync(default!, default);
    }

    [Fact]
    public async Task TrackPackage_ApiException_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.TrackAsync("LX123456789GB", Arg.Any<CancellationToken>())
            .Returns<ParcelTracking?>(_ => throw new ParcelsApiException(
                "The parcel tracking service is currently unavailable. Try again later."));

        var plugin = Plugin(client);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.TrackPackageAsync("LX123456789GB", CancellationToken.None));

        result.ShouldBe("The parcel tracking service is currently unavailable. Try again later.");
    }

    [Fact]
    public async Task TrackPackage_Timeout_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.TrackAsync("LX123456789GB", Arg.Any<CancellationToken>())
            .Returns<ParcelTracking?>(_ => throw new TaskCanceledException("timeout"));

        var plugin = Plugin(client);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.TrackPackageAsync("LX123456789GB", CancellationToken.None));

        result.ShouldBe("The parcel request timed out. Try again later.");
    }

    [Fact]
    public async Task TrackPackage_HttpRequestException_Returns_Clean_Error_Without_Throwing()
    {
        var client = Client();
        client.TrackAsync("LX123456789GB", Arg.Any<CancellationToken>())
            .Returns<ParcelTracking?>(_ => throw new HttpRequestException("connection reset"));

        var plugin = Plugin(client);

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.TrackPackageAsync("LX123456789GB", CancellationToken.None));

        result.ShouldBe("Could not reach the parcel tracking service. Try again later.");
    }

    [Fact]
    public async Task WatchPackage_Adds_And_Confirms_With_Label()
    {
        var result = await Plugin(Client()).WatchPackageAsync("lx123456789gb", "Nike order");

        result.ShouldBe("Now watching LX123456789GB (Nike order).");
    }

    [Fact]
    public async Task WatchPackage_Without_Label_Confirms_Plain()
    {
        var result = await Plugin(Client()).WatchPackageAsync("LX123456789GB");

        result.ShouldBe("Now watching LX123456789GB.");
    }

    [Fact]
    public async Task WatchPackage_Duplicate_Reports_Already_Watching()
    {
        var plugin = Plugin(Client());
        await plugin.WatchPackageAsync("LX123456789GB");

        var result = await plugin.WatchPackageAsync("lx123456789gb");

        result.ShouldBe("Already watching LX123456789GB.");
    }

    [Fact]
    public async Task WatchPackage_Invalid_Number_Returns_Teaching()
    {
        var result = await Plugin(Client()).WatchPackageAsync("!!");

        result.ShouldContain("Provide a tracking number");
    }

    [Fact]
    public async Task ListWatched_Empty_Returns_Message()
    {
        var result = await Plugin(Client()).ListWatchedPackagesAsync();

        result.ShouldBe("You are not watching any packages yet.");
    }

    [Fact]
    public async Task ListWatched_Lists_With_Label_And_Date()
    {
        var plugin = Plugin(Client());
        await plugin.WatchPackageAsync("LX123456789GB", "Nike order");
        await plugin.WatchPackageAsync("1Z999AA10123456784");

        var result = await plugin.ListWatchedPackagesAsync();
        var lines = result.Split('\n');

        lines.Length.ShouldBe(2);
        lines[0].ShouldBe("LX123456789GB [Nike order] - watching since 2026-07-03");
        lines[1].ShouldBe("1Z999AA10123456784 - watching since 2026-07-03");
    }

    [Fact]
    public async Task UnwatchPackage_Removes_And_Confirms()
    {
        var plugin = Plugin(Client());
        await plugin.WatchPackageAsync("LX123456789GB");

        var result = await plugin.UnwatchPackageAsync("lx123456789gb");

        result.ShouldBe("Stopped watching LX123456789GB.");
        (await plugin.ListWatchedPackagesAsync()).ShouldBe("You are not watching any packages yet.");
    }

    [Fact]
    public async Task UnwatchPackage_NotFound_Lists_Remaining()
    {
        var plugin = Plugin(Client());
        await plugin.WatchPackageAsync("LX123456789GB");

        var result = await plugin.UnwatchPackageAsync("1Z999AA10123456784");

        result.ShouldContain("You were not watching 1Z999AA10123456784");
        result.ShouldContain("LX123456789GB");
    }

    [Fact]
    public async Task UnwatchPackage_NotFound_Empty_Reports_Empty_List()
    {
        var result = await Plugin(Client()).UnwatchPackageAsync("LX123456789GB");

        result.ShouldBe("You were not watching LX123456789GB. Your watch list is empty.");
    }

    [Fact]
    public async Task WatchPackage_Store_Failure_Returns_Error_Line_Without_Throwing()
    {
        var store = Substitute.For<IScoutLocalStore>();
        store.MutateAsync(
                Arg.Any<string>(),
                Arg.Any<Action<WatchedPackagesDocument>>(),
                Arg.Any<CancellationToken>())
            .Returns<WatchedPackagesDocument>(_ => throw new IOException("disk gone"));

        var plugin = new ParcelsPlugin(Client(), store, Clock, Options());

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.WatchPackageAsync("LX123456789GB"));

        result.ShouldBe("Could not access the local watch list. Try again.");
    }

    [Fact]
    public async Task ListWatched_Store_Failure_Returns_Error_Line_Without_Throwing()
    {
        var store = Substitute.For<IScoutLocalStore>();
        store.ReadOrCreateAsync<WatchedPackagesDocument>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<WatchedPackagesDocument>(_ => throw new UnauthorizedAccessException("denied"));

        var plugin = new ParcelsPlugin(Client(), store, Clock, Options());

        string result = string.Empty;
        await Should.NotThrowAsync(async () =>
            result = await plugin.ListWatchedPackagesAsync());

        result.ShouldBe("Could not access the local watch list. Try again.");
    }

    [Fact]
    public void Ctor_Throws_When_Client_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new ParcelsPlugin(null!, Store(), Clock, Options()));
    }

    [Fact]
    public void Ctor_Throws_When_Store_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new ParcelsPlugin(Client(), null!, Clock, Options()));
    }

    [Fact]
    public void Ctor_Throws_When_Clock_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new ParcelsPlugin(Client(), Store(), null!, Options()));
    }

    [Fact]
    public void Ctor_Throws_When_Options_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new ParcelsPlugin(Client(), Store(), Clock, null!));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
