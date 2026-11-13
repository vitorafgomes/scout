namespace Scout.ControlPlane.Controls;

/// <summary>The kinds of job an owner control acts on.</summary>
public static class OwnerControlKinds
{
    /// <summary>The audit of a whole repository, named by owner and repo.</summary>
    public const string Audit = "audit";

    /// <summary>A pull request's test run in a container, named by owner, repo and pull request number.</summary>
    public const string Tests = "tests";
}

/// <summary>What an owner control asks for.</summary>
public static class OwnerControlActions
{
    /// <summary>Pause a running audit.</summary>
    public const string Pause = "pause";

    /// <summary>Continue a paused audit.</summary>
    public const string Continue = "continue";

    /// <summary>Stop a running or paused audit, or a running test run.</summary>
    public const string Stop = "stop";
}

/// <summary>How a control ended, as its record in the write log says it, for the desktop to colour.</summary>
public static class OwnerControlStatuses
{
    /// <summary>The worker applied the control: the job paused, continued or stopped.</summary>
    public const string Applied = "applied";

    /// <summary>The worker took the control and it changed nothing, as on a job that had already ended.</summary>
    public const string NotApplied = "not applied";

    /// <summary>The worker took the control and did not say how it ended in time; it may or may not have been applied.</summary>
    public const string NotConfirmed = "not confirmed";

    /// <summary>The control waited too long for the worker and was dropped without being applied.</summary>
    public const string Expired = "expired";
}

/// <summary>
/// One click of the owner in Scout Desktop on a running job: its kind, the repository, the pull request number for a test
/// run, the action, when it was asked, when the worker took it and when the worker said how it ended. A control is handed
/// to the worker once and recorded once.
/// </summary>
public sealed record OwnerControl(
    string Id,
    string Kind,
    string Owner,
    string Repo,
    int? Number,
    string Action,
    DateTimeOffset RequestedAt,
    DateTimeOffset? TakenAt = null,
    DateTimeOffset? SettledAt = null)
{
    /// <summary>The job the control names: <c>owner/repo</c> for an audit, <c>owner/repo#number</c> for a test run.</summary>
    public string Target => Number is { } number ? $"{Owner}/{Repo}#{number}" : $"{Owner}/{Repo}";
}
