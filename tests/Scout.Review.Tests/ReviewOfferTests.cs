namespace Scout.Review.Tests;

/// <summary>The one decision of what a review offers, case by case in the words of the owner's rule.</summary>
public sealed class ReviewOfferTests
{
    private static readonly TestRunFacts Passed = new(true, null, "dotnet test", 0, [new("Shop.Tests", 827, 0, 0, false)], false);

    private static readonly TestRunFacts CouldNotRun = new(
        true, null, "dotnet test", 1, [new("Shop.Tests", 827, 0, 0, false), new("Shop.IntegrationTests", 0, 133, 0, true)], false);

    private static readonly TestRunFacts ReallyFailed = new(true, null, "dotnet test", 1, [new("Shop.Tests", 825, 2, 0, false)], false);

    private static readonly OfferChecks AllPassed = new(5, 0, 0, true);

    private static OfferInput Clean(params OfferFinding[] findings) => new(findings, 10, 10, Passed, AllPassed);

    private static readonly OfferFinding Block = new(Severities.Block, false);
    private static readonly OfferFinding Fix = new(Severities.Fix, false);
    private static readonly OfferFinding Nit = new(Severities.Nit, false);
    private static readonly OfferFinding UnconfirmedFix = new(Severities.Fix, true);

    public static TheoryData<string, OfferInput, Offer> Cases => new()
    {
        {
            "V1 a kept finding that needs fixing, confirmed, offers request changes",
            Clean(Block, Fix, Nit),
            new(ReviewOffers.RequestChanges, "Request changes: 2 findings need fixing.", true)
        },
        {
            "V1 tests that really failed offer request changes",
            Clean() with { Tests = ReallyFailed },
            new(ReviewOffers.RequestChanges, "Request changes: the pull request's tests failed.", true)
        },
        {
            "V1 a check that failed offers request changes",
            Clean(Fix) with { Checks = new(4, 1, 0, true) },
            new(ReviewOffers.RequestChanges, "Request changes: 1 finding needs fixing and 1 check failed.", true)
        },
        {
            "V2 only optional suggestions offer a comment, never approve",
            Clean(Nit, Nit),
            new(ReviewOffers.Comment, "Comment, not approve: 2 optional suggestions were kept.", false)
        },
        {
            "V2 only unconfirmed findings offer a comment, never approve",
            Clean(UnconfirmedFix),
            new(ReviewOffers.Comment, "Comment, not approve: 1 finding could not be confirmed.", false)
        },
        {
            "V3 files not read offer a comment, and the reason says how many",
            Clean() with { FilesRead = 7 },
            new(ReviewOffers.Comment, "Comment, not approve: 3 of 10 changed files were not read.", false)
        },
        {
            "V4 tests that could not run are covered when the pull request's own checks all passed",
            Clean() with { Tests = CouldNotRun },
            new(ReviewOffers.Comment, "Comment, not approve: every changed file was read, nothing needs fixing, and every check passed, which covers the tests that could not run here. " + ReviewOffers.NotOnItsOwn, false)
        },
        {
            "V4 tests that could not run and checks that cannot be read offer a comment that says which",
            Clean() with { Tests = CouldNotRun, Checks = null },
            new(ReviewOffers.Comment, "Comment, not approve: the pull request's checks could not be read and some tests could not run here and the pull request's checks cannot cover them.", false)
        },
        {
            "V4 no test run and no check at all offer a comment",
            Clean() with { Tests = null, Checks = new(0, 0, 0, true) },
            new(ReviewOffers.Comment, "Comment, not approve: no test ran here and no check of the pull request runs them.", false)
        },
        {
            "V5 nothing kept, every file read, tests passed, every check passed says so, and no review is offered approval on its own",
            Clean(),
            new(ReviewOffers.Comment, "Comment, not approve: every changed file was read, nothing needs fixing, the tests passed and every check passed. " + ReviewOffers.NotOnItsOwn, false)
        },
        {
            "checks still running with everything else clean say so, and no approval waits for them on its own",
            Clean() with { Checks = new(3, 0, 2, true) },
            new(ReviewOffers.Comment, "Comment, not approve: every changed file was read and nothing needs fixing, and 2 checks are still running. " + ReviewOffers.NotOnItsOwn, false)
        },
        {
            "a commit with more checks than Scout read offers a comment",
            Clean() with { Checks = new(100, 0, 0, false) },
            new(ReviewOffers.Comment, "Comment, not approve: the commit has more checks than Scout could read.", false)
        },
        {
            "the author's own pull request is offered a comment, and says why",
            Clean() with { ReviewerIsAuthor = true },
            new(ReviewOffers.Comment, "Comment, not approve: GitHub does not let the author approve their own pull request.", false)
        },
        {
            "conflicts offer a comment, and say why",
            Clean() with { Mergeable = false },
            new(ReviewOffers.Comment, "Comment, not approve: the pull request has conflicts with its base branch.", false)
        },
        {
            "mergeability unknown offers a comment, and says why",
            Clean() with { Mergeable = null },
            new(ReviewOffers.Comment, "Comment, not approve: GitHub has not said whether the pull request can be merged.", false)
        },
        {
            "the author's own pull request with a finding to fix still offers request changes",
            Clean(Block) with { ReviewerIsAuthor = true },
            new(ReviewOffers.RequestChanges, "Request changes: 1 finding needs fixing.", true)
        },
        {
            "a moved head offers nothing",
            Clean(Block) with { HeadMoved = true },
            new(ReviewOffers.None, ReviewOffers.Moved, false)
        },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_offer_follows_the_owner_s_rule(string rule, OfferInput input, Offer expected) =>
        ReviewOffers.Decide(input).ShouldBe(expected, rule);

    [Fact]
    public void No_review_is_offered_approval_on_its_own()
    {
        var offers = Cases.Select(c => ReviewOffers.Decide((OfferInput)c[1]).Verdict).ToList();

        offers.ShouldNotContain(ReviewOffers.Approve, "no review is offered approval on its own");
        offers.ShouldNotContain(ReviewOffers.Wait, "no approval waits for the checks on its own");
        ReviewOffers.NotOnItsOwn.ShouldBe("Scout does not approve on its own, so a person approves the pull request on GitHub.");
    }

    [Fact]
    public void Sending_as_a_comment_instead_is_allowed_exactly_when_the_offer_is_request_changes()
    {
        Cases.Select(c => ReviewOffers.Decide((OfferInput)c[1])).ShouldAllBe(o => o.CommentInstead == (o.Verdict == ReviewOffers.RequestChanges), "comment instead exactly for request changes");
    }
}
