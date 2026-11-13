using Scout.ControlPlane.Contracts;
using Scout.ControlPlane.Features;
using Scout.Host.ControlPlane.Services;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Features;

public sealed class FeatureMappingStatusTests
{
    [Fact]
    public void An_Unknown_Status_Throws_Instead_Of_Reaching_The_Wire_As_Unspecified()
    {
        var unknown = (FeatureStatusKind)99;

        var ex = Should.Throw<ArgumentOutOfRangeException>(() => FeatureMapping.ToWire(Snapshot(unknown)));

        ex.ParamName.ShouldBe("kind");
        ex.ActualValue.ShouldBe(unknown);
    }

    [Theory]
    [InlineData(FeatureStatusKind.Enabled, FeatureStatus.Enabled)]
    [InlineData(FeatureStatusKind.NeedsCredentials, FeatureStatus.NeedsCredentials)]
    [InlineData(FeatureStatusKind.Disabled, FeatureStatus.Disabled)]
    [InlineData(FeatureStatusKind.Error, FeatureStatus.Error)]
    public void Every_Known_Status_Reaches_The_Wire_As_Itself(FeatureStatusKind kind, FeatureStatus wire)
    {
        FeatureMapping.ToWire(Snapshot(kind)).Status.ShouldBe(wire);
    }

    private static FeatureSnapshot Snapshot(FeatureStatusKind status) => new(
        "probe",
        "Probe",
        "A feature for the status mapping.",
        RequiresCredentials: false,
        HasCredentials: false,
        Enabled: false,
        status,
        LastError: null,
        CredentialFields: [],
        SettingsFields: [],
        Settings: new Dictionary<string, string?>(),
        ScopeMatrix: null,
        SetupSteps: null,
        WriteClass.None,
        Granted: false,
        GrantedAt: null);
}
