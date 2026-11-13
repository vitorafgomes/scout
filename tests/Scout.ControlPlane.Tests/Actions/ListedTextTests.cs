using Scout.ControlPlane.Actions;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Actions;

/// <summary>
/// The text of a waiting action the desktop's list carries, read from its payload and cut to a bound, whether it was cut,
/// and the payload's digest: the cases where the payload has no text, is not an object, does not parse, holds text within
/// the bound or holds more than the bound.
/// </summary>
public sealed class ListedTextTests
{
    private const string DigestOfNoBytes = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private static PendingAction Holding(string? payload) =>
        new("a1", "github", "New issues", "GitHubActions.create_issue", "s", DateTimeOffset.UnixEpoch, PendingActionStatus.Pending, null)
        {
            Payload = payload,
        };

    public static TheoryData<string, string?, int?, int?, bool> Cases() => new()
    {
        { "no payload", null, null, null, false },
        { "a payload that does not parse", "not json", null, null, false },
        { "a payload that is not an object", "[1]", null, null, false },
        { "a body that is not text", """{"body":5,"title":true}""", null, null, false },
        { "a title and a body at exactly their bound are not cut", $$"""{"title":"{{new string('t', 256)}}","body":"{{new string('b', 4_000)}}"}""", 256, 4_000, false },
        { "a body one over the bound cut to 4,000", $$"""{"body":"{{new string('b', 4_001)}}"}""", null, 4_000, true },
        { "a title over the bound cut to 256", $$"""{"title":"{{new string('t', 300)}}"}""", 256, null, true },
        { "a body over the bound cut before a split character pair", $$"""{"body":"{{new string('a', 3_999)}}😀"}""", null, 3_999, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_Listed_Text_Is_The_Payload_Title_And_Body_Cut_To_Their_Bound_And_Says_Whether_It_Was_Cut(
        string label, string? payload, int? titleLength, int? bodyLength, bool clipped)
    {
        var (title, body, cut) = Holding(payload).ListedText();

        (title?.Length, body?.Length, cut).ShouldBe((titleLength, bodyLength, clipped), label);
        if (body is not null)
        {
            char.IsHighSurrogate(body[^1]).ShouldBeFalse(label);
        }
    }

    [Fact]
    public void The_Digest_Of_An_Action_Without_A_Payload_Is_That_Of_No_Bytes()
    {
        Holding(null).PayloadDigest().ShouldBe(DigestOfNoBytes);
    }

    public static TheoryData<string, string, string> Digests() => new()
    {
        { "an empty payload is the digest of no bytes", "", DigestOfNoBytes },
        { "two-, three- and four-byte characters are hashed as their UTF-8 bytes", """{"body":"Ação 漢字 😀","title":"Ünïcödé"}""", "23810f21d1f0e8b65ab4f529682240131c5dd41b2b8bb59b3f58e77b8440e59e" },
    };

    [Theory]
    [MemberData(nameof(Digests))]
    public void The_Digest_Is_The_SHA256_Of_The_Payload_s_UTF8_Bytes_In_Lowercase_Hex(string label, string payload, string digest)
    {
        Holding(payload).PayloadDigest().ShouldBe(digest, label);
    }
}
