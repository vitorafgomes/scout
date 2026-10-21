using System.Net;
using System.Net.Http.Headers;
using Scout.Plugin.Parcels;
using Scout.Plugin.Parcels.Tests.Fakes;

namespace Scout.Plugin.Parcels.Tests;

public sealed class ParcelsClientTests
{
    private const string SampleTracking =
        """
        {
          "data": {
            "trackings": [
              {
                "tracker": { "trackingNumber": "LX123456789GB" },
                "shipment": {
                  "statusMilestone": "in_transit",
                  "delivery": { "estimatedDeliveryDate": "2026-07-10T00:00:00" }
                },
                "events": [
                  { "occurrenceDatetime": "2026-07-05T10:00:00", "status": "Departed facility", "location": "London, GB" },
                  { "occurrenceDatetime": "2026-07-04T18:00:00", "status": "Info received", "location": "GB" }
                ]
              }
            ]
          }
        }
        """;

    private static (ParcelsClient client, StubHttpMessageHandler handler) Build()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.ship24.com/"),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Scout");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");
        return (new ParcelsClient(http), handler);
    }

    [Fact]
    public async Task Track_Posts_Expected_Method_Path_Headers_And_Body()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, SampleTracking);

        await client.TrackAsync("LX123456789GB", CancellationToken.None);

        var req = handler.LastRequest.ShouldNotBeNull();
        req.Method.ShouldBe(HttpMethod.Post);
        req.RequestUri!.ToString().ShouldContain("public/v1/tracking/search");
        req.Headers.UserAgent.ToString().ShouldContain("Scout");
        req.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        handler.LastRequestBody.ShouldNotBeNull();
        handler.LastRequestBody!.ShouldContain("trackingNumber");
        handler.LastRequestBody!.ShouldContain("LX123456789GB");
    }

    [Fact]
    public async Task Track_Parses_Status_Estimated_And_Events()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, SampleTracking);

        var tracking = await client.TrackAsync("LX123456789GB", CancellationToken.None);

        tracking.ShouldNotBeNull();
        tracking!.TrackingNumber.ShouldBe("LX123456789GB");
        tracking.Status.ShouldBe("in_transit");
        tracking.EstimatedDelivery.ShouldBe("2026-07-10T00:00:00");
        tracking.Events.Count.ShouldBe(2);
        tracking.Events[0].Status.ShouldBe("Departed facility");
        tracking.Events[0].Location.ShouldBe("London, GB");
    }

    [Fact]
    public async Task Track_Empty_Trackings_Returns_Null()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """{"data":{"trackings":[]}}""");

        var tracking = await client.TrackAsync("LX123456789GB", CancellationToken.None);

        tracking.ShouldBeNull();
    }

    [Fact]
    public async Task Track_NotFound_Returns_Null()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.NotFound, "{}");

        var tracking = await client.TrackAsync("UNKNOWN123", CancellationToken.None);

        tracking.ShouldBeNull();
    }

    [Fact]
    public async Task Track_Unauthorized_Throws_Auth_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.Unauthorized, "{}");

        var ex = await Should.ThrowAsync<ParcelsApiException>(
            () => client.TrackAsync("LX123456789GB", CancellationToken.None));

        ex.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        ex.Message.ShouldContain("authentication failed");
    }

    [Fact]
    public async Task Track_RateLimited_Throws_RateLimit_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.TooManyRequests, "{}");

        var ex = await Should.ThrowAsync<ParcelsApiException>(
            () => client.TrackAsync("LX123456789GB", CancellationToken.None));

        ex.Message.ShouldContain("rate limiting");
    }

    [Fact]
    public async Task Track_ServerError_Throws_Unavailable_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.InternalServerError, "{}");

        var ex = await Should.ThrowAsync<ParcelsApiException>(
            () => client.TrackAsync("LX123456789GB", CancellationToken.None));

        ex.Message.ShouldContain("unavailable");
    }

    [Fact]
    public async Task Track_Malformed_Body_Throws_Unreadable_Message()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, "{ this is not valid json");

        var ex = await Should.ThrowAsync<ParcelsApiException>(
            () => client.TrackAsync("LX123456789GB", CancellationToken.None));

        ex.Message.ShouldContain("unreadable");
    }

    [Fact]
    public async Task Track_Timeout_Propagates_As_OperationCanceled()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<TaskCanceledException>(
            () => client.TrackAsync("LX123456789GB", CancellationToken.None));
    }

    [Fact]
    public async Task Track_Transport_Failure_Propagates_As_HttpRequestException()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new HttpRequestException("connection reset"));

        await Should.ThrowAsync<HttpRequestException>(
            () => client.TrackAsync("LX123456789GB", CancellationToken.None));
    }
}
