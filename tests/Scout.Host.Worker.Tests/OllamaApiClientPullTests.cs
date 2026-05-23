using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.Host.Worker.Providers;
using Shouldly;

namespace Scout.Host.Worker.Tests;

public sealed class OllamaApiClientPullTests
{
    private static OllamaApiClient BuildClient(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:11434"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var options = Options.Create(new OllamaProviderOptions
        {
            Endpoint = "http://localhost:11434",
            PingTimeout = TimeSpan.FromSeconds(2),
        });
        return new OllamaApiClient(http, options, NullLogger<OllamaApiClient>.Instance);
    }

    [Fact]
    public async Task PullAsync_YieldsAllProgressLines()
    {
        const string body =
            "{\"status\":\"pulling manifest\"}\n" +
            "{\"status\":\"downloading\",\"digest\":\"sha256:abc\",\"total\":1000,\"completed\":250}\n" +
            "{\"status\":\"downloading\",\"digest\":\"sha256:abc\",\"total\":1000,\"completed\":1000}\n" +
            "{\"status\":\"success\"}\n";

        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson"),
        });
        var client = BuildClient(handler);

        var collected = new List<PullProgress>();
        await foreach (var ev in client.PullAsync("qwen2.5:3b", CancellationToken.None))
        {
            collected.Add(ev);
        }

        collected.Count.ShouldBe(4);
        collected[0].Status.ShouldBe("pulling manifest");
        collected[1].Status.ShouldBe("downloading");
        collected[1].Digest.ShouldBe("sha256:abc");
        collected[1].Total.ShouldBe(1000);
        collected[1].Completed.ShouldBe(250);
        collected[3].Status.ShouldBe("success");
    }

    [Fact]
    public async Task PullAsync_SkipsMalformedJsonLines()
    {
        const string body =
            "{\"status\":\"pulling manifest\"}\n" +
            "{ not valid json\n" +
            "\n" +
            "{\"status\":\"success\"}\n";

        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson"),
        });
        var client = BuildClient(handler);

        var collected = new List<PullProgress>();
        await foreach (var ev in client.PullAsync("foo", CancellationToken.None))
        {
            collected.Add(ev);
        }

        collected.Count.ShouldBe(2);
        collected[0].Status.ShouldBe("pulling manifest");
        collected[1].Status.ShouldBe("success");
    }

    [Fact]
    public async Task PullAsync_HttpError_Throws()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("model not found"),
        });
        var client = BuildClient(handler);

        var ex = await Should.ThrowAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in client.PullAsync("missing", CancellationToken.None))
            {
            }
        });

        ex.Message.ShouldContain("404");
    }

    [Fact]
    public async Task PullAsync_RespectsCancellation()
    {
        // Handler returns a content stream whose ReadAsync blocks on the
        // external CTS. HttpClient cancels its internal request CT once the
        // response headers are returned, so we cannot rely on the handler's
        // SendAsync token to observe caller cancellation.
        using var cts = new CancellationTokenSource();
        var handler = new BlockingStreamHandler(cts.Token);
        var client = BuildClient(handler);

        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.PullAsync("foo", cts.Token))
            {
            }
        });
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) => _factory = factory;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_factory(request));
    }

    private sealed class BlockingStreamHandler : HttpMessageHandler
    {
        private readonly CancellationToken _externalCt;

        public BlockingStreamHandler(CancellationToken externalCt) => _externalCt = externalCt;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new BlockingStreamContent(_externalCt),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class BlockingStreamContent : HttpContent
    {
        private readonly CancellationToken _externalCt;

        public BlockingStreamContent(CancellationToken externalCt) => _externalCt = externalCt;

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => Task.CompletedTask;

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new BlockingStream(_externalCt));

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    // Stream that blocks ReadAsync until the external CTS is cancelled, then
    // throws OperationCanceledException. Mirrors a real connection that never
    // emits bytes after headers.
    private sealed class BlockingStream : Stream
    {
        private readonly CancellationToken _externalCt;

        public BlockingStream(CancellationToken externalCt) => _externalCt = externalCt;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_externalCt, cancellationToken);
            await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
