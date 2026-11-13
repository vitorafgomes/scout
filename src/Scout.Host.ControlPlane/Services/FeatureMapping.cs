using System.Globalization;
using System.Text;
using Scout.ControlPlane.Actions;
using Scout.ControlPlane.Contracts;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.WriteGate;
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
    /// <summary>
    /// The most UTF-8 bytes of listed titles and bodies one list or group answer carries, half the largest message, so the
    /// rest of every row has the other half; a row past it carries no title or body and says its text was cut.
    /// </summary>
    internal const int MaxListedTextBytes = ControlPlaneServiceRegistration.MaxMessageBytes / 2;

    /// <summary>A set decision on the wire: whether it decided, the group, the listed actions as they stand and the mismatches.</summary>
    public static ActionSetResponse ToWire(string groupId, ActionSetDecision decision)
    {
        var wire = new ActionSetResponse { Decided = decision.Decided, GroupId = groupId };
        foreach (var action in decision.Actions)
        {
            wire.Actions.Add(ToWire(action));
        }
        foreach (var mismatch in decision.Mismatches)
        {
            wire.Mismatches.Add(new ActionMismatch { Id = mismatch.Id, Reason = mismatch.Reason });
        }
        return wire;
    }

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
                var wireRow = new WireScopeRow { Scope = row.Scope, Summary = row.Summary, Access = (int)row.Access };
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

    /// <summary>
    /// The action on the wire with its group and repository; a waiting one also with the title and body of its payload,
    /// whether either was cut, the payload's digest and <paramref name="groupState"/>, which a decided one carries only with
    /// <paramref name="anyStatus"/>, and then never the digest.
    /// </summary>
    public static WirePendingAction ToWire(DomainPendingAction action, string? groupState = null, bool anyStatus = false)
    {
        var unbounded = int.MaxValue;
        return ToWire(action, groupState, anyStatus, ref unbounded);
    }

    /// <summary>
    /// The listed actions on the wire, in order, as <see cref="ToWire(DomainPendingAction, string?, bool)"/> maps each, with
    /// the listed text of each until one would take the text past <see cref="MaxListedTextBytes"/>; that action and every
    /// one after it carry no title or body and, when their payload holds either, <c>text_clipped</c>.
    /// </summary>
    public static IEnumerable<WirePendingAction> ToWire(IEnumerable<(DomainPendingAction Action, string? GroupState)> listed, bool anyStatus = false)
    {
        var budget = MaxListedTextBytes;
        var wire = new List<WirePendingAction>();
        foreach (var (action, groupState) in listed)
        {
            wire.Add(ToWire(action, groupState, anyStatus, ref budget));
        }
        return wire;
    }

    private static WirePendingAction ToWire(DomainPendingAction action, string? groupState, bool anyStatus, ref int budget)
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
                PendingActionStatus.Executing => "executing",
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
        if (action.Outcome is not null)
        {
            wire.Outcome = action.Outcome;
        }
        if (action.Result is not null)
        {
            wire.Result = action.Result;
        }
        if (action.GroupId is not null)
        {
            wire.GroupId = action.GroupId;
        }
        if (action.Repository is not null)
        {
            wire.Repository = action.Repository;
        }
        var waiting = action.Status == PendingActionStatus.Pending;
        if (!waiting && !anyStatus)
        {
            return wire;
        }

        var (title, body, clipped) = action.ListedText();
        var size = (title is null ? 0 : Encoding.UTF8.GetByteCount(title)) + (body is null ? 0 : Encoding.UTF8.GetByteCount(body));
        if (size > budget)
        {
            budget = 0;
            clipped = true;
            title = null;
            body = null;
        }
        else
        {
            budget -= size;
        }
        if (title is not null)
        {
            wire.Title = title;
        }
        if (body is not null)
        {
            wire.Body = body;
        }
        if (clipped)
        {
            wire.TextClipped = true;
        }
        if (waiting)
        {
            wire.PayloadDigest = action.PayloadDigest();
        }
        if (groupState is not null)
        {
            wire.GroupState = groupState;
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
        if (entry.SignedAs is not null)
        {
            wire.SignedAs = entry.SignedAs;
        }
        if (entry.GroupId is not null)
        {
            wire.GroupId = entry.GroupId;
        }
        if (entry.ControlStatus is not null)
        {
            wire.ControlStatus = entry.ControlStatus;
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
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
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
