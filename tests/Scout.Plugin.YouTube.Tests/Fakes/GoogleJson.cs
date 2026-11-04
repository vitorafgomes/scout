namespace Scout.Plugin.YouTube.Tests.Fakes;

/// <summary>
/// Response bodies in the shape the YouTube Data API v3 returns: counts as JSON strings,
/// hidden counts absent, durations in ISO 8601.
/// </summary>
internal static class GoogleJson
{
    public const string Channel = """
        {"kind":"youtube#channelListResponse","items":[{"kind":"youtube#channel","id":"UCowner0000000000000000",
         "snippet":{"title":"Owner Channel","customUrl":"@ownerchannel","publishedAt":"2019-03-04T10:00:00Z"},
         "contentDetails":{"relatedPlaylists":{"likes":"","uploads":"UUowner0000000000000000"}},
         "statistics":{"viewCount":"1234567","subscriberCount":"45600","hiddenSubscriberCount":false,"videoCount":"321"}}]}
        """;

    public const string ChannelWithHiddenSubscribers = """
        {"items":[{"id":"UCquiet","snippet":{"title":"Quiet","customUrl":"@quiet","publishedAt":"2020-01-01T00:00:00Z"},
         "statistics":{"viewCount":"10","subscriberCount":"0","hiddenSubscriberCount":true,"videoCount":"1"}}]}
        """;

    public const string NoItems = """{"kind":"youtube#channelListResponse","pageInfo":{"totalResults":0},"items":[]}""";

    public const string Uploads = """
        {"items":[{"contentDetails":{"videoId":"abcDEF12345","videoPublishedAt":"2026-10-30T18:00:00Z"}},
                  {"contentDetails":{"videoId":"xyz_-987654","videoPublishedAt":"2026-10-20T18:00:00Z"}}]}
        """;

    public const string Videos = """
        {"items":[
         {"id":"abcDEF12345","snippet":{"title":"Building a bot","channelTitle":"Owner Channel","publishedAt":"2026-10-30T18:00:00Z"},
          "contentDetails":{"duration":"PT1H2M3S"},"statistics":{"viewCount":"15230","likeCount":"1200","commentCount":"87"}},
         {"id":"xyz_-987654","snippet":{"title":"Short tip","channelTitle":"Owner Channel","publishedAt":"2026-10-20T18:00:00Z"},
          "contentDetails":{"duration":"PT45S"},"statistics":{"viewCount":"980"}}]}
        """;

    public const string Search = """
        {"kind":"youtube#searchListResponse","items":[
         {"kind":"youtube#searchResult","id":{"kind":"youtube#video","videoId":"srch0000001"},
          "snippet":{"publishedAt":"2026-09-01T12:00:00Z","channelId":"UCother","title":"Sourdough in 10 minutes","description":"A fast starter.","channelTitle":"Bread Lab"}},
         {"kind":"youtube#searchResult","id":{"kind":"youtube#channel","channelId":"UCbread"},
          "snippet":{"publishedAt":"2020-01-01T00:00:00Z","channelId":"UCbread","title":"Bread Lab","description":"","channelTitle":"Bread Lab"}},
         {"kind":"youtube#searchResult","id":{"kind":"youtube#video","videoId":"srch0000002"},
          "snippet":{"publishedAt":"2026-08-15T08:30:00Z","channelId":"UCother2","title":"Why bread rises","channelTitle":"Kitchen Science"}}]}
        """;

    public const string CommentThreads = """
        {"kind":"youtube#commentThreadListResponse","nextPageToken":"page2token","items":[
         {"kind":"youtube#commentThread","id":"UgThread001","snippet":{"channelId":"UCowner0000000000000000","videoId":"abcDEF12345",
          "topLevelComment":{"kind":"youtube#comment","id":"UgComment001","snippet":{"videoId":"abcDEF12345",
           "textDisplay":"How long do I feed the starter?","textOriginal":"How long do I feed the starter?",
           "authorDisplayName":"@breadfan","authorChannelId":{"value":"UCfan"},"likeCount":3,
           "publishedAt":"2026-11-02T09:30:00Z","updatedAt":"2026-11-02T09:30:00Z"}},
          "canReply":true,"totalReplyCount":2,"isPublic":true}},
         {"kind":"youtube#commentThread","id":"UgThread002","snippet":{"channelId":"UCowner0000000000000000","videoId":"xyz_-987654",
          "topLevelComment":{"kind":"youtube#comment","id":"UgComment002","snippet":{"videoId":"xyz_-987654",
           "textDisplay":"Great tip","authorDisplayName":"Anonymous","likeCount":0,"publishedAt":"2026-11-01T18:00:00Z"}},
          "canReply":false,"totalReplyCount":0,"isPublic":true}}]}
        """;

    public const string CommentThreadsLastPage = """{"kind":"youtube#commentThreadListResponse","items":[]}""";

    public const string InsertedReply = """
        {"kind":"youtube#comment","id":"UgComment001.reply0001","snippet":{"parentId":"UgComment001",
         "textOriginal":"Feed it daily for a week.","authorDisplayName":"Owner Channel"}}
        """;

    public static string Error(int code, string reason) => $$$"""
        {"error":{"code":{{{code}}},"message":"The request cannot be completed.","errors":[{"message":"The request cannot be completed.","domain":"youtube.quota","reason":"{{{reason}}}"}]}}
        """;
}
