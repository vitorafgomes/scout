using System.Net;
using Amazon.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.Plugin.CloudStatus.Domain;
using Scout.Plugin.CloudStatus.Providers;
using Scout.Plugin.CloudStatus.Providers.Aws;

namespace Scout.Plugin.CloudStatus.Tests.Providers;

public class AwsCloudHealthProviderTests
{
    private const string Region = "eu-west-1";
    private static readonly DateTimeOffset FixedTime =
        new(2026, 4, 21, 10, 0, 0, TimeSpan.Zero);

    private static AwsCloudHealthProvider Sut(IAwsHealthSource source) =>
        new(
            Options.Create(new AwsCloudHealthOptions { Region = Region }),
            source,
            NullLogger<AwsCloudHealthProvider>.Instance,
            new FixedTimeProvider(FixedTime));

    [Fact]
    public async Task Returns_Healthy_When_No_Open_Events()
    {
        var report = await Sut(new StubHealthSource(Array.Empty<AwsHealthEvent>()))
            .GetHealthAsync(CancellationToken.None);

        report.Provider.Should().Be("AWS");
        report.Scope.Should().Be(Region);
        report.Overall.Should().Be(HealthLevel.Healthy);
        report.Services.Should().BeEmpty();
        report.CheckedAt.Should().Be(FixedTime);
    }

    [Fact]
    public async Task Issue_Category_Maps_To_Unhealthy()
    {
        var source = new StubHealthSource([
            new AwsHealthEvent(
                "arn:aws:health:us-east-1::event/EC2/AWS_EC2_OPERATIONAL_ISSUE",
                "EC2", "us-east-1",
                Category: "issue", StatusCode: "open",
                Description: "EC2 API errors in us-east-1"),
        ]);

        var report = await Sut(source).GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(HealthLevel.Unhealthy);
        report.Services.Should().ContainSingle()
              .Which.Service.Should().Be("EC2");
    }

    [Fact]
    public async Task AccountNotification_Only_Maps_To_Degraded()
    {
        var source = new StubHealthSource([
            new AwsHealthEvent(
                "arn:aws:health:::event/IAM/AWS_IAM_POLICY_CHANGE",
                "IAM", "global",
                Category: "accountNotification", StatusCode: "open",
                Description: "Policy update scheduled"),
        ]);

        var report = await Sut(source).GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(HealthLevel.Degraded);
    }

    [Fact]
    public async Task Unknown_Category_Treated_As_Degraded()
    {
        var source = new StubHealthSource([
            new AwsHealthEvent(
                "arn",
                "EC2", "us-east-1",
                Category: "someFutureCategory", StatusCode: "open",
                Description: "???"),
        ]);

        var report = await Sut(source).GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(HealthLevel.Degraded);
    }

    [Fact]
    public async Task SubscriptionRequired_Maps_To_Unknown_With_Hint()
    {
        var awsEx = new AmazonServiceException(
            "An error occurred",
            new Exception(),
            ErrorType.Unknown,
            errorCode: "SubscriptionRequiredException",
            requestId: null,
            statusCode: HttpStatusCode.BadRequest);

        var report = await Sut(new ThrowingHealthSource(awsEx))
            .GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(HealthLevel.Unknown);
        report.Message.Should().Contain("Business or Enterprise Support");
    }

    [Fact]
    public async Task Other_AmazonServiceException_Maps_To_Unknown_With_ErrorCode()
    {
        var awsEx = new AmazonServiceException(
            "throttled",
            new Exception(),
            ErrorType.Unknown,
            errorCode: "ThrottlingException",
            requestId: "req-1",
            statusCode: HttpStatusCode.TooManyRequests);

        var report = await Sut(new ThrowingHealthSource(awsEx))
            .GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(HealthLevel.Unknown);
        report.Message.Should().Contain("ThrottlingException");
        report.Message.Should().Contain("429");
    }

    [Fact]
    public async Task Unexpected_Exception_Maps_To_Unknown()
    {
        var report = await Sut(new ThrowingHealthSource(new InvalidOperationException("boom")))
            .GetHealthAsync(CancellationToken.None);

        report.Overall.Should().Be(HealthLevel.Unknown);
        report.Message.Should().Be("boom");
    }

    [Fact]
    public async Task Propagates_Cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => Sut(new CancellableHealthSource())
            .GetHealthAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Ctor_Throws_When_Region_Empty()
    {
        Action create = () => new AwsCloudHealthProvider(
            Options.Create(new AwsCloudHealthOptions { Region = "" }),
            new StubHealthSource(Array.Empty<AwsHealthEvent>()),
            NullLogger<AwsCloudHealthProvider>.Instance);

        create.Should().Throw<ArgumentException>()
            .WithMessage("*Region*");
    }

    [Fact]
    public void Ctor_Throws_When_Source_Null()
    {
        Action create = () => new AwsCloudHealthProvider(
            Options.Create(new AwsCloudHealthOptions { Region = Region }),
            source: null!,
            NullLogger<AwsCloudHealthProvider>.Instance);

        create.Should().Throw<ArgumentNullException>();
    }

    private sealed class StubHealthSource(IReadOnlyList<AwsHealthEvent> events) : IAwsHealthSource
    {
        public Task<IReadOnlyList<AwsHealthEvent>> GetOpenEventsAsync(CancellationToken ct) =>
            Task.FromResult(events);
    }

    private sealed class ThrowingHealthSource(Exception exception) : IAwsHealthSource
    {
        public Task<IReadOnlyList<AwsHealthEvent>> GetOpenEventsAsync(CancellationToken ct) =>
            throw exception;
    }

    private sealed class CancellableHealthSource : IAwsHealthSource
    {
        public Task<IReadOnlyList<AwsHealthEvent>> GetOpenEventsAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<AwsHealthEvent>>(Array.Empty<AwsHealthEvent>());
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
