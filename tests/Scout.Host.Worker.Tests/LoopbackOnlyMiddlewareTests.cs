using System.Net;
using Shouldly;

namespace Scout.Host.Worker.Tests;

public sealed class LoopbackOnlyMiddlewareTests
{
    [Fact]
    public async Task LoopbackOnly_RejectsNonLoopback_Returns403()
    {
        using var factory = new ScoutWorkerWebApplicationFactory
        {
            ForceNonLoopbackRemoteIp = true,
        };
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/providers");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("providers/forbidden-origin");
    }
}
