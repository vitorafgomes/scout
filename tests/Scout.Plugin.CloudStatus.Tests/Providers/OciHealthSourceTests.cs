using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Scout.Plugin.CloudStatus.Providers.Oci;

namespace Scout.Plugin.CloudStatus.Tests.Providers;

public class OciHealthSourceTests
{
    private const string Endpoint = "https://ocistatus.oraclecloud.com/api/v2/summary.json";

    [Fact]
    public async Task Returns_Empty_When_Payload_Has_No_Incidents()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"incidents":[]}""");
        var sut = BuildSut(handler);

        var incidents = await sut.GetActiveIncidentsAsync(CancellationToken.None);

        incidents.Should().BeEmpty();
    }

    [Fact]
    public async Task Parses_Active_Incident()
    {
        const string json = """
        {
          "incidents": [
            {
              "id": "inc-1",
              "name": "Object Storage latency",
              "status": "investigating",
              "impact": "major",
              "service": "Object Storage",
              "region": "eu-london-1"
            }
          ]
        }
        """;
        var sut = BuildSut(new StubHandler(HttpStatusCode.OK, json));

        var incidents = await sut.GetActiveIncidentsAsync(CancellationToken.None);

        incidents.Should().ContainSingle();
        incidents[0].Id.Should().Be("inc-1");
        incidents[0].Service.Should().Be("Object Storage");
        incidents[0].Region.Should().Be("eu-london-1");
        incidents[0].Impact.Should().Be("major");
        incidents[0].Title.Should().Be("Object Storage latency");
    }

    [Fact]
    public async Task Skips_Resolved_Incidents()
    {
        const string json = """
        {
          "incidents": [
            {"id":"a","status":"resolved","impact":"major","service":"Compute","region":"us-phoenix-1","name":"Fixed"},
            {"id":"b","status":"postmortem","impact":"critical","service":"Compute","region":"us-phoenix-1","name":"Done"},
            {"id":"c","status":"monitoring","impact":"minor","service":"Networking","region":"eu-london-1","name":"Active"}
          ]
        }
        """;
        var sut = BuildSut(new StubHandler(HttpStatusCode.OK, json));

        var incidents = await sut.GetActiveIncidentsAsync(CancellationToken.None);

        incidents.Should().ContainSingle()
                 .Which.Id.Should().Be("c");
    }

    [Fact]
    public async Task Filters_By_Configured_Regions()
    {
        const string json = """
        {
          "incidents": [
            {"id":"a","status":"investigating","impact":"major","service":"Compute","region":"us-phoenix-1","name":"US"},
            {"id":"b","status":"investigating","impact":"major","service":"Compute","region":"eu-london-1","name":"EU"}
          ]
        }
        """;
        var sut = BuildSut(
            new StubHandler(HttpStatusCode.OK, json),
            new OciCloudHealthOptions { Regions = new List<string> { "eu-london-1" } });

        var incidents = await sut.GetActiveIncidentsAsync(CancellationToken.None);

        incidents.Should().ContainSingle()
                 .Which.Region.Should().Be("eu-london-1");
    }

    [Fact]
    public async Task Propagates_Http_Failure_So_Provider_Can_Map_It()
    {
        var sut = BuildSut(new StubHandler(HttpStatusCode.InternalServerError, "boom"));

        Func<Task> act = () => sut.GetActiveIncidentsAsync(CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Propagates_Cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var sut = BuildSut(new StubHandler(HttpStatusCode.OK, """{"incidents":[]}"""));

        Func<Task> act = () => sut.GetActiveIncidentsAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Malformed_Json_Throws_JsonException()
    {
        var sut = BuildSut(new StubHandler(HttpStatusCode.OK, "{not-json"));

        Func<Task> act = () => sut.GetActiveIncidentsAsync(CancellationToken.None);

        await act.Should().ThrowAsync<System.Text.Json.JsonException>();
    }

    private static OciHealthSource BuildSut(
        HttpMessageHandler handler,
        OciCloudHealthOptions? options = null)
    {
        options ??= new OciCloudHealthOptions { StatusEndpoint = Endpoint };
        if (string.IsNullOrWhiteSpace(options.StatusEndpoint))
        {
            options.StatusEndpoint = Endpoint;
        }
        var http = new HttpClient(handler);
        return new OciHealthSource(http, Options.Create(options));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
