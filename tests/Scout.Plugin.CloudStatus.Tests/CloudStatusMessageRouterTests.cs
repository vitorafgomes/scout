using Scout.Core.Chat;
using Scout.Plugin.CloudStatus.Domain;
using Scout.Plugin.CloudStatus.Providers;

namespace Scout.Plugin.CloudStatus.Tests;

public class CloudStatusMessageRouterTests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 4, 21, 10, 0, 0, TimeSpan.Zero);

    private static CloudStatusFunctions WithOneProvider(HealthLevel level = HealthLevel.Healthy)
    {
        var provider = new InMemoryCloudHealthProvider(new StatusReport(
            "Azure", "sub-1", level, Array.Empty<ServiceStatus>(), FixedTime));
        return new CloudStatusFunctions([provider]);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("is prod healthy?")]
    [InlineData("anything down?")]
    [InlineData("cloud outage")]
    [InlineData("STATUS")]
    public async Task Matches_Expected_Keywords(string text)
    {
        var router = new CloudStatusMessageRouter(WithOneProvider());

        var response = await router.RouteAsync(
            new ChatMessage("u1", "c1", text),
            CancellationToken.None);

        response.Should().NotBeNull();
        response!.Text.Should().Contain("Azure");
    }

    [Theory]
    [InlineData("good morning team")]
    [InlineData("substatus")]    // must not match as substring
    [InlineData("distatus")]     // must not match as substring
    public async Task Returns_Null_For_Non_Matching_Text(string text)
    {
        var router = new CloudStatusMessageRouter(WithOneProvider());

        var response = await router.RouteAsync(
            new ChatMessage("u1", "c1", text),
            CancellationToken.None);

        response.Should().BeNull();
    }

    [Fact]
    public async Task Uses_Formatter_Output_For_ChatResponse_Text()
    {
        var router = new CloudStatusMessageRouter(WithOneProvider(HealthLevel.Degraded));

        var response = await router.RouteAsync(
            new ChatMessage("u1", "c1", "status"),
            CancellationToken.None);

        response!.Text.Should().Contain("Cloud health:");
        response.Text.Should().Contain("- Azure (sub-1): Degraded");
    }

    [Fact]
    public async Task Null_Message_Throws()
    {
        var router = new CloudStatusMessageRouter(WithOneProvider());

        Func<Task> act = () => router.RouteAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
