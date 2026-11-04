using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Scout.Operator.Tests.Fakes;

namespace Scout.Operator.Tests;

public sealed class OpenversePicturesTests : IDisposable
{
    private const string NoPictureCard = " The scene shows a text card; attach a background to the project in Scout Desktop, Production, or connect an AI Source with pictures.";

    private readonly string _media = Directory.CreateTempSubdirectory("scout-openverse-").FullName;
    private readonly OpenverseHttp _http = new();

    public void Dispose()
    {
        _http.Dispose();
        Directory.Delete(_media, recursive: true);
    }

    private OpenversePictures Pictures(TimeProvider? time = null, TimeSpan? search = null, TimeSpan? download = null) => new(_http.Factory(), time ?? Clocked(new SteppingTime(Given.Now)))
    {
        SearchDeadline = search ?? TimeSpan.FromSeconds(10),
        DownloadDeadline = download ?? TimeSpan.FromSeconds(60),
    };

    private TimeProvider Clocked(TimeProvider time)
    {
        _http.Clock = time;
        return time;
    }

    [Fact]
    public async Task The_search_asks_for_safe_cc0_by_and_public_domain_pictures_and_saves_the_first_fit_with_its_attribution()
    {
        var picture = await Pictures().FindAsync("mountain lake dawn", _media, "s3", CancellationToken.None);

        _http.Requests.Select(r => (r.Method.Method, r.RequestUri!.AbsoluteUri)).ShouldBe([
            ("GET", "https://api.openverse.org/v1/images/?q=mountain%20lake%20dawn&license=cc0,by,pdm&mature=false&page_size=5"),
            ("GET", OpenverseHttp.PictureUrl),
        ]);
        picture.ShouldBe(new StockPicture(
            "scene-s3.jpg", "\"Mountain lake\" by Thomas Nes Myhre is licensed under CC BY 2.0.", "Picture from Openverse, credited in the video's description."));
        _http.Requests[1].Headers.Accept.ToString().ShouldBe("image/*");
        File.ReadAllBytes(Path.Combine(_media, "scene-s3.jpg")).ShouldBe(OpenverseHttp.PictureBytes);
        Directory.GetFiles(_media).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Only_allowed_licences_are_taken()
    {
        _http.Search = _ => OpenverseHttp.Json(OpenverseHttp.Results(
            OpenverseHttp.Result("by-sa", "https://pictures.example.test/share-alike.jpg"),
            OpenverseHttp.Result("by-nc", "https://pictures.example.test/non-commercial.jpg"),
            OpenverseHttp.Result("by-nc-sa", "https://pictures.example.test/both.jpg"),
            OpenverseHttp.Result("pdm", "https://pictures.example.test/public-domain.png", filetype: "png")));

        var picture = await Pictures().FindAsync("mountain lake", _media, "s1", CancellationToken.None);

        picture.File.ShouldBe("scene-s1.png");
        _http.Requests[^1].RequestUri.ShouldBe(new Uri("https://pictures.example.test/public-domain.png"));
        _http.Requests.ShouldNotContain(r => r.RequestUri!.AbsolutePath.Contains("share-alike") || r.RequestUri!.AbsolutePath.Contains("non-commercial"));
    }

    [Fact]
    public async Task A_picture_narrower_than_800_pixels_without_a_width_or_of_another_type_is_skipped()
    {
        _http.Search = _ => OpenverseHttp.Json(OpenverseHttp.Results(
            OpenverseHttp.Result(url: "https://pictures.example.test/small.jpg", width: 799),
            OpenverseHttp.Result(url: "https://pictures.example.test/unknown.jpg", width: null),
            OpenverseHttp.Result(url: "https://pictures.example.test/animated.gif"),
            OpenverseHttp.Result(url: "https://pictures.example.test/vector", filetype: "svg"),
            OpenverseHttp.Result(url: "https://pictures.example.test/wide.webp", width: 800)));

        var picture = await Pictures().FindAsync("mountain lake", _media, "s1", CancellationToken.None);

        picture.File.ShouldBe("scene-s1.webp");
        _http.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Without_an_attribution_the_credit_is_built_from_the_title_the_creator_and_the_licence()
    {
        _http.Search = _ => OpenverseHttp.Json(OpenverseHttp.Results(
            OpenverseHttp.Result("cc0", attribution: null, title: "Lake <b>at</b> dawn", creator: "Ana")));

        var picture = await Pictures().FindAsync("mountain lake", _media, "s1", CancellationToken.None);

        picture.Credit.ShouldBe("Lake bat/b dawn by Ana, CC0 2.0, via Openverse");
    }

    [Fact]
    public async Task No_result_of_an_allowed_licence_gives_no_picture_with_a_note_and_no_download()
    {
        _http.Search = _ => OpenverseHttp.Json(OpenverseHttp.Results(OpenverseHttp.Result("by-sa")));

        var picture = await Pictures().FindAsync("mountain lake", _media, "s1", CancellationToken.None);

        picture.ShouldBe(new StockPicture(null, null, "No picture: Openverse has no CC0, public domain or CC BY picture for this scene." + NoPictureCard));
        _http.Requests.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("search timeout", "No picture: Scout could not reach Openverse.")]
    [InlineData("search 503", "No picture: Scout could not reach Openverse.")]
    [InlineData("search reset", "No picture: Scout could not reach Openverse.")]
    [InlineData("search 429", "No picture: Openverse is limiting Scout's requests for now.")]
    [InlineData("search not json", "No picture: Scout could not reach Openverse.")]
    [InlineData("empty result", "No picture: Openverse has no CC0, public domain or CC BY picture for this scene.")]
    [InlineData("picture timeout", "No picture: Scout could not download the picture Openverse named as an image of at most 20 MB.")]
    [InlineData("picture 503", "No picture: Scout could not download the picture Openverse named as an image of at most 20 MB.")]
    [InlineData("picture reset", "No picture: Scout could not download the picture Openverse named as an image of at most 20 MB.")]
    [InlineData("picture redirected to a web page", "No picture: Scout could not download the picture Openverse named as an image of at most 20 MB.")]
    [InlineData("picture over 20 MB", "No picture: Scout could not download the picture Openverse named as an image of at most 20 MB.")]
    [InlineData("picture 302", "No picture: Scout could not download the picture Openverse named as an image of at most 20 MB.")]
    [InlineData("picture gif", "No picture: Scout could not download the picture Openverse named as an image of at most 20 MB.")]
    [InlineData("picture svg", "No picture: Scout could not download the picture Openverse named as an image of at most 20 MB.")]
    public async Task A_timeout_a_5xx_a_429_and_a_non_image_body_give_no_picture_with_a_note(string failure, string note)
    {
        Func<HttpRequestMessage, HttpResponseMessage> fails = failure.Split(' ', 2)[1] switch
        {
            "timeout" => _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing."),
            "503" => _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            "reset" => _ => throw new HttpRequestException("Connection reset by peer", new IOException("reset")),
            "429" => _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            "not json" => _ => OpenverseHttp.Json("<html>maintenance</html>"),
            "result" => _ => OpenverseHttp.Json(OpenverseHttp.Results()),
            "redirected to a web page" => _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>" + new string('x', 4096) + "</html>", System.Text.Encoding.UTF8, "text/html") },
            "over 20 MB" => _ => Oversized(),
            "302" => _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://elsewhere.example.test/lake.jpg") } },
            "gif" => _ => OpenverseHttp.Image("image/gif"),
            "svg" => _ => OpenverseHttp.Image("image/svg+xml"),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        if (failure.StartsWith("picture", StringComparison.Ordinal))
        {
            _http.Picture = fails;
        }
        else
        {
            _http.Search = fails;
        }

        var picture = await Pictures().FindAsync("mountain lake", _media, "s1", CancellationToken.None);

        picture.ShouldBe(new StockPicture(null, null, note + NoPictureCard));
        Directory.GetFiles(_media).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_picture_under_1_KB_gives_no_picture_with_the_media_sentence_in_the_note()
    {
        _http.Picture = _ => Tiny();

        var picture = await Pictures().FindAsync("mountain lake", _media, "s1", CancellationToken.None);

        picture.ShouldBe(new StockPicture(null, null,
            "No picture: Scout could not download the picture Openverse named as an image of at most 20 MB. (the file came out empty)" + NoPictureCard));
        Directory.GetFiles(_media).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_media_folder_that_cannot_be_written_puts_the_media_sentence_in_the_note()
    {
        var picture = await Pictures().FindAsync("mountain lake", "relative/media", "s1", CancellationToken.None);

        picture.ShouldBe(new StockPicture(null, null, "No picture: " + MediaFiles.Outside + NoPictureCard));
    }

    [Fact]
    public async Task A_stalled_search_body_gives_the_unreachable_note_and_no_file()
    {
        _http.Search = _ => Stalled();

        var picture = await Pictures(search: TimeSpan.FromMilliseconds(200)).FindAsync("mountain lake", _media, "s1", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        picture.ShouldBe(new StockPicture(null, null, "No picture: Scout could not reach Openverse." + NoPictureCard));
        Directory.GetFiles(_media).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_stalled_picture_body_gives_the_not_downloaded_note_and_no_file()
    {
        _http.Picture = _ => Stalled("image/jpeg");

        var picture = await Pictures(download: TimeSpan.FromMilliseconds(200)).FindAsync("mountain lake", _media, "s1", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        picture.ShouldBe(new StockPicture(null, null, OpenversePictures.NotDownloaded));
        Directory.GetFiles(_media).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_result_that_is_not_https_is_skipped_for_the_next_https_one()
    {
        _http.Search = _ => OpenverseHttp.Json(OpenverseHttp.Results(
            OpenverseHttp.Result(url: "http://pictures.example.test/plain.jpg"),
            OpenverseHttp.Result(url: "https://pictures.example.test/secure.jpg")));

        var picture = await Pictures().FindAsync("mountain lake", _media, "s1", CancellationToken.None);

        picture.File.ShouldBe("scene-s1.jpg");
        _http.Requests[^1].RequestUri.ShouldBe(new Uri("https://pictures.example.test/secure.jpg"));
        _http.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_body_past_20_MB_without_a_length_is_refused_while_it_streams_and_leaves_no_file()
    {
        _http.Picture = _ =>
        {
            var content = new StreamContent(new UnsizedStream((20L * 1024 * 1024) + 1));
            content.Headers.ContentType = new("image/jpeg");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };

        var picture = await Pictures().FindAsync("mountain lake", _media, "s1", CancellationToken.None);

        picture.Note.ShouldBe("No picture: Scout could not download the picture Openverse named as an image of at most 20 MB." + NoPictureCard);
        Directory.GetFiles(_media).ShouldBeEmpty();
    }

    [Fact]
    public async Task Search_terms_past_200_characters_are_cut_without_splitting_a_character()
    {
        var terms = new string('a', 199) + "\U0001F35E bread";

        var picture = await Pictures().FindAsync(terms, _media, "s1", CancellationToken.None);

        picture.File.ShouldBe("scene-s1.jpg");
        _http.Requests[0].RequestUri!.Query.ShouldStartWith($"?q={new string('a', 199)}&");
    }

    [Fact]
    public async Task A_cancellation_is_not_turned_into_a_note()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        _http.Search = _ => throw new TaskCanceledException();

        await Should.ThrowAsync<OperationCanceledException>(() => Pictures().FindAsync("mountain lake", _media, "s1", cancel.Token));
    }

    [Fact]
    public async Task The_second_picture_starts_its_search_at_least_3_point_1_seconds_after_the_first_request()
    {
        var pictures = Pictures();

        await pictures.FindAsync("mountain lake", _media, "s1", CancellationToken.None);
        await pictures.FindAsync("mountain lake", _media, "s2", CancellationToken.None);

        _http.Requests[2].RequestUri!.Host.ShouldBe("api.openverse.org");
        (_http.Starts[2] - _http.Starts[0]).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(3.1));
    }

    [Fact]
    public async Task A_download_starts_at_once_after_its_search()
    {
        await Pictures().FindAsync("mountain lake", _media, "s1", CancellationToken.None);

        _http.Requests.Count.ShouldBe(2);
        _http.Starts[1].ShouldBe(_http.Starts[0]);
    }

    [Fact]
    public async Task A_429_with_Retry_After_7_waits_7_seconds_then_retries_once_and_succeeds()
    {
        var answers = 0;
        _http.Search = _ => ++answers == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromSeconds(7)) } }
            : OpenverseHttp.Json(OpenverseHttp.Results(OpenverseHttp.Result()));

        var picture = await Pictures().FindAsync("mountain lake", _media, "s1", CancellationToken.None);

        picture.File.ShouldBe("scene-s1.jpg");
        _http.Requests.Count.ShouldBe(3);
        (_http.Starts[1] - _http.Starts[0]).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task A_429_that_keeps_coming_with_Retry_After_is_retried_once_only()
    {
        _http.Search = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromSeconds(500)) } };

        var picture = await Pictures().FindAsync("mountain lake", _media, "s1", CancellationToken.None);

        picture.ShouldBe(new StockPicture(null, null, "No picture: Openverse is limiting Scout's requests for now." + NoPictureCard));
        _http.Requests.Count.ShouldBe(2);
        (_http.Starts[1] - _http.Starts[0]).ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task The_daily_budget_stops_requests_with_the_note()
    {
        var time = new SteppingTime(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.FromHours(-8)));
        var pictures = Pictures(time);
        for (var i = 1; i <= 100; i++)
        {
            (await pictures.FindAsync("mountain lake", _media, $"a{i}", CancellationToken.None)).File.ShouldBe($"scene-a{i}.jpg");
        }

        var spent = await pictures.FindAsync("mountain lake", _media, "b1", CancellationToken.None);
        var requestsWhenSpent = _http.Requests.Count;
        time.Advance(TimeSpan.FromHours(12));
        var nextDay = await pictures.FindAsync("mountain lake", _media, "c1", CancellationToken.None);

        requestsWhenSpent.ShouldBe(200);
        spent.ShouldBe(new StockPicture(null, null, "The Openverse picture budget for today is spent; it resets at midnight Pacific time."));
        File.Exists(Path.Combine(_media, "scene-b1.jpg")).ShouldBeFalse();
        nextDay.File.ShouldBe("scene-c1.jpg");
    }

    [Fact]
    public void The_openverse_client_waits_10_seconds_and_names_Scout_and_its_repository()
    {
        using var provider = new ServiceCollection().AddYouTubeOperator().BuildServiceProvider();

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("openverse");

        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("openverse");
        while (handler is DelegatingHandler outer)
        {
            handler = outer.InnerHandler!;
        }

        ((SocketsHttpHandler)handler).AllowAutoRedirect.ShouldBeFalse();
        client.Timeout.ShouldBe(TimeSpan.FromSeconds(10));
        client.DefaultRequestHeaders.UserAgent.ToString().ShouldBe("Scout (+https://github.com/vitorafgomes/scout)");
    }

    private static HttpResponseMessage Stalled(string mediaType = "application/json")
    {
        var content = new StreamContent(new StalledStream());
        content.Headers.ContentType = new(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage Oversized()
    {
        var response = OpenverseHttp.Image();
        response.Content.Headers.ContentLength = (20L * 1024 * 1024) + 1;
        return response;
    }

    private static HttpResponseMessage Tiny()
    {
        var content = new ByteArrayContent([0xFF, 0xD8, 0xFF]);
        content.Headers.ContentType = new("image/jpeg");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class UnsizedStream(long length) : Stream
    {
        private long _left = length;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, _left);
            _left -= read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
