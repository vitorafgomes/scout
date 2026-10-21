using Scout.ControlPlane.Contracts;
using Scout.ControlPlane.Features;
using WireCredentialField = Scout.ControlPlane.Contracts.FeatureCredentialField;
using WireScopeRow = Scout.ControlPlane.Contracts.FeatureScopeRow;
using WireSettingField = Scout.ControlPlane.Contracts.FeatureSettingField;

namespace Scout.Host.ControlPlane.Services;

/// <summary>Maps feature domain snapshots to the wire <c>Feature</c> message.</summary>
internal static class FeatureMapping
{
    public static Feature ToWire(FeatureSnapshot snapshot)
    {
        var feature = new Feature
        {
            Id = snapshot.Id,
            DisplayName = snapshot.DisplayName,
            Description = snapshot.Description,
            RequiresCredentials = snapshot.RequiresCredentials,
            HasCredentials = snapshot.HasCredentials,
            Enabled = snapshot.Enabled,
            Status = ToWireStatus(snapshot.Status),
            HasScopeMatrix = snapshot.ScopeMatrix is not null,
            HasSetupSteps = snapshot.SetupSteps is not null,
        };

        if (snapshot.LastError is not null)
        {
            feature.LastError = snapshot.LastError;
        }

        foreach (var field in snapshot.CredentialFields)
        {
            feature.CredentialFields.Add(ToWire(field));
        }
        foreach (var field in snapshot.SettingsFields)
        {
            feature.SettingsFields.Add(ToWire(field));
        }
        foreach (var (key, value) in snapshot.Settings)
        {
            // Map drops nulls: a cleared setting is simply absent from the map.
            if (value is not null)
            {
                feature.Settings[key] = value;
            }
        }
        if (snapshot.ScopeMatrix is not null)
        {
            foreach (var row in snapshot.ScopeMatrix)
            {
                var wireRow = new WireScopeRow { Scope = row.Scope };
                wireRow.Actions.AddRange(row.Actions);
                feature.ScopeMatrix.Add(wireRow);
            }
        }
        if (snapshot.SetupSteps is not null)
        {
            feature.SetupSteps.AddRange(snapshot.SetupSteps);
        }

        return feature;
    }

    private static FeatureStatus ToWireStatus(FeatureStatusKind kind) => kind switch
    {
        FeatureStatusKind.Enabled => FeatureStatus.Enabled,
        FeatureStatusKind.NeedsCredentials => FeatureStatus.NeedsCredentials,
        FeatureStatusKind.Disabled => FeatureStatus.Disabled,
        FeatureStatusKind.Error => FeatureStatus.Error,
        _ => FeatureStatus.Unspecified,
    };

    private static WireCredentialField ToWire(Scout.ControlPlane.Features.FeatureCredentialField field)
    {
        var wire = new WireCredentialField
        {
            Key = field.Key,
            Kind = field.Kind,
            Label = field.Label,
        };
        if (field.HelperText is not null)
        {
            wire.HelperText = field.HelperText;
        }
        if (field.CreateLinkUrl is not null)
        {
            wire.CreateLinkUrl = field.CreateLinkUrl;
        }
        if (field.CreateLinkLabel is not null)
        {
            wire.CreateLinkLabel = field.CreateLinkLabel;
        }
        return wire;
    }

    private static WireSettingField ToWire(Scout.ControlPlane.Features.FeatureSettingField field)
    {
        var wire = new WireSettingField
        {
            Key = field.Key,
            Kind = field.Kind,
            Label = field.Label,
            Required = field.Required,
        };
        if (field.HelperText is not null)
        {
            wire.HelperText = field.HelperText;
        }
        if (field.CreateLinkUrl is not null)
        {
            wire.CreateLinkUrl = field.CreateLinkUrl;
        }
        if (field.CreateLinkLabel is not null)
        {
            wire.CreateLinkLabel = field.CreateLinkLabel;
        }
        return wire;
    }
}
