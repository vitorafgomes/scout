using Scout.ControlPlane.Features;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Features;

public sealed class FeatureCatalogTests
{
    private static readonly FeatureMetadata GitHub = new FeatureCatalog().Find(FeatureCatalog.GitHubId)!;
    private static readonly FeatureMetadata Kubernetes = new FeatureCatalog().Find(FeatureCatalog.KubernetesId)!;
    private static readonly FeatureMetadata YouTube = new FeatureCatalog().Find(FeatureCatalog.YouTubeId)!;
    private static readonly FeatureMetadata Desktop = new FeatureCatalog().Find(FeatureCatalog.DesktopId)!;

    private static IEnumerable<(string Scope, ScopeAccess Access, string Summary)> Shown(FeatureMetadata feature)
        => feature.ScopeMatrix.ShouldNotBeNull().Select(r => (r.Scope, r.Access, r.Summary));

    private static IEnumerable<string> AllText(FeatureMetadata feature)
        => new[] { feature.Description }
            .Concat(feature.CredentialFields.SelectMany(f => new[] { f.Label, f.HelperText, f.CreateLinkUrl, f.CreateLinkLabel }))
            .Concat((feature.ScopeMatrix ?? []).SelectMany(r => r.Actions.Prepend(r.Scope)))
            .Concat(feature.SetupSteps ?? [])
            .OfType<string>();

    [Fact]
    public void GitHub_Declares_The_Four_App_Permissions_As_Reads_The_Profile_As_A_Write_Under_The_Grant_And_The_Review_The_Verdict_The_Comments_And_The_Issue_As_Irreversible()
    {
        GitHub.ScopeMatrix!.Select(r => (r.Scope, r.Access))
            .ShouldBe([
                ("Metadata", ScopeAccess.Read),
                ("Contents", ScopeAccess.Read),
                ("Pull requests", ScopeAccess.Read),
                ("Issues", ScopeAccess.Read),
                ("Notifications", ScopeAccess.Read),
                ("Scout's data folder", ScopeAccess.Write),
                ("Pull request reviews", ScopeAccess.IrreversibleWrite),
                ("Pull request verdicts", ScopeAccess.IrreversibleWrite),
                ("Pull request comments", ScopeAccess.IrreversibleWrite),
                ("New issues", ScopeAccess.IrreversibleWrite),
                ("Issue comments", ScopeAccess.IrreversibleWrite),
                ("Local container", ScopeAccess.Write),
                ("Scout bot on GitHub (optional)", ScopeAccess.Write),
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
        GitHub.ScopeMatrix!.Single(r => r.Scope == "Scout's data folder").Actions.Select(a => a.Split(' ')[2]).ShouldBe(["conventions", "snapshot", "audit"]);
        GitHub.ScopeMatrix!.Single(r => r.Scope == "Pull requests").Actions.ShouldBe([
            "List open pull requests",
            "Read one pull request with its changed files and their diffs",
            "Read the comments on a pull request",
            "Read which account Scout signs in as, so it never approves a pull request that account opened",
            "Read the check runs and commit statuses of a pull request's head commit, and the reviews already given on it",
        ]);
        GitHub.ScopeMatrix!.Single(r => r.Scope == "Issues").Actions.ShouldBe(["List open issues", "Read one issue"]);
        GitHub.ScopeMatrix!.Single(r => r.Scope == "Notifications").Actions.ShouldBe([
            "Search your GitHub activity every five minutes for the bell: mentions, review requests and replies on your pull requests and issues",
            "When your Scout bot is installed, search the open pull requests on its account for a mention of the bot, and read the description, the comments, the review comments and the reviews of each pull request found, to confirm the mention before the bell shows it",
            "When your Scout bot exists, read its GitHub App's own name and handle at start and whenever the GitHub card reads the bot, so a rename on GitHub is followed",
        ]);
        GitHub.ScopeMatrix!.Single(r => r.Scope == "Pull request reviews").Actions.Single().ShouldContain("never approves");
        GitHub.ScopeMatrix!.Single(r => r.Scope == "Scout bot on GitHub (optional)").Actions.ShouldBe([
            "Sign the reviews, verdicts, comments and issues you switched to the bot with its token, on the repositories you installed it on; on any other repository Scout signs as you and says so, and every read still uses your sign-in",
            "Creating the bot is your own action in the desktop, like connecting GitHub: you confirm the GitHub App on GitHub's page, and its private key stays in this computer's protected storage",
        ]);
    }

    [Fact]
    public void GitHub_Shows_The_Artboards_Description_And_One_Short_Sentence_Per_Row_With_The_Notifications_Read()
    {
        GitHub.Description.ShouldBe(
            "Query repositories, issues and pull requests from chat; review a pull request and audit a whole project. " +
            "With your write permission it also posts reviews and comments, opens issues and gives verdicts, each one only after you approve it.");
        Shown(GitHub).ShouldBe([
            ("Metadata", ScopeAccess.Read, "List the repositories the Scout GitHub App is installed on"),
            ("Contents", ScopeAccess.Read, "Read files, directories and languages; list and search every file to audit a project"),
            ("Pull requests", ScopeAccess.Read, "List and read pull requests, their diffs, comments, checks and reviews, and the account Scout signs in as"),
            ("Issues", ScopeAccess.Read, "List open issues and read one issue"),
            ("Notifications", ScopeAccess.Read, "Search every five minutes for your mentions, review requests and replies; read the pull requests that mention your Scout bot and the bot's own name"),
            ("Scout's data folder", ScopeAccess.Write, "Keep conventions profiles, repository snapshots and audit jobs on this computer"),
            ("Pull request reviews", ScopeAccess.IrreversibleWrite, "Post a review with comments; your click on Post on a review card is the approval, and one asked in chat waits for you in Approvals"),
            ("Pull request verdicts", ScopeAccess.IrreversibleWrite, "Approve a pull request or request changes; your choice on a review card is the approval, and one asked in chat waits for you in Approvals"),
            ("Pull request comments", ScopeAccess.IrreversibleWrite, "Comment on a pull request or reply to a review comment; each one waits for you in Approvals"),
            ("New issues", ScopeAccess.IrreversibleWrite, "Open an issue; your click on Open issues on a findings card is the approval, and one asked in chat waits for you in Approvals"),
            ("Issue comments", ScopeAccess.IrreversibleWrite, "Comment on an existing issue; each one waits for you in Approvals"),
            ("Local container", ScopeAccess.Write, "Run a pull request's tests in a disposable container on this computer"),
            ("Scout bot on GitHub (optional)", ScopeAccess.Write, "Sign what you switched to the bot with its token, on the repositories you installed it on; you create the bot on GitHub's page and its key stays on this computer"),
        ]);
    }

    [Fact]
    public void GitHub_Write_Rows_Keep_Every_Write_Action_With_Its_Own_Sentence_For_The_Allow_Writes_Confirmation()
    {
        GitHub.ScopeMatrix!.Where(r => r.Access != ScopeAccess.Read).Select(r => (r.Scope, string.Join(" | ", r.Actions)))
            .ShouldBe([
                ("Scout's data folder",
                    "Keep the conventions profile of a repository Scout audited, so later audits judge its code against its own rules | " +
                    "Keep a snapshot of a repository at one commit, within a size cap (200 MB unless configured), so a review or an audit reads every file locally and the Knowledge index can search it | " +
                    "Keep the audit job of a repository, its plan, progress and findings, when you ask for a whole-repository audit in chat; the audit reads the snapshot and pauses or stops when you ask"),
                ("Pull request reviews",
                    "Post a review with comments on a pull request; this review only comments, it never approves or requests changes. A review notifies the pull request's author and everyone following it, so each one needs your approval in Scout Desktop: your click on Post on a review card is that approval, and a review asked for in chat waits for you to approve it"),
                ("Pull request verdicts",
                    "Approve a pull request or request changes on it when you choose that verdict on the review card; a verdict stays in the pull request's history, so each one runs only after you approve that action in Scout Desktop | Approve a pull request or request changes on it when you ask for it in chat; each verdict waits for you to approve it in Scout Desktop"),
                ("Pull request comments",
                    "Comment on a pull request's conversation when you ask for it in chat; a comment notifies everyone following the pull request, so each one waits for you to approve that comment in Scout Desktop | " +
                    "Reply to a review comment on a pull request when you ask for it in chat; each reply waits for you to approve it in Scout Desktop"),
                ("New issues",
                    "Open an issue on a repository when you ask for it in chat; an issue notifies the repository's watchers and stays in its history, so each one waits for you to approve that issue in Scout Desktop | Open the issues a findings card lists when you click Open issues on it; that click is your approval of exactly those issues"),
                ("Issue comments",
                    "Comment on an existing issue when you ask for it in chat; a comment notifies everyone following the issue, so each one waits for you to approve that comment in Scout Desktop"),
                ("Local container",
                    "Run a pull request's own tests in a disposable container on this computer, over the snapshot in Scout's data folder; the container sees no file, secret or variable of yours and, during the tests, no network"),
                ("Scout bot on GitHub (optional)",
                    "Sign the reviews, verdicts, comments and issues you switched to the bot with its token, on the repositories you installed it on; on any other repository Scout signs as you and says so, and every read still uses your sign-in | " +
                    "Creating the bot is your own action in the desktop, like connecting GitHub: you confirm the GitHub App on GitHub's page, and its private key stays in this computer's protected storage"),
            ]);
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
    public void GitHub_Token_Helper_Asks_For_No_Notifications_Permission_Because_The_Bell_Searches()
    {
        GitHub.CredentialFields.Single().HelperText.ShouldNotBeNull().ShouldNotContain("Notifications");
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
    public void Kubernetes_Declares_Namespaces_Pods_Deployments_And_Events_As_Its_Four_Reads_With_The_Artboards_Words_And_No_Write()
    {
        Kubernetes.ScopeMatrix.ShouldNotBeNull().Select(r => (r.Scope, string.Join(" | ", r.Actions), r.Access))
            .ShouldBe([
                ("Namespaces", "List the namespaces of each context you chose", ScopeAccess.Read),
                ("Pods", "Read pod status and restarts; never logs or secrets", ScopeAccess.Read),
                ("Deployments", "Read replicas and rollout status", ScopeAccess.Read),
                ("Events", "Read recent events", ScopeAccess.Read),
            ]);
        Kubernetes.WriteClass.ShouldBe(WriteClass.None);
        Kubernetes.HasWriteRows.ShouldBeFalse();
    }

    [Fact]
    public void Kubernetes_Shows_Each_Of_Its_Four_Reads_In_One_Sentence_As_The_Artboard_Draws_It()
    {
        Shown(Kubernetes).ShouldBe([
            ("Namespaces", ScopeAccess.Read, "List the namespaces of each context you chose"),
            ("Pods", ScopeAccess.Read, "Read pod status and restarts; never logs or secrets"),
            ("Deployments", ScopeAccess.Read, "Read replicas and rollout status"),
            ("Events", ScopeAccess.Read, "Read recent events"),
        ]);
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
        YouTube.DisplayName.ShouldBe("YouTube");
        YouTube.WriteClass.ShouldBe(WriteClass.Executive);
        YouTube.HasWriteRows.ShouldBeTrue();
        YouTube.ScopeMatrix.ShouldNotBeNull().Select(r => (r.Scope, r.Access))
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
    }

    [Fact]
    public void YouTube_Shows_The_Artboards_Description_And_One_Short_Sentence_Per_Row()
    {
        YouTube.Description.ShouldBe(
            "Reads a channel's overview, uploads, statistics, search results and comments; your own channel after you sign in. " +
            "With your write permission it also produces videos under Scout's folder and publishes what you approve.");
        Shown(YouTube).ShouldBe([
            ("channels.list", ScopeAccess.Read, "Title, handle, subscribers, total views and video count"),
            ("playlistItems.list", ScopeAccess.Read, "The latest uploads of a channel"),
            ("videos.list", ScopeAccess.Read, "Title, duration, views, likes and comment count"),
            ("search.list", ScopeAccess.Read, "Search YouTube for videos on a topic"),
            ("commentThreads.list", ScopeAccess.Read, "The comments on your videos"),
            ("youtubeAnalytics reports.query", ScopeAccess.Read, "Views, watch time, retention and subscribers gained"),
            ("youtubeReporting reports.list", ScopeAccess.Read, "Thumbnail impressions and click-through rate"),
            ("api.openverse.org and the host of the picture it names", ScopeAccess.Read, "Find a CC0, public domain or CC BY picture for a scene on Openverse and download it"),
            ("Scout's data folder", ScopeAccess.Write, "Save scripts, narration, images, the scene list, the video and thumbnails, and the recording you attach with its transcript"),
            ("youtubeReporting jobs.create", ScopeAccess.Write, "Create the channel's reach reporting job once"),
            ("videos.insert", ScopeAccess.IrreversibleWrite, "Public, private, unlisted or scheduled"),
            ("videos.update", ScopeAccess.IrreversibleWrite, "Change a video's title during a title test"),
            ("thumbnails.set", ScopeAccess.IrreversibleWrite, "Set a video's thumbnail"),
            ("playlistItems.insert", ScopeAccess.IrreversibleWrite, "Add a video to a playlist"),
            ("comments.insert", ScopeAccess.IrreversibleWrite, "Publish a reply to a comment"),
            ("comments.setModerationStatus", ScopeAccess.IrreversibleWrite, "Hold a comment for your review on YouTube"),
        ]);
    }

    [Fact]
    public void YouTube_Write_Rows_Keep_Every_Write_Action_With_Its_Own_Sentence_For_The_Allow_Writes_Confirmation()
    {
        YouTube.ScopeMatrix!.Where(r => r.Access != ScopeAccess.Read).Select(r => (r.Scope, string.Join(" | ", r.Actions)))
            .ShouldBe([
                ("Scout's data folder", "Save scripts, narration, images, the scene list, the video and thumbnails, and keep the recording you attach and its transcript"),
                ("youtubeReporting jobs.create", "Create the channel's reach reporting job once, so YouTube prepares the reach reports"),
                ("videos.insert", "Upload a video as public, private or unlisted, or as private with a time YouTube publishes it"),
                ("videos.update", "Change a video's title during a title test: to each title the test tries, and then to the title kept when it ends"),
                ("thumbnails.set", "Set a video's thumbnail"),
                ("playlistItems.insert", "Add a video to a playlist"),
                ("comments.insert", "Publish a reply to a comment"),
                ("comments.setModerationStatus", "Hold a comment for your review on YouTube; Scout never publishes or rejects a comment"),
            ]);
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
        Desktop.Id.ShouldBe("desktop");
        Desktop.DisplayName.ShouldBe("Desktop");
        Desktop.Description.ShouldBe(
            "What Scout can see and hear on this computer when you ask from the desktop. Nothing is captured unless you click.");
        Desktop.RequiresCredentials.ShouldBeFalse();
        Desktop.CredentialFields.ShouldBeEmpty();
        Desktop.SettingsFields.ShouldBeEmpty();
        Desktop.WriteClass.ShouldBe(WriteClass.None);
        Desktop.HasWriteRows.ShouldBeFalse();
        Desktop.ScopeMatrix.ShouldNotBeNull().Select(r => (r.Scope, string.Join(" | ", r.Actions), r.Access))
            .ShouldBe([
                ("Screen", "Send one screenshot with a question when you attach it | Read the text on a screenshot with the bundled OCR program", ScopeAccess.Read),
                ("Microphone", "Record a voice question | Listen while you speak | Listen while you record a meeting", ScopeAccess.Read),
                ("System audio", "Listen to what this computer plays while you keep System audio on | Listen to what this computer plays while you record a meeting", ScopeAccess.Read),
            ]);
    }

    [Fact]
    public void Desktop_Shows_One_Short_Sentence_Per_Read_As_The_Artboard_Draws_It()
    {
        Shown(Desktop).ShouldBe([
            ("Screen", ScopeAccess.Read, "One screenshot you attach to a question, and the text read from it"),
            ("Microphone", ScopeAccess.Read, "Your voice while you hold to talk, listen or record a meeting"),
            ("System audio", ScopeAccess.Read, "The computer's sound while you keep System audio on or record a meeting"),
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
            ScopeMatrix: [new FeatureScopeRow("videos.insert", "Upload a video", ["Upload a video"], ScopeAccess.IrreversibleWrite)]);
        var reversible = undeclared with
        {
            Id = "drafty",
            ScopeMatrix = [new FeatureScopeRow("Scripts", "Save a script", ["Save a script"], ScopeAccess.Write)],
        };

        var ex = Should.Throw<InvalidOperationException>(() =>
            FeatureCatalog.Validate(new FeatureCatalog().All.Append(undeclared).Append(reversible)));

        ex.Message.ShouldBe("Features with a write row must declare a write class other than None: sneaky, drafty.");
        Should.NotThrow(() => FeatureCatalog.Validate(new FeatureCatalog().All));
    }
}
