using System.Net;
using System.Text.Json;
using Scout.Plugin.YouTube.Abstractions;
using Scout.Plugin.YouTube.Tests.Fakes;

namespace Scout.Plugin.YouTube.Tests;

public sealed class YouTubeWriteClientTests : IDisposable
{
    private const string Session = "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&upload_id=session-1";
    private const string Session2 = "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&upload_id=session-2";
    private const string Created = """{"kind":"youtube#video","id":"newVideo001","status":{"uploadStatus":"uploaded","privacyStatus":"private"}}""";

    private static readonly byte[] Video = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

    private readonly string _directory = Directory.CreateTempSubdirectory("scout-upload-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static (YouTubeWriteClient Client, StubHttpMessageHandler Handler) Build()
    {
        var handler = new StubHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://www.googleapis.com/") };
        return (new YouTubeWriteClient(http, TimeProvider.System) { Backoff = TimeSpan.Zero }, handler);
    }

    private string File(string name, byte[] bytes)
    {
        var path = Path.Combine(_directory, name);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    private static YouTubeUpload Upload(string title = "Sourdough in ten minutes", DateTimeOffset? publishAt = null)
        => new(title, "How to bake.", ["bread", "sourdough"], "22", "en", "private", publishAt, MadeForKids: false, SyntheticMedia: true);

    private static HttpResponseMessage SessionStarted(string location = Session)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private static HttpResponseMessage Incomplete(long? lastByte)
    {
        var response = new HttpResponseMessage((HttpStatusCode)308);
        if (lastByte is { } last)
        {
            response.Headers.TryAddWithoutValidation("Range", $"bytes=0-{last}");
        }

        return response;
    }

    [Fact]
    public async Task An_Upload_Opens_A_Resumable_Session_With_The_Metadata_Then_Puts_The_Whole_File()
    {
        var (client, handler) = Build();
        handler.Enqueue(_ => SessionStarted());
        handler.EnqueueJson(HttpStatusCode.Created, Created);

        var id = await client.UploadVideoAsync(File("video.mp4", Video), Upload(publishAt: new DateTimeOffset(2026, 11, 10, 17, 0, 0, TimeSpan.Zero)), new UploadSession(), CancellationToken.None);

        id.ShouldBe("newVideo001");
        handler.Requests.Count.ShouldBe(2);
        var start = handler.Requests[0];
        (start.Method, start.Uri.ToString()).ShouldBe(("POST", "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status"));
        start.Headers["X-Upload-Content-Length"].ShouldBe("10");
        start.Headers["X-Upload-Content-Type"].ShouldBe("video/mp4");
        using var body = JsonDocument.Parse(start.Text);
        var snippet = body.RootElement.GetProperty("snippet");
        snippet.GetProperty("title").GetString().ShouldBe("Sourdough in ten minutes");
        snippet.GetProperty("description").GetString().ShouldBe("How to bake.");
        snippet.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ShouldBe(["bread", "sourdough"]);
        snippet.GetProperty("categoryId").GetString().ShouldBe("22");
        snippet.GetProperty("defaultLanguage").GetString().ShouldBe("en");
        var status = body.RootElement.GetProperty("status");
        status.GetProperty("privacyStatus").GetString().ShouldBe("private");
        status.GetProperty("publishAt").GetString().ShouldBe("2026-11-10T17:00:00Z");
        status.GetProperty("selfDeclaredMadeForKids").GetBoolean().ShouldBeFalse();
        status.GetProperty("containsSyntheticMedia").GetBoolean().ShouldBeTrue();
        var put = handler.Requests[1];
        (put.Method, put.Uri.ToString()).ShouldBe(("PUT", Session));
        put.Body.ShouldBe(Video);
        put.Headers["Content-Type"].ShouldBe("video/mp4");
    }

    [Fact]
    public async Task An_Upload_Without_A_Language_Or_A_Publish_Time_Sends_Neither()
    {
        var (client, handler) = Build();
        handler.Enqueue(_ => SessionStarted());
        handler.EnqueueJson(HttpStatusCode.OK, Created);

        await client.UploadVideoAsync(File("video.mp4", Video), Upload() with { DefaultLanguage = null }, new UploadSession(), CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Requests[0].Text);
        body.RootElement.GetProperty("snippet").TryGetProperty("defaultLanguage", out _).ShouldBeFalse();
        body.RootElement.GetProperty("status").TryGetProperty("publishAt", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task After_A_Server_Error_The_Upload_Asks_Where_It_Stopped_And_Sends_Only_The_Rest()
    {
        var (client, handler) = Build();
        handler.Enqueue(_ => SessionStarted());
        handler.EnqueueJson(HttpStatusCode.ServiceUnavailable, "{}");
        handler.Enqueue(_ => Incomplete(lastByte: 3));
        handler.EnqueueJson(HttpStatusCode.Created, Created);

        var id = await client.UploadVideoAsync(File("video.mp4", Video), Upload(), new UploadSession(), CancellationToken.None);

        id.ShouldBe("newVideo001");
        var query = handler.Requests[2];
        (query.Method, query.Uri.ToString(), query.Headers["Content-Range"]).ShouldBe(("PUT", Session, "bytes */10"));
        query.Body.ShouldBeEmpty();
        var resumed = handler.Requests[3];
        resumed.Headers["Content-Range"].ShouldBe("bytes 4-9/10");
        resumed.Body.ShouldBe(Video[4..]);
    }

    [Fact]
    public async Task When_The_Connection_Drops_Mid_Upload_It_Resumes_From_The_Byte_YouTube_Confirmed()
    {
        var (client, handler) = Build();
        handler.Enqueue(_ => SessionStarted());
        handler.EnqueueThrow(new HttpRequestException("Connection reset by peer", new IOException("reset")));
        handler.Enqueue(_ => Incomplete(lastByte: 6));
        handler.EnqueueJson(HttpStatusCode.Created, Created);

        var id = await client.UploadVideoAsync(File("video.mp4", Video), Upload(), new UploadSession(), CancellationToken.None);

        id.ShouldBe("newVideo001");
        handler.Requests.Count(r => r.Method == "POST").ShouldBe(1);
        handler.Requests[3].Headers["Content-Range"].ShouldBe("bytes 7-9/10");
        handler.Requests[3].Body.ShouldBe(Video[7..]);
    }

    [Fact]
    public async Task A_Put_Answered_With_Resume_Incomplete_Is_Followed_By_A_Query_And_A_Continued_Put()
    {
        var (client, handler) = Build();
        handler.Enqueue(_ => SessionStarted());
        handler.Enqueue(_ => Incomplete(lastByte: 6));
        handler.Enqueue(_ => Incomplete(lastByte: 6));
        handler.EnqueueJson(HttpStatusCode.Created, Created);

        var id = await client.UploadVideoAsync(File("video.mp4", Video), Upload(), new UploadSession(), CancellationToken.None);

        id.ShouldBe("newVideo001");
        handler.Requests.Select(r => r.Method == "PUT" ? r.Headers["Content-Range"] : r.Method)
            .ShouldBe(["POST", "bytes 0-9/10", "bytes */10", "bytes 7-9/10"]);
    }

    [Fact]
    public async Task When_YouTube_Kept_No_Byte_The_Upload_Sends_The_Whole_File_Again_On_The_Same_Session()
    {
        var (client, handler) = Build();
        handler.Enqueue(_ => SessionStarted());
        handler.EnqueueJson(HttpStatusCode.BadGateway, "{}");
        handler.Enqueue(_ => Incomplete(lastByte: null));
        handler.EnqueueJson(HttpStatusCode.Created, Created);

        await client.UploadVideoAsync(File("video.mp4", Video), Upload(), new UploadSession(), CancellationToken.None);

        (handler.Requests[3].Uri.ToString(), handler.Requests[3].Headers["Content-Range"]).ShouldBe((Session, "bytes 0-9/10"));
        handler.Requests[3].Body.ShouldBe(Video);
    }

    [Fact]
    public async Task When_The_Upload_Session_Is_Gone_The_Upload_Starts_Over_With_A_New_Session()
    {
        var (client, handler) = Build();
        handler.Enqueue(_ => SessionStarted());
        handler.EnqueueJson(HttpStatusCode.InternalServerError, "{}");
        handler.EnqueueJson(HttpStatusCode.NotFound, "{}");
        handler.Enqueue(_ => SessionStarted(Session2));
        handler.EnqueueJson(HttpStatusCode.Created, Created);

        var id = await client.UploadVideoAsync(File("video.mp4", Video), Upload(), new UploadSession(), CancellationToken.None);

        id.ShouldBe("newVideo001");
        handler.Requests.Select(r => r.Method).ShouldBe(["POST", "PUT", "PUT", "POST", "PUT"]);
        handler.Requests[4].Uri.ToString().ShouldBe(Session2);
        handler.Requests[4].Body.ShouldBe(Video);
    }

    [Fact]
    public async Task A_Query_That_Finds_The_Upload_Complete_Returns_The_Video_Without_Sending_Again()
    {
        var (client, handler) = Build();
        handler.Enqueue(_ => SessionStarted());
        handler.EnqueueThrow(new HttpRequestException("Connection reset by peer"));
        handler.EnqueueJson(HttpStatusCode.OK, Created);

        var id = await client.UploadVideoAsync(File("video.mp4", Video), Upload(), new UploadSession(), CancellationToken.None);

        id.ShouldBe("newVideo001");
        handler.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_Session_Kept_From_An_Earlier_Call_Is_Resumed_And_No_New_Session_Is_Opened()
    {
        var (client, handler) = Build();
        handler.Enqueue(_ => Incomplete(lastByte: 4));
        handler.EnqueueJson(HttpStatusCode.Created, Created);

        var id = await client.UploadVideoAsync(File("video.mp4", Video), Upload(), new UploadSession { Address = new Uri(Session) }, CancellationToken.None);

        id.ShouldBe("newVideo001");
        handler.Requests.Select(r => (r.Method, r.Uri.ToString())).ShouldBe([("PUT", Session), ("PUT", Session)]);
        handler.Requests[0].Headers["Content-Range"].ShouldBe("bytes */10");
        handler.Requests[1].Body.ShouldBe(Video[5..]);
    }

    [Fact]
    public async Task An_Upload_Stopped_By_A_Rate_Limit_Keeps_Its_Session_For_The_Next_Call()
    {
        var (client, handler) = Build();
        handler.Enqueue(_ => SessionStarted());
        handler.EnqueueJson(HttpStatusCode.TooManyRequests, GoogleJson.Error(429, "rateLimitExceeded"));
        var session = new UploadSession();

        var error = await Should.ThrowAsync<YouTubeApiException>(
            () => client.UploadVideoAsync(File("video.mp4", Video), Upload(), session, CancellationToken.None));

        error.Retryable.ShouldBeTrue();
        session.Address.ShouldBe(new Uri(Session));
    }

    [Fact]
    public async Task A_Put_That_Stalls_Past_Its_Time_Limit_Is_Resumed_Like_A_Dropped_Connection()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var stalled = new TaskCompletionSource();
        var calls = 0;
        var handler = new StallingHandler(async (request, token) =>
        {
            switch (Interlocked.Increment(ref calls))
            {
                case 1:
                    return SessionStarted();
                case 2:
                    stalled.SetResult();
                    await Task.Delay(Timeout.Infinite, token);
                    throw new InvalidOperationException("unreachable");
                case 3:
                    return Incomplete(lastByte: 2);
                default:
                    return StubHttpMessageHandler.Json(HttpStatusCode.Created, Created);
            }
        });
        var client = new YouTubeWriteClient(new HttpClient(handler) { BaseAddress = new Uri("https://www.googleapis.com/") }, time) { Backoff = TimeSpan.Zero };

        var upload = client.UploadVideoAsync(File("video.mp4", Video), Upload(), new UploadSession(), CancellationToken.None);
        await stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        time.Advance(YouTubeWriteClient.StallLimit(10) - TimeSpan.FromSeconds(1));
        upload.IsCompleted.ShouldBeFalse();
        time.Advance(TimeSpan.FromSeconds(1));

        (await upload.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe("newVideo001");
        calls.ShouldBe(4);
    }

    [Fact]
    public void The_Stall_Limit_Is_Five_Minutes_Plus_The_Remaining_Bytes_At_100_KB_A_Second()
        => YouTubeWriteClient.StallLimit(1_024_000).ShouldBe(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(10));

    [Fact]
    public async Task An_Upload_That_Keeps_Failing_Stops_After_Five_Attempts_With_A_Retryable_Error_And_Keeps_Its_Session()
    {
        var handler = new StubHttpMessageHandler(request => request.Method == HttpMethod.Post
            ? SessionStarted()
            : StubHttpMessageHandler.Json(HttpStatusCode.ServiceUnavailable, "{}"));
        var client = new YouTubeWriteClient(new HttpClient(handler) { BaseAddress = new Uri("https://www.googleapis.com/") }, TimeProvider.System)
        {
            Backoff = TimeSpan.Zero,
        };

        var session = new UploadSession();
        var error = await Should.ThrowAsync<YouTubeApiException>(
            () => client.UploadVideoAsync(File("video.mp4", Video), Upload(), session, CancellationToken.None));

        session.Address.ShouldBe(new Uri(Session));
        error.Message.ShouldBe(YouTubeWriteClient.UploadInterrupted);
        error.Retryable.ShouldBeTrue();
        handler.Requests.Count(r => r.Method == "POST").ShouldBe(1);
        handler.Requests.Count(r => r.Method == "PUT").ShouldBe(YouTubeWriteClient.MaxAttempts);
    }

    [Fact]
    public async Task An_Upload_Session_Address_Outside_Googleapis_Is_Refused_Without_Sending_The_Video_There()
    {
        var (client, handler) = Build();
        handler.Enqueue(_ => SessionStarted("https://uploads.example.com/session"));

        var error = await Should.ThrowAsync<YouTubeApiException>(
            () => client.UploadVideoAsync(File("video.mp4", Video), Upload(), new UploadSession(), CancellationToken.None));

        error.Message.ShouldBe(YouTubeWriteClient.UnexpectedSession);
        handler.Requests.ShouldHaveSingleItem().Method.ShouldBe("POST");
    }

    [Theory]
    [InlineData("Bread <3 butter", "How to bake.")]
    [InlineData("Bread", "Use > 2 cups.")]
    public async Task A_Title_Or_Description_With_Angle_Brackets_Is_Refused_Before_Any_Request(string title, string description)
    {
        var (client, handler) = Build();

        var error = await Should.ThrowAsync<YouTubeApiException>(
            () => client.UploadVideoAsync(File("video.mp4", Video), Upload(title) with { Description = description }, new UploadSession(), CancellationToken.None));

        error.Message.ShouldBe(YouTubeWriteClient.AngleBrackets);
        error.Retryable.ShouldBeFalse();
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Spent_Quota_On_Upload_Is_Retryable_Because_It_Resets_Daily()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.Forbidden, GoogleJson.Error(403, "quotaExceeded"));

        var error = await Should.ThrowAsync<YouTubeApiException>(
            () => client.UploadVideoAsync(File("video.mp4", Video), Upload(), new UploadSession(), CancellationToken.None));

        (error.Message, error.Retryable).ShouldBe((YouTubeClient.QuotaMessage, true));
    }

    [Fact]
    public async Task An_Upload_Without_The_Upload_Permission_Asks_To_Allow_Writes_Again_And_Is_Retryable()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.Forbidden, GoogleJson.Error(403, "insufficientPermissions"));

        var error = await Should.ThrowAsync<YouTubeApiException>(
            () => client.UploadVideoAsync(File("video.mp4", Video), Upload(), new UploadSession(), CancellationToken.None));

        (error.Message, error.Retryable).ShouldBe(("YouTube needs the upload permission: open the YouTube card in Scout Desktop and allow writes again.", true));
    }

    [Fact]
    public async Task A_Thumbnail_Without_The_Upload_Permission_Asks_To_Allow_Writes_Again_And_Is_Retryable()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.Forbidden, GoogleJson.Error(403, "insufficientPermissions"));

        var error = await Should.ThrowAsync<YouTubeApiException>(
            () => client.SetThumbnailAsync("newVideo001", File("thumbnail.png", [1]), CancellationToken.None));

        (error.Message, error.Retryable).ShouldBe((YouTubeWriteClient.MissingPermission, true));
    }

    [Fact]
    public async Task A_Rejected_Upload_Is_Not_Retryable()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.BadRequest, GoogleJson.Error(400, "invalidTitle"));

        var error = await Should.ThrowAsync<YouTubeApiException>(
            () => client.UploadVideoAsync(File("video.mp4", Video), Upload(), new UploadSession(), CancellationToken.None));

        (error.Message, error.Retryable, error.StatusCode).ShouldBe(("YouTube rejected the request (invalidTitle).", false, HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task A_Thumbnail_Is_Posted_To_Thumbnails_Set_As_A_Png()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """{"items":[{"default":{"url":"https://i.ytimg.com/vi/x/default.jpg"}}]}""");

        await client.SetThumbnailAsync("newVideo001", File("thumbnail.png", [0x89, 0x50, 0x4E, 0x47]), CancellationToken.None);

        var request = handler.Requests.ShouldHaveSingleItem();
        (request.Method, request.Uri.ToString()).ShouldBe(("POST", "https://www.googleapis.com/upload/youtube/v3/thumbnails/set?videoId=newVideo001"));
        request.Headers["Content-Type"].ShouldBe("image/png");
        request.Body.ShouldBe([0x89, 0x50, 0x4E, 0x47]);
    }

    [Fact]
    public async Task A_Jpeg_Thumbnail_Is_Sent_As_A_Jpeg()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, "{}");

        await client.SetThumbnailAsync("newVideo001", File("thumbnail-proposal-24h-2.jpg", [0xFF, 0xD8]), CancellationToken.None);

        handler.Requests.ShouldHaveSingleItem().Headers["Content-Type"].ShouldBe("image/jpeg");
    }

    [Fact]
    public async Task A_Thumbnail_YouTube_Forbids_Says_The_Channel_Must_Be_Verified_And_Is_Not_Retryable()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.Forbidden, GoogleJson.Error(403, "forbidden"));

        var error = await Should.ThrowAsync<YouTubeApiException>(
            () => client.SetThumbnailAsync("newVideo001", File("thumbnail.png", [1]), CancellationToken.None));

        (error.Message, error.Retryable).ShouldBe((YouTubeWriteClient.ThumbnailForbidden, false));
    }

    [Fact]
    public async Task A_Thumbnail_Over_The_Upload_Rate_Limit_Is_Retryable()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.TooManyRequests, GoogleJson.Error(429, "uploadRateLimitExceeded"));

        var error = await Should.ThrowAsync<YouTubeApiException>(
            () => client.SetThumbnailAsync("newVideo001", File("thumbnail.png", [1]), CancellationToken.None));

        (error.Message, error.Retryable).ShouldBe((YouTubeWriteClient.RateLimited, true));
    }

    [Fact]
    public async Task A_New_Title_Is_Sent_With_The_Rest_Of_The_Snippet_As_YouTube_Holds_It()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """
            {"items":[{"id":"newVideo001","snippet":{"title":"Old title","description":"How to bake.","tags":["bread"],
             "categoryId":"26","defaultLanguage":"en","channelTitle":"Owner Bakes","publishedAt":"2026-11-03T12:00:00Z"}}]}
            """);
        handler.EnqueueJson(HttpStatusCode.OK, """{"id":"newVideo001"}""");

        await client.UpdateTitleAsync("newVideo001", "Bread in ten minutes", CancellationToken.None);

        handler.Requests[0].Uri.PathAndQuery.ShouldBe("/youtube/v3/videos?part=snippet&id=newVideo001");
        var update = handler.Requests[1];
        (update.Method, update.Uri.PathAndQuery).ShouldBe(("PUT", "/youtube/v3/videos?part=snippet"));
        using var body = JsonDocument.Parse(update.Text);
        body.RootElement.GetProperty("id").GetString().ShouldBe("newVideo001");
        var snippet = body.RootElement.GetProperty("snippet");
        snippet.GetProperty("title").GetString().ShouldBe("Bread in ten minutes");
        snippet.GetProperty("description").GetString().ShouldBe("How to bake.");
        snippet.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ShouldBe(["bread"]);
        snippet.GetProperty("categoryId").GetString().ShouldBe("26");
        snippet.GetProperty("defaultLanguage").GetString().ShouldBe("en");
    }

    [Fact]
    public async Task A_Title_The_Video_Already_Has_Is_Not_Sent_Again()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """{"items":[{"id":"newVideo001","snippet":{"title":"Bread in ten minutes","categoryId":"22"}}]}""");

        await client.UpdateTitleAsync("newVideo001", "Bread in ten minutes", CancellationToken.None);

        handler.Requests.ShouldHaveSingleItem().Method.ShouldBe("GET");
    }

    [Fact]
    public async Task A_New_Title_For_A_Video_YouTube_Does_Not_Have_Fails_Without_An_Update()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, GoogleJson.NoItems);

        var error = await Should.ThrowAsync<YouTubeApiException>(
            () => client.UpdateTitleAsync("gone0000001", "Bread", CancellationToken.None));

        (error.Message, error.StatusCode).ShouldBe((YouTubeClient.NotFoundMessage, HttpStatusCode.NotFound));
        handler.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_Video_Status_Reads_Upload_And_Privacy_Status_And_The_Publish_Time()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """
            {"items":[{"id":"newVideo001","status":{"uploadStatus":"processed","privacyStatus":"private","publishAt":"2026-11-10T17:00:00Z"}}]}
            """);

        var status = await client.GetVideoStatusAsync("newVideo001", CancellationToken.None);

        handler.Requests.ShouldHaveSingleItem().Uri.PathAndQuery.ShouldBe("/youtube/v3/videos?part=status&id=newVideo001");
        status.ShouldBe(new YouTubeVideoStatus("processed", "private", new DateTimeOffset(2026, 11, 10, 17, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task A_Reply_Is_Posted_To_Comments_Insert_With_The_Parent_And_The_Text_And_Returns_The_Reply_Id()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, GoogleJson.InsertedReply);

        var replyId = await client.ReplyToCommentAsync("UgComment001", "Feed it daily for a week.", CancellationToken.None);

        var request = handler.Requests.ShouldHaveSingleItem();
        (request.Method, request.Uri.ToString()).ShouldBe(("POST", "https://www.googleapis.com/youtube/v3/comments?part=snippet"));
        using var body = JsonDocument.Parse(request.Body!);
        body.RootElement.GetRawText().ShouldBe("""{"snippet":{"parentId":"UgComment001","textOriginal":"Feed it daily for a week."}}""");
        replyId.ShouldBe("UgComment001.reply0001");
    }

    [Fact]
    public async Task A_Hold_Sets_Held_For_Review_With_No_Body_And_Never_Rejects_Or_Bans()
    {
        var (client, handler) = Build();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await client.HoldCommentAsync("UgComment001", CancellationToken.None);

        var request = handler.Requests.ShouldHaveSingleItem();
        (request.Method, request.Uri.ToString(), request.Body).ShouldBe(
            ("POST", "https://www.googleapis.com/youtube/v3/comments/setModerationStatus?id=UgComment001&moderationStatus=heldForReview", null));
    }

    public static TheoryData<int, string, string, bool> CommentWriteFailures => new()
    {
        { 503, "backendError", YouTubeClient.UnavailableMessage, true },
        { 403, "insufficientPermissions", "YouTube needs Scout's permission to reply to and moderate comments: open the YouTube card in Scout Desktop and allow writes again.", true },
        { 403, "forbidden", YouTubeWriteClient.CommentForbidden, false },
        { 403, "quotaExceeded", YouTubeClient.QuotaMessage, true },
    };

    [Theory]
    [MemberData(nameof(CommentWriteFailures))]
    public async Task Reply_And_Hold_5xx_And_403_Fail_With_The_Owners_Sentence_And_Whether_It_Can_Pass(
        int status, string reason, string message, bool retryable)
    {
        var (client, handler) = Build();
        handler.EnqueueJson((HttpStatusCode)status, GoogleJson.Error(status, reason));
        handler.EnqueueJson((HttpStatusCode)status, GoogleJson.Error(status, reason));

        var reply = await Should.ThrowAsync<YouTubeApiException>(
            () => client.ReplyToCommentAsync("UgComment001", "Thanks!", CancellationToken.None));
        var hold = await Should.ThrowAsync<YouTubeApiException>(() => client.HoldCommentAsync("UgComment001", CancellationToken.None));

        (reply.Message, reply.Retryable, reply.StatusCode).ShouldBe((message, retryable, (HttpStatusCode)status));
        (hold.Message, hold.Retryable, hold.StatusCode).ShouldBe((message, retryable, (HttpStatusCode)status));
    }

    [Fact]
    public async Task A_Reply_YouTube_Took_With_An_Unreadable_Answer_Fails_With_The_Success_Status()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, "<html>proxy</html>");
        handler.EnqueueJson(HttpStatusCode.OK, """{"kind":"youtube#comment"}""");

        var unreadable = await Should.ThrowAsync<YouTubeApiException>(() => client.ReplyToCommentAsync("UgComment001", "Thanks!", CancellationToken.None));
        var noId = await Should.ThrowAsync<YouTubeApiException>(() => client.ReplyToCommentAsync("UgComment001", "Thanks!", CancellationToken.None));

        (unreadable.Message, unreadable.StatusCode, unreadable.Retryable).ShouldBe(("YouTube returned an unexpected response.", HttpStatusCode.OK, false));
        (noId.Message, noId.StatusCode).ShouldBe((YouTubeWriteClient.NoReplyId, HttpStatusCode.OK));
    }

    [Fact]
    public async Task Reply_And_Hold_Timeout_Propagates_As_The_Timeout()
    {
        var (client, handler) = Build();
        handler.EnqueueThrow(new TaskCanceledException("timed out"));
        handler.EnqueueThrow(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<TaskCanceledException>(() => client.ReplyToCommentAsync("UgComment001", "Thanks!", CancellationToken.None));
        await Should.ThrowAsync<TaskCanceledException>(() => client.HoldCommentAsync("UgComment001", CancellationToken.None));

        handler.Requests.Count.ShouldBe(2);
    }

    private sealed class StallingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await respond(request, cancellationToken);
            response.RequestMessage = request;
            return response;
        }
    }
}
