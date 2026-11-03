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
    public void GitHub_Lists_Only_The_Read_Only_App_Permissions()
    {
        GitHub.ScopeMatrix!.Select(r => (r.Scope, r.Access))
            .ShouldBe([
                ("Metadata", ScopeAccess.Read),
                ("Contents", ScopeAccess.Read),
                ("Pull requests", ScopeAccess.Read),
                ("Issues", ScopeAccess.Read),
            ]);
        GitHub.WriteClass.ShouldBe(WriteClass.None);
    }

    [Fact]
    public void GitHub_Text_Promises_No_Keyring_No_Repo_Scope_And_No_Issue_Creation()
    {
        AllText(GitHub).Count().ShouldBeGreaterThan(8);
        foreach (var text in AllText(GitHub))
        {
            text.ShouldNotContain("keyring", Case.Insensitive);
            text.ShouldNotContain("Create issues", Case.Insensitive);
            text.ShouldNotContain("scopes=repo", Case.Insensitive);
            text.ShouldNotContain("'repo'", Case.Insensitive);
        }
    }

    [Fact]
    public void GitHub_Setup_Steps_Walk_Through_Connect_With_GitHub()
    {
        GitHub.SetupSteps!.First().ShouldContain("Connect with GitHub");
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
                "Save scripts, narration, images, the scene list, the video and thumbnails",
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
