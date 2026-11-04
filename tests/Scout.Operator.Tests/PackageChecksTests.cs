using Scout.Operator.Tests.Fakes;

namespace Scout.Operator.Tests;

public sealed class PackageChecksTests
{
    private static readonly OperatorScript Script = new(
        "Why do rivers bend?",
        [
            new OperatorScriptSection("Erosion", "Water cuts the outer bank. It drops sand on the inner bank."),
            new OperatorScriptSection("Floods", "Floods move the bends."),
            new OperatorScriptSection("Oxbows", "A bend can close into a lake."),
        ],
        "Subscribe for more rivers.",
        ["Why Rivers Bend", "Meanders explained"],
        "How rivers carve their shape.",
        ["rivers", "geography"],
        []);

    private static SceneCut Cut(string id, int order, string narration, int startMs, int durationMs, int captions = 1, bool placeholder = false)
        => new(Given.Scene(id, order, narration, "ready"), startMs, durationMs, captions, placeholder);

    private static List<SceneCut> Cuts() =>
    [
        Cut("s1", 1, "Why do rivers bend?", 0, 8_000),
        Cut("s2", 2, "Water cuts the outer bank.", 8_000, 12_000),
        Cut("s3", 3, "It drops sand on the inner bank.", 20_000, 6_000),
        Cut("s4", 4, "Floods move the bends.", 26_000, 19_000),
        Cut("s5", 5, "A bend can close into a lake.", 45_000, 35_000),
        Cut("s6", 6, "Subscribe for more rivers.", 80_000, 10_000),
    ];

    [Theory]
    [InlineData(29_999, "fail", "The video lasts 0:29; a video needs at least 0:30.")]
    [InlineData(30_000, "pass", "The video lasts 0:30.")]
    [InlineData(43_200_000, "pass", "The video lasts 12:00:00.")]
    [InlineData(43_201_000, "fail", "The video lasts 12:00:01; YouTube takes at most 12 hours.")]
    public void The_Duration_Fails_Under_30_Seconds_Or_Over_12_Hours(int ms, string result, string detail)
        => PackageChecks.Duration(ms).ShouldBe(new OperatorCheck("duration", result, detail));

    [Fact]
    public void The_Integrated_Loudness_Is_Read_From_A_Recorded_Loudnorm_Output()
    {
        PackageChecks.ParseLoudness(FakeProcessRunner.Loudnorm).ShouldBe(-14.21);
        PackageChecks.ParseLoudness(FakeProcessRunner.Loudnorm.Replace("\"-14.21\"", "\"-inf\"", StringComparison.Ordinal)).ShouldBeNull();
        PackageChecks.ParseLoudness("Error while filtering: Cannot allocate memory").ShouldBeNull();
        PackageChecks.LoudnessArguments("/media/p1/video.mp4")
            .ShouldBe(["-hide_banner", "-nostdin", "-i", "/media/p1/video.mp4", "-vn", "-af", "loudnorm=print_format=json", "-f", "null", "-"]);
    }

    [Theory]
    [InlineData(-14.21, "pass", "The video plays at -14.2 LUFS.")]
    [InlineData(-18.0, "pass", "The video plays at -18.0 LUFS.")]
    [InlineData(-18.1, "warn", "The video plays at -18.1 LUFS; aim between -18 and -11 LUFS.")]
    [InlineData(-10.5, "warn", "The video plays at -10.5 LUFS; aim between -18 and -11 LUFS.")]
    [InlineData(null, "warn", "Scout could not measure the loudness of the video.")]
    public void The_Loudness_Warns_Outside_Minus_18_To_Minus_11_LUFS(double? lufs, string result, string detail)
        => PackageChecks.Loudness(lufs).ShouldBe(new OperatorCheck("loudness", result, detail));

    [Fact]
    public void Captions_Fail_When_A_Scene_With_Narration_Has_No_Caption_Line()
    {
        PackageChecks.Captions([Cut("s1", 1, "Rivers bend.", 0, 3000, captions: 0), Cut("s2", 2, "  ", 3000, 3000, captions: 0)])
            .ShouldBe(new OperatorCheck("captions", "fail", "Scenes 1 have narration but no captions."));
        PackageChecks.Captions(Cuts()).Result.ShouldBe("pass");
    }

    [Fact]
    public void Placeholders_Fail_When_A_Scene_Is_Not_Ready_Or_Misses_Its_Media()
    {
        PackageChecks.Placeholders([Cut("s1", 1, "a", 0, 3000), Cut("s2", 2, "b", 3000, 3000, placeholder: true)])
            .ShouldBe(new OperatorCheck("placeholders", "fail", "Scenes 2 are not ready or miss their picture or narration file."));
        PackageChecks.Placeholders(Cuts()).Result.ShouldBe("pass");
    }

    [Fact]
    public async Task Background_satisfies_the_placeholder_check()
    {
        var media = Directory.CreateTempSubdirectory("scout-checks-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(media, "studio.png"), [5, 6]);
            OperatorScene[] scenes =
            [
                Given.Scene("s1", 1, "Picture gone.", "ready", visualSource: "generated", visualFile: "scene-s1.png", durationMs: 3000),
                Given.Scene("s2", 2, "Owner picture gone.", "ready", visualSource: "owner", visualFile: "mine.png", durationMs: 3000),
                Given.Scene("s3", 3, "Narration gone.", "ready", audioSource: "generated", audioFile: "scene-s3.mp3", durationMs: 3000),
            ];
            var assembler = new Assembler(Given.Toolset(new FakeProcessRunner()));

            var over = await assembler.AssembleAsync(Given.Tools(), scenes, media, "studio.png", null, "gentle", CancellationToken.None);
            var without = await assembler.AssembleAsync(Given.Tools(), scenes, media, null, null, "gentle", CancellationToken.None);

            PackageChecks.Placeholders(over.Cuts)
                .ShouldBe(new OperatorCheck("placeholders", "fail", "Scenes 3 are not ready or miss their picture or narration file."));
            PackageChecks.Placeholders([.. over.Cuts.Take(2)])
                .ShouldBe(new OperatorCheck("placeholders", "pass", "Every scene is ready with its own media, the project's background or a text card."));
            PackageChecks.Placeholders(without.Cuts)
                .ShouldBe(new OperatorCheck("placeholders", "fail", "Scenes 1, 2, 3 are not ready or miss their picture or narration file."));
        }
        finally
        {
            Directory.Delete(media, recursive: true);
        }
    }

    [Fact]
    public void Metadata_Fails_When_The_Title_Is_Empty_Or_Over_100_The_Description_Over_5000_Or_The_Tags_Over_500()
    {
        PackageChecks.Metadata("Why Rivers Bend", "short", ["rivers"]).Result.ShouldBe("pass");
        PackageChecks.Metadata(" ", "short", []).ShouldBe(new OperatorCheck(
            "metadata", "fail", "YouTube would refuse this metadata: the title must have 1 to 100 characters (it has 0)."));
        PackageChecks.Metadata(new string('t', 101), new string('d', 5001), [new string('x', 501)]).Detail.ShouldBe(
            "YouTube would refuse this metadata: the title must have 1 to 100 characters (it has 101); " +
            "the description must have at most 5000 characters (it has 5001); the tags must fit in 500 characters (they take 501).");
    }

    [Fact]
    public void Discoverability_Warns_When_The_Title_Misses_The_Topic_And_Pillars_Or_The_Description_Has_No_Chapters()
    {
        var mandate = Given.Mandate(pillars: ["geography"]);

        PackageChecks.Discoverability("Why Rivers Bend", "Intro\n\n0:00 Erosion\n0:20 Floods", "rivers", mandate).Result.ShouldBe("pass");
        PackageChecks.Discoverability("Amazing Geography", "0:00 Erosion", "rivers", mandate).Result.ShouldBe("pass");
        PackageChecks.Discoverability("You will not believe this", "No list here.", "rivers", mandate).ShouldBe(new OperatorCheck(
            "discoverability", "warn",
            "Viewers may not find this video: the title shares no word with the topic or the channel's pillars; the description has no chapters list."));
    }

    [Fact]
    public void The_First_Description_Is_The_Scripts_With_Chapters_Timed_From_The_Real_Scene_Lengths()
    {
        PackageChecks.Description(Script, Cuts()).ShouldBe("How rivers carve their shape.\n\n0:00 Erosion\n0:26 Floods\n0:45 Oxbows");
        PackageChecks.Description(Script, [.. Cuts().Take(4)]).ShouldBe("How rivers carve their shape.");
        PackageChecks.Title(Script, "rivers").ShouldBe("Why Rivers Bend");
        PackageChecks.Title(Script with { TitleVariants = [] }, "rivers").ShouldBe("rivers");
    }

    [Fact]
    public void A_First_Title_Over_100_Characters_Is_Cut_At_The_Last_Word_That_Fits_With_No_Trailing_Space()
    {
        var long120 = string.Join(' ', Enumerable.Repeat("Meanders", 13)) + " end";
        var topic200 = string.Concat(Enumerable.Repeat("rivers and ", 19)) + "lakes";

        var title = PackageChecks.Title(Script with { TitleVariants = [long120, "Short"] }, "rivers");
        var fromTopic = PackageChecks.Title(Script with { TitleVariants = [] }, topic200);

        long120.Length.ShouldBeGreaterThan(100);
        title.ShouldBe(string.Join(' ', Enumerable.Repeat("Meanders", 11)));
        topic200.Length.ShouldBeGreaterThan(200);
        fromTopic.ShouldBe(string.Concat(Enumerable.Repeat("rivers and ", 9)).TrimEnd());
        PackageChecks.Title(Script with { TitleVariants = [new string('x', 120)] }, "rivers").ShouldBe(new string('x', 100));
    }

    [Fact]
    public void A_Description_Over_5000_Characters_Cuts_The_Scripts_Text_And_Keeps_The_Chapters_List_Whole()
    {
        var longText = string.Concat(Enumerable.Repeat("Rivers carve. ", 400));

        var description = PackageChecks.Description(Script with { Description = longText }, Cuts());

        description.Length.ShouldBeLessThanOrEqualTo(5000);
        description.ShouldEndWith("\n\n0:00 Erosion\n0:26 Floods\n0:45 Oxbows");
        description.ShouldStartWith("Rivers carve. Rivers carve.");
        var kept = description.Split("\n\n")[0];
        longText.ShouldStartWith(kept);
        longText[kept.Length].ShouldBe(' ');
        PackageChecks.Description(Script with { Description = longText }, []).Length.ShouldBeLessThanOrEqualTo(5000);
    }

    private static List<SceneCut> StockCuts(string longCredit = "")
    {
        var cuts = Cuts();
        SceneCut Stock(int i, string credit) => cuts[i] with { Scene = cuts[i].Scene with { VisualSource = "stock", VisualFile = $"scene-s{i + 1}.jpg", VisualCredit = credit } };
        cuts[1] = Stock(1, "\"Outer bank\" by Ana is licensed under CC BY 2.0." + longCredit);
        cuts[3] = Stock(3, "Flood plain by Bo, CC0 1.0, via Openverse");
        cuts[4] = Stock(4, "\"Outer bank\" by Ana is licensed under CC BY 2.0." + longCredit);
        cuts[5] = cuts[5] with { Scene = cuts[5].Scene with { VisualSource = "generated", VisualCredit = "not a stock picture" } };
        return cuts;
    }

    [Fact]
    public void The_description_credits_every_stock_picture()
    {
        PackageChecks.Description(Script, StockCuts()).ShouldBe(
            "How rivers carve their shape.\n\n0:00 Erosion\n0:26 Floods\n0:45 Oxbows\n\n" +
            "Credits\n\"Outer bank\" by Ana is licensed under CC BY 2.0.\nFlood plain by Bo, CC0 1.0, via Openverse");
        PackageChecks.Description(Script, [.. StockCuts().Take(4)]).ShouldBe(
            "How rivers carve their shape.\n\nCredits\n\"Outer bank\" by Ana is licensed under CC BY 2.0.\nFlood plain by Bo, CC0 1.0, via Openverse");
    }

    [Fact]
    public void A_description_over_5000_characters_cuts_the_scripts_text_and_keeps_the_chapters_and_credits_whole()
    {
        var longText = string.Concat(Enumerable.Repeat("Rivers carve. ", 400));

        var description = PackageChecks.Description(Script with { Description = longText }, StockCuts());

        description.Length.ShouldBeLessThanOrEqualTo(5000);
        description.ShouldStartWith("Rivers carve. Rivers carve.");
        description.ShouldEndWith(
            "\n\n0:00 Erosion\n0:26 Floods\n0:45 Oxbows\n\nCredits\n\"Outer bank\" by Ana is licensed under CC BY 2.0.\nFlood plain by Bo, CC0 1.0, via Openverse");
        PackageChecks.Metadata("Why Rivers Bend", description, ["rivers"]).Result.ShouldBe("pass");
    }

    [Fact]
    public void The_Tags_Are_Kept_In_Order_While_They_Fit_YouTubes_500_Characters_Counting_Quotes_And_Commas()
    {
        PackageChecks.TagsLength(["rivers", "river bends"]).ShouldBe(6 + 13 + 1);
        var tags = PackageChecks.Tags(Enumerable.Range(1, 12).Select(i => $"tag{i:00}{new string('x', 40)}").Append("short"));

        tags.Count.ShouldBe(11);
        tags[^1].ShouldBe("short");
        PackageChecks.TagsLength(tags).ShouldBeLessThanOrEqualTo(500);
        PackageChecks.Tags(["a,b", "  ", "c\nd", new string('x', 101)]).ShouldBe(["ab", "c d"]);
    }

    [Fact]
    public void Every_Check_Is_Run_In_Order_Over_The_Video_And_The_Metadata()
    {
        var video = new AssembledVideo("video.mp4", 90_000, Cuts(), []);

        PackageChecks.Run(video, -14.21, "Why Rivers Bend", PackageChecks.Description(Script, Cuts()), ["rivers"], "rivers", Given.Mandate())
            .Select(c => (c.Name, c.Result))
            .ShouldBe([("duration", "pass"), ("loudness", "pass"), ("captions", "pass"), ("placeholders", "pass"), ("metadata", "pass"), ("discoverability", "pass")]);
    }
}
