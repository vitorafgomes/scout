using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Scout.Host.Worker.Providers;
using Scout.Host.Worker.Tests.Helpers;
using Shouldly;

namespace Scout.Host.Worker.Tests;

public sealed class OllamaWarmupSuppressingLoggingHandlerTests
{
    [Fact]
    public async Task Handler_DuringWarmup_OnConnectionRefused_LogsAtDebug_AndRethrows()
    {
        var probe = FakeReadinessProbes.NeverReady();
        var logger = new RecordingLogger();
        var inner = new ThrowingHandler(new HttpRequestException(
            "Connection refused",
            new SocketException((int)SocketError.ConnectionRefused)));

        var sut = new OllamaWarmupSuppressingLoggingHandler(probe, logger) { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(sut);
        using var req = new HttpRequestMessage(HttpMethod.Get, "http://localhost:11434/api/tags");

        await Should.ThrowAsync<HttpRequestException>(
            async () => await invoker.SendAsync(req, CancellationToken.None));

        logger.Entries.Count(e => e.Level == LogLevel.Debug).ShouldBe(1);
        logger.Entries.Count(e => e.Level >= LogLevel.Warning).ShouldBe(0);
    }

    [Fact]
    public async Task Handler_AfterReady_OnConnectionRefused_LogsAtWarning_AndRethrows()
    {
        var probe = FakeReadinessProbes.AlwaysReady();
        var logger = new RecordingLogger();
        var inner = new ThrowingHandler(new HttpRequestException(
            "Connection refused",
            new SocketException((int)SocketError.ConnectionRefused)));

        var sut = new OllamaWarmupSuppressingLoggingHandler(probe, logger) { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(sut);
        using var req = new HttpRequestMessage(HttpMethod.Get, "http://localhost:11434/api/tags");

        await Should.ThrowAsync<HttpRequestException>(
            async () => await invoker.SendAsync(req, CancellationToken.None));

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        warnings.Count.ShouldBe(1);
        warnings[0].Exception.ShouldNotBeNull();
    }

    [Fact]
    public async Task Handler_OnSuccess_LogsNothing()
    {
        var probe = FakeReadinessProbes.AlwaysReady();
        var logger = new RecordingLogger();
        var inner = new ConstantResponseHandler(HttpStatusCode.OK);

        var sut = new OllamaWarmupSuppressingLoggingHandler(probe, logger) { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(sut);
        using var req = new HttpRequestMessage(HttpMethod.Get, "http://localhost:11434/api/tags");

        using var response = await invoker.SendAsync(req, CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        logger.Entries.ShouldBeEmpty();
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _ex;

        public ThrowingHandler(Exception ex) => _ex = ex;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => throw _ex;
    }

    private sealed class ConstantResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public ConstantResponseHandler(HttpStatusCode status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status));
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class RecordingLogger : ILogger<OllamaWarmupSuppressingLoggingHandler>
    {
        public List<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }
}
