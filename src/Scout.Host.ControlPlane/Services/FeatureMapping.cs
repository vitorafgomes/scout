using System.Globalization;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Contracts;
using Scout.ControlPlane.Features;
using DomainPendingAction = Scout.ControlPlane.Actions.PendingAction;
using DomainWriteLogEntry = Scout.ControlPlane.Actions.WriteLogEntry;
using WireCredentialField = Scout.ControlPlane.Contracts.FeatureCredentialField;
using WirePendingAction = Scout.ControlPlane.Contracts.PendingAction;
using WireScopeRow = Scout.ControlPlane.Contracts.FeatureScopeRow;
using WireSettingField = Scout.ControlPlane.Contracts.FeatureSettingField;
using WireWriteLogEntry = Scout.ControlPlane.Contracts.WriteLogEntry;

namespace Scout.Host.ControlPlane.Services;

/// <summary>Maps feature snapshots, queued actions and write-log entries to their wire messages.</summary>
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
            WriteClass = (int)snapshot.WriteClass,
            Granted = snapshot.Granted,
        };

        if (snapshot.LastError is not null)
        {
            feature.LastError = snapshot.LastError;
        }
        if (snapshot.GrantedAt is { } grantedAt)
        {
            feature.GrantedAt = Iso(grantedAt);
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
                var wireRow = new WireScopeRow { Scope = row.Scope, Access = (int)row.Access };
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

    public static WirePendingAction ToWire(DomainPendingAction action)
    {
        var wire = new WirePendingAction
        {
            Id = action.Id,
            FeatureId = action.FeatureId,
            Action = action.Action,
            Target = action.Target,
            Summary = action.Summary,
            RequestedAt = Iso(action.RequestedAt),
            Status = action.Status switch
            {
                PendingActionStatus.Pending => "pending",
                PendingActionStatus.Approved => "approved",
                PendingActionStatus.Rejected => "rejected",
                PendingActionStatus.Executed => "executed",
                _ => throw new ArgumentOutOfRangeException(nameof(action), action.Status, null),
            },
        };
        if (action.DecidedAt is { } decidedAt)
        {
            wire.DecidedAt = Iso(decidedAt);
        }
        if (action.Reason is not null)
        {
            wire.Reason = action.Reason;
        }
        return wire;
    }

    public static WireWriteLogEntry ToWire(DomainWriteLogEntry entry)
    {
        var wire = new WireWriteLogEntry
        {
            At = Iso(entry.At),
            FeatureId = entry.FeatureId,
            Action = entry.Action,
            Target = entry.Target,
            Outcome = entry.Outcome,
        };
        if (entry.ApprovedBy is not null)
        {
            wire.ApprovedBy = entry.ApprovedBy;
        }
        if (entry.ActionId is not null)
        {
            wire.ActionId = entry.ActionId;
        }
        return wire;
    }

    private static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

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
