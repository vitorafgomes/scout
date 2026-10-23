using Scout.ControlPlane.Features;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Features;

public sealed class FeatureCatalogTests
{
    private static readonly FeatureMetadata GitHub = new FeatureCatalog().Find(FeatureCatalog.GitHubId)!;

    private static IEnumerable<string> AllText(FeatureMetadata feature)
        => new[] { feature.Description }
            .Concat(feature.CredentialFields.SelectMany(f => new[] { f.Label, f.HelperText, f.CreateLinkUrl, f.CreateLinkLabel }))
            .Concat(feature.ScopeMatrix!.SelectMany(r => r.Actions.Prepend(r.Scope)))
            .Concat(feature.SetupSteps!)
            .OfType<string>();

    [Fact]
    public void GitHub_Lists_Only_The_Read_Only_App_Permissions()
    {
        GitHub.ScopeMatrix!.Select(r => r.Scope)
            .ShouldBe(["Metadata: read", "Contents: read", "Pull requests: read", "Issues: read"]);
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
}
