using System.Net;
using Microsoft.Extensions.Time.Testing;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Operator.Tests.Fakes;
using Scout.Plugin.YouTube;

namespace Scout.Operator.Tests;

public sealed class TopicResearcherTests
{
    [Fact]
    public async Task Research_Makes_At_Most_Five_Searches_Per_Project_From_The_Topic_And_The_Pillars()
    {
        var youtube = Given.YouTube();
        var chat = ScriptedChat.Answering(Given.Candidates("Sourdough basics"));
        var mandate = Given.Mandate(pillars: ["starters", "Sourdough", "flour", "ovens", "shaping", "scoring"]);

        await Given.Researcher(youtube, chat).ResearchAsync("sourdough", mandate, CancellationToken.None);

        youtube.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(youtube.SearchVideosAsync))
            .Select(c => (string)c.GetArguments()[0]!)
            .ShouldBe(["sourdough", "starters", "flour", "ovens", "shaping"]);
    }

    [Fact]
    public async Task A_Candidate_Naming_A_Blocked_Topic_Is_Dropped_Whatever_Its_Case()
    {
        var chat = ScriptedChat.Answering(Given.Candidates("An easy GLUTEN-FREE loaf", "Sourdough basics"));
        var mandate = Given.Mandate(blocked: ["gluten-free"]);

        var research = await Given.Researcher(Given.YouTube(), chat)
            .ResearchAsync("bread", mandate, CancellationToken.None);

        research.Candidates.Select(c => c.Title).ShouldBe(["Sourdough basics"]);
    }

    [Fact]
    public async Task With_Todays_Searches_Spent_Research_Searches_No_More_And_Tells_The_Model_Why()
    {
        var youtube = Given.YouTube();
        var chat = ScriptedChat.Answering(Given.Candidates("Sourdough basics"));
        var budget = new SearchBudget(new FakeTimeProvider(Given.Now));
        while (budget.TryTake())
        {
        }

        var research = await Given.Researcher(youtube, chat, budget)
            .ResearchAsync("sourdough", Given.Mandate(), CancellationToken.None);

        await youtube.DidNotReceive().SearchVideosAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        chat.Requests.ShouldHaveSingleItem().Last().Text.ShouldContain(SearchBudget.Spent);
        research.Candidates.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task The_Sources_Are_The_Links_Of_The_Channels_Uploads_And_Of_The_Search_Results()
    {
        var youtube = Given.YouTube(uploads: [Given.Upload("upload00001")], found: [Given.Found("found000001")]);
        var chat = ScriptedChat.Answering(Given.Candidates("Sourdough basics"));

        var research = await Given.Researcher(youtube, chat)
            .ResearchAsync("sourdough", Given.Mandate(), CancellationToken.None);

        research.Sources.Select(s => s.Url).ShouldBe([
            "https://www.youtube.com/watch?v=upload00001",
            "https://www.youtube.com/watch?v=found000001",
        ]);
    }

    [Fact]
    public async Task An_Answer_That_Is_Not_The_Json_Asked_For_Is_Asked_Once_More_And_The_Second_Answer_Is_Used()
    {
        var chat = ScriptedChat.Answering("Here are some ideas: sourdough!", Given.Candidates("Sourdough basics"));

        var research = await Given.Researcher(Given.YouTube(), chat)
            .ResearchAsync("sourdough", Given.Mandate(), CancellationToken.None);

        chat.Requests.Count.ShouldBe(2);
        chat.Requests[1].Last().ShouldBe((AuthorRole.User, ModelJson.Retry));
        research.Candidates.Select(c => c.Title).ShouldBe(["Sourdough basics"]);
    }

    [Fact]
    public async Task The_Second_Ask_Carries_Only_The_System_Prompt_The_Request_And_The_Retry_Never_The_Bad_Answer()
    {
        var chat = ScriptedChat.Answering("Here are some ideas: sourdough!", Given.Candidates("Sourdough basics"));

        await Given.Researcher(Given.YouTube(), chat).ResearchAsync("sourdough", Given.Mandate(), CancellationToken.None);

        chat.Requests[1].Select(m => m.Role).ShouldBe([AuthorRole.System, AuthorRole.User, AuthorRole.User]);
        chat.Requests[1].Take(2).ShouldBe(chat.Requests[0]);
        chat.Requests[1].ShouldNotContain(m => m.Role == AuthorRole.Assistant);
    }

    [Fact]
    public async Task Two_Answers_That_Are_Not_The_Json_Asked_For_Fail_The_Research_With_A_Readable_Error()
    {
        var chat = ScriptedChat.Answering("not json", """{"candidates":[]}""");

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Given.Researcher(Given.YouTube(), chat)
            .ResearchAsync("sourdough", Given.Mandate(), CancellationToken.None));

        ex.Message.ShouldBe("The AI model did not return topic candidates in the expected form, twice.");
        chat.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_Model_Is_Offered_No_Function_So_A_Search_Result_Cannot_Make_It_Call_One()
    {
        var chat = ScriptedChat.Answering(Given.Candidates("Sourdough basics"));

        await Given.Researcher(Given.YouTube(), chat)
            .ResearchAsync("sourdough", Given.Mandate(), CancellationToken.None);

        chat.Offers.ShouldHaveSingleItem().ShouldBe((null, null));
    }

    [Fact]
    public async Task A_YouTube_Error_Fails_The_Research_With_YouTubes_Readable_Message_Before_The_Model_Is_Asked()
    {
        var youtube = Given.YouTube();
        youtube.SearchVideosAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<Scout.Plugin.YouTube.Abstractions.YouTubeSearchResult>>>(_ =>
                throw new YouTubeApiException("YouTube's daily quota for this key is spent; it resets at midnight Pacific time.", HttpStatusCode.Forbidden));
        var chat = ScriptedChat.Answering(Given.Candidates("never"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Given.Researcher(youtube, chat)
            .ResearchAsync("sourdough", Given.Mandate(), CancellationToken.None));

        ex.Message.ShouldBe("YouTube's daily quota for this key is spent; it resets at midnight Pacific time.");
        chat.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_A_YouTube_Sign_In_Research_Goes_On_Without_The_Channels_Uploads_And_Tells_The_Model_They_Are_Unavailable()
    {
        var youtube = Given.YouTube(found: [Given.Found("found000001")]);
        youtube.GetMyChannelAsync(Arg.Any<CancellationToken>())
            .Returns<Task<Scout.Plugin.YouTube.Abstractions.YouTubeChannel?>>(_ =>
                throw new YouTubeApiException("Sign in from the Scout app to read your own channel; an API key reads public channels by handle only.", HttpStatusCode.Unauthorized));
        var chat = ScriptedChat.Answering(Given.Candidates("Sourdough basics"));

        var research = await Given.Researcher(youtube, chat)
            .ResearchAsync("sourdough", Given.Mandate(), CancellationToken.None);

        research.Candidates.Select(c => c.Title).ShouldBe(["Sourdough basics"]);
        research.Sources.Select(s => s.Url).ShouldBe(["https://www.youtube.com/watch?v=found000001"]);
        chat.Requests.ShouldHaveSingleItem().Last().Text.ShouldContain(TopicResearcher.NoChannelHistory);
        await youtube.DidNotReceive().ListUploadsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Any_Other_YouTube_Error_On_The_Channel_Still_Fails_The_Research_With_YouTubes_Readable_Message()
    {
        var youtube = Given.YouTube();
        youtube.GetMyChannelAsync(Arg.Any<CancellationToken>())
            .Returns<Task<Scout.Plugin.YouTube.Abstractions.YouTubeChannel?>>(_ =>
                throw new YouTubeApiException("YouTube is currently unavailable. Try again later.", HttpStatusCode.ServiceUnavailable));
        var chat = ScriptedChat.Answering(Given.Candidates("never"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Given.Researcher(youtube, chat)
            .ResearchAsync("sourdough", Given.Mandate(), CancellationToken.None));

        ex.Message.ShouldBe("YouTube is currently unavailable. Try again later.");
        chat.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Connection_Reset_On_YouTube_Fails_The_Research_Saying_YouTube_Could_Not_Be_Reached()
    {
        var youtube = Given.YouTube();
        youtube.GetMyChannelAsync(Arg.Any<CancellationToken>())
            .Returns<Task<Scout.Plugin.YouTube.Abstractions.YouTubeChannel?>>(_ => throw new HttpRequestException("connection reset"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Given.Researcher(youtube, ScriptedChat.Answering("{}"))
            .ResearchAsync("sourdough", Given.Mandate(), CancellationToken.None));

        ex.Message.ShouldBe("Could not reach YouTube. Try again later.");
    }

    [Fact]
    public async Task A_Model_That_Does_Not_Answer_Fails_The_Research_With_A_Readable_Error()
    {
        var chat = new ScriptedChat(() => throw new HttpRequestException("503 from the provider"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Given.Researcher(Given.YouTube(), chat)
            .ResearchAsync("sourdough", Given.Mandate(), CancellationToken.None));

        ex.Message.ShouldBe("The AI model did not answer; check the AI Source in Scout Desktop.");
    }
}
