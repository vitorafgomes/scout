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

    public static string Error(int code, string reason) => $$$"""
        {"error":{"code":{{{code}}},"message":"The request cannot be completed.","errors":[{"message":"The request cannot be completed.","domain":"youtube.quota","reason":"{{{reason}}}"}]}}
        """;
}
