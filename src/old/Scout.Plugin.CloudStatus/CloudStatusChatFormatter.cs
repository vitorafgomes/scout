using System.Text;
using Scout.Plugin.CloudStatus.Domain;

namespace Scout.Plugin.CloudStatus;

/// <summary>
/// Pure, stateless formatter that renders <see cref="StatusReport"/> values
/// into chat-friendly text. Keeps rendering logic out of the plugin and the
/// router so it is trivially unit-testable and adapter-agnostic (Discord /
/// Slack / Teams all consume plain text today).
/// </summary>
public static class CloudStatusChatFormatter
{
    public static string Format(IReadOnlyList<StatusReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);

        if (reports.Count == 0)
        {
            return "No cloud providers are configured.";
        }

        var sb = new StringBuilder();
        sb.AppendLine("Cloud health:");
        foreach (var report in reports)
        {
            AppendReportLine(sb, report);
        }
        return sb.ToString().TrimEnd();
    }

    public static string Format(StatusReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        AppendReportLine(sb, report);
        return sb.ToString().TrimEnd();
    }

    private static void AppendReportLine(StringBuilder sb, StatusReport report)
    {
        sb.Append("- ")
          .Append(report.Provider)
          .Append(" (")
          .Append(report.Scope)
          .Append("): ")
          .Append(report.Overall);

        if (!string.IsNullOrWhiteSpace(report.Message))
        {
            sb.Append(" — ").Append(report.Message);
        }

        sb.AppendLine();
    }
}
