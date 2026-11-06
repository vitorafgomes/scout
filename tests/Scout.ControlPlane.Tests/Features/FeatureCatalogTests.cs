using Scout.ControlPlane.Features;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Features;

public sealed class FeatureCatalogTests
{
    private static readonly FeatureMetadata GitHub = new FeatureCatalog().Find(FeatureCatalog.GitHubId)!;
    private static readonly FeatureMetadata Kubernetes = new FeatureCatalog().Find(FeatureCatalog.KubernetesId)!;

    private static IEnumerable<string> AllText(FeatureMetadata feature)
        => new[] { feature.Description }
            .Concat(feature.CredentialFields.SelectMany(f => new[] { f.Label, f.HelperText, f.CreateLinkUrl, f.CreateLinkLabel }))
            .Concat((feature.ScopeMatrix ?? []).SelectMany(r => r.Actions.Prepend(r.Scope)))
            .Concat(feature.SetupSteps ?? [])
            .OfType<string>();

    [Fact]
    public void GitHub_Declares_The_Four_App_Permissions_As_Reads_And_The_Profile_The_Review_And_The_Issue_As_Writes_Under_The_Grant()
    {
        GitHub.ScopeMatrix!.Select(r => (r.Scope, r.Access))
            .ShouldBe([
                ("Metadata", ScopeAccess.Read),
                ("Contents", ScopeAccess.Read),
                ("Pull requests", ScopeAccess.Read),
                ("Issues", ScopeAccess.Read),
                ("Scout's data folder", ScopeAccess.Write),
                ("Pull request reviews", ScopeAccess.Write),
                ("New issues", ScopeAccess.Write),
            ]);
        GitHub.WriteClass.ShouldBe(WriteClass.Executive);
        GitHub.HasWriteRows.ShouldBeTrue();
    }

    [Fact]
    public void GitHub_Read_Rows_Name_Every_Read_The_Plugin_Performs()
    {
        GitHub.ScopeMatrix!.Single(r => r.Scope == "Contents").Actions.ShouldBe([
            "Read the README, languages and top-level files of a repository",
            "Read one file or list one directory of a repository, at a branch, tag or commit, for a code review",
            "List every file of a repository and search its code, to audit a whole project",
        ]);
        GitHub.ScopeMatrix!.Single(r => r.Scope == "Scout's data folder").Actions.ShouldHaveSingleItem().ShouldContain("conventions profile");
        GitHub.ScopeMatrix!.Single(r => r.Scope == "Pull requests").Actions.ShouldBe([
            "List open pull requests",
            "Read one pull request with its changed files and their diffs",
            "Read the comments on a pull request",
        ]);
        GitHub.ScopeMatrix!.Single(r => r.Scope == "Issues").Actions.ShouldBe(["List open issues", "Read one issue"]);
        GitHub.ScopeMatrix!.Single(r => r.Scope == "Pull request reviews").Actions.Single().ShouldContain("never approves");
    }

    [Fact]
    public void GitHub_Text_Promises_No_Keyring_No_Repo_Scope_And_Says_Writes_Run_When_Asked_In_Chat_Under_The_Grant()
    {
        AllText(GitHub).Count().ShouldBeGreaterThan(8);
        foreach (var text in AllText(GitHub))
        {
            text.ShouldNotContain("keyring", Case.Insensitive);
            text.ShouldNotContain("scopes=repo", Case.Insensitive);
            text.ShouldNotContain("'repo'", Case.Insensitive);
            text.ShouldNotContain("never writes to GitHub", Case.Insensitive);
        }

        GitHub.Description.ShouldContain("when you ask for it in chat");
        GitHub.SetupSteps!.ShouldContain(step => step.Contains("only when you ask for it in chat"));
        foreach (var text in AllText(GitHub))
        {
            text.ShouldNotContain("Review screen");
        }
    }

    [Fact]
    public void GitHub_Setup_Steps_Walk_Through_Connect_With_GitHub()
    {
        GitHub.SetupSteps![0].ShouldContain("Connect with GitHub");
        GitHub.SetupSteps!.ShouldContain(step => step.Contains("Open GitHub") && step.Contains("code"));
    }

    [Fact]
    public void GitHub_Keeps_The_Token_Field_For_Hosts_Without_The_Desktop_Sign_In()
    {
        GitHub.CredentialFields.ShouldHaveSingleItem().Key.ShouldBe("pat");
    }

    [Fact]
    public void Kubernetes_Uses_The_Configuration_On_This_Computer_With_Consent_And_Read_Only()
    {
        Kubernetes.Description.ShouldContain("Kubernetes configuration on this computer");
        Kubernetes.Description.ShouldContain("with your consent");
        Kubernetes.Description.ShouldContain("read-only");
        Kubernetes.WriteClass.ShouldBe(WriteClass.None);
    }

    [Fact]
    public void Kubernetes_Text_Promises_No_Keyring_And_Drops_The_Paste_Instructions()
    {
        AllText(Kubernetes).Count().ShouldBeGreaterThan(5);
        foreach (var text in AllText(Kubernetes))
        {
            text.ShouldNotContain("keyring", Case.Insensitive);
            text.ShouldNotContain("not supported", Case.Insensitive);
            text.ShouldNotContain("--flatten", Case.Insensitive);
        }
    }

    [Fact]
    public void Kubernetes_Asks_For_No_Credentials_Because_The_Desktop_Card_Uses_The_Local_Kubeconfig()
    {
        Kubernetes.CredentialFields.ShouldBeEmpty();
        Kubernetes.RequiresCredentials.ShouldBeFalse();
    }

    [Fact]
    public void The_YouTube_Feature_Declares_Its_Read_Write_And_Irreversible_Rows()
    {
        var youTube = new FeatureCatalog().Find(FeatureCatalog.YouTubeId).ShouldNotBeNull();

        youTube.DisplayName.ShouldBe("YouTube");
        youTube.WriteClass.ShouldBe(WriteClass.Executive);
        youTube.HasWriteRows.ShouldBeTrue();
        youTube.ScopeMatrix.ShouldNotBeNull().Select(r => (r.Scope, r.Access))
            .ShouldBe([
                ("channels.list", ScopeAccess.Read),
                ("playlistItems.list", ScopeAccess.Read),
                ("videos.list", ScopeAccess.Read),
                ("search.list", ScopeAccess.Read),
                ("commentThreads.list", ScopeAccess.Read),
                ("youtubeAnalytics reports.query", ScopeAccess.Read),
                ("youtubeReporting reports.list", ScopeAccess.Read),
                ("api.openverse.org and the host of the picture it names", ScopeAccess.Read),
                ("Scout's data folder", ScopeAccess.Write),
                ("youtubeReporting jobs.create", ScopeAccess.Write),
                ("videos.insert", ScopeAccess.IrreversibleWrite),
                ("videos.update", ScopeAccess.IrreversibleWrite),
                ("thumbnails.set", ScopeAccess.IrreversibleWrite),
                ("playlistItems.insert", ScopeAccess.IrreversibleWrite),
                ("comments.insert", ScopeAccess.IrreversibleWrite),
                ("comments.setModerationStatus", ScopeAccess.IrreversibleWrite),
            ]);
        youTube.ScopeMatrix!.Where(r => r.Access == ScopeAccess.Write).Select(r => r.Actions.Single())
            .ShouldBe([
                "Save scripts, narration, images, the scene list, the video and thumbnails, and keep the recording you attach and its transcript",
                "Create the channel's reach reporting job once, so YouTube prepares the reach reports",
            ]);
        youTube.ScopeMatrix!.Single(r => r.Scope == "videos.insert").Actions
            .ShouldBe(["Upload a video as public, private or unlisted, or as private with a time YouTube publishes it"]);
        youTube.Description.ShouldBe(
            "Reads a channel's overview, uploads, statistics, search results and comments; your own channel after " +
            "you sign in from the desktop. With your write permission it also produces videos under Scout's " +
            "folder and, one action at a time with your approval, uploads, schedules and tests titles and " +
            "thumbnails. Replying to and moderating comments are not implemented yet.");
    }

    [Fact]
    public void YouTube_Asks_Only_For_An_Api_Key_And_Explains_Where_To_Create_It()
    {
        var youTube = new FeatureCatalog().Find(FeatureCatalog.YouTubeId).ShouldNotBeNull();

        youTube.RequiresCredentials.ShouldBeTrue();
        var field = youTube.CredentialFields.ShouldHaveSingleItem();
        field.Key.ShouldBe("api_key");
        field.Kind.ShouldBe("password");
        field.Label.ShouldBe("YouTube Data API key");
        field.HelperText.ShouldNotBeNull().ShouldContain("Google Cloud Console");
        youTube.SettingsFields.ShouldBeEmpty();
        youTube.SetupSteps.ShouldNotBeNull().Count.ShouldBe(3);
        youTube.SetupSteps!.ShouldContain(step => step.Contains("YouTube Data API v3"));
        youTube.SetupSteps!.ShouldContain(step => step.Contains("sign in") && step.Contains("Scout app"));
    }

    [Fact]
    public void The_desktop_feature_declares_screen_microphone_and_system_audio_reads_and_no_write()
    {
        var desktop = new FeatureCatalog().Find(FeatureCatalog.DesktopId).ShouldNotBeNull();

        desktop.Id.ShouldBe("desktop");
        desktop.DisplayName.ShouldBe("Desktop");
        desktop.Description.ShouldBe(
            "What Scout can see and hear on this computer when you ask from the desktop: a screenshot you attach " +
            "to a question, your microphone while you record or listen, and the system audio while you keep Others on. " +
            "Nothing is captured unless you click.");
        desktop.RequiresCredentials.ShouldBeFalse();
        desktop.CredentialFields.ShouldBeEmpty();
        desktop.SettingsFields.ShouldBeEmpty();
        desktop.WriteClass.ShouldBe(WriteClass.None);
        desktop.HasWriteRows.ShouldBeFalse();
        desktop.ScopeMatrix.ShouldNotBeNull().Select(r => (r.Scope, string.Join(" | ", r.Actions), r.Access))
            .ShouldBe([
                ("Screen", "Send one screenshot with a question when you attach it | Read the text on a screenshot with the bundled OCR program", ScopeAccess.Read),
                ("Microphone", "Record a voice question | Listen while you speak", ScopeAccess.Read),
                ("System audio", "Listen to what this computer plays while you keep Others on", ScopeAccess.Read),
            ]);
    }

    [Fact]
    public void The_catalog_lists_github_kubernetes_youtube_and_desktop_in_that_order()
    {
        new FeatureCatalog().All.Select(f => f.Id).ShouldBe(["github", "kubernetes", "youtube", "desktop"]);
    }

    [Fact]
    public void A_Write_Row_Under_WriteClass_None_Fails_The_Catalog_Check()
    {
        var undeclared = new FeatureMetadata(
            Id: "sneaky",
            DisplayName: "Sneaky",
            Description: "Claims to read.",
            RequiresCredentials: false,
            CredentialFields: [],
            SettingsFields: [],
            WriteClass: WriteClass.None,
            ScopeMatrix: [new FeatureScopeRow("videos.insert", ["Upload a video"], ScopeAccess.IrreversibleWrite)]);
        var reversible = undeclared with
        {
            Id = "drafty",
            ScopeMatrix = [new FeatureScopeRow("Scripts", ["Save a script"], ScopeAccess.Write)],
        };

        var ex = Should.Throw<InvalidOperationException>(() =>
            FeatureCatalog.Validate(new FeatureCatalog().All.Append(undeclared).Append(reversible)));

        ex.Message.ShouldBe("Features with a write row must declare a write class other than None: sneaky, drafty.");
        Should.NotThrow(() => FeatureCatalog.Validate(new FeatureCatalog().All));
    }
}
