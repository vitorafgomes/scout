using System.Net;
using Shouldly;

namespace Scout.Host.Worker.Tests;

public sealed class OpenApiEndpointsTests : IClassFixture<ScoutWorkerWebApplicationFactory>
{
    private readonly ScoutWorkerWebApplicationFactory _factory;

    public OpenApiEndpointsTests(ScoutWorkerWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task OpenApiDocument_IsServed()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldNotBeNullOrWhiteSpace();
        body.ShouldContain("\"openapi\"");
    }

    [Fact]
    public async Task ScalarUi_IsServed()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/scalar/v1");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldNotBeNullOrWhiteSpace();
        body.ShouldContain("<", Case.Sensitive);
    }
}
