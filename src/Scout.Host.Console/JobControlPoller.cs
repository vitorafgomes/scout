using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scout.Conversation.Abstractions;
using Scout.Plugin.GitHub;

namespace Scout.Host.Console;

/// <summary>
/// Applies the owner's clicks in Scout Desktop on running jobs, each taken from the control plane once and settled once with
/// its status (<c>applied</c>, <c>not applied</c> or <c>not confirmed</c>) and the sentence that says how it ended, which the
/// control plane keeps in the write log as the owner's. An audit control goes to the audit library: pause, continue (only a
/// paused audit, in the conversation it already holds; never a new audit) or stop. A test run control asks every run in
/// progress of that pull request to stop and waits for each to end: a run the stop cut short gets one notice in the
/// conversation it was asked from, and a run that had already finished keeps its result. It asks every <see cref="Fast"/>
/// while clicks arrive, and every <see cref="Slow"/> once none has arrived for <see cref="QuietWindow"/>, the control plane
/// down included, back to <see cref="Fast"/> as soon as one arrives. A take that fails is logged once and tried on the next
/// tick, so nothing is lost; a settle that fails after the control was applied is tried again on the next tick and the
/// control is never applied again.
/// </summary>
internal sealed class JobControlPoller(
    IJobControls controls,
    IOriginNotifier notifier,
    ILogger<JobControlPoller> logger,
    Scout.Audit.IRepositoryAudits? audits = null,
    RunningTests? tests = null,
    TimeProvider? time = null) : BackgroundService
{
    internal static readonly TimeSpan Fast = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan Slow = GitHubActionExecutor.Interval;
    internal static readonly TimeSpan QuietWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a settle that keeps failing is retried before it is dropped, so a record the control plane refuses never
    /// keeps the next click waiting; past it the control plane's sweep has already recorded the control as not confirmed.
    /// </summary>
    internal static readonly TimeSpan PendingSettleLimit = TimeSpan.FromMinutes(2);

    /// <summary>How long a stop waits for the runs it asked to end before it is settled as not confirmed.</summary>
    internal static readonly TimeSpan StopWait = TimeSpan.FromSeconds(90);

    internal const string Applied = "applied";
    internal const string NotApplied = "not applied";
    internal const string NotConfirmed = "not confirmed";

    internal const string NoAudits = "Not applied: audits are not available in this Scout.";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private DateTimeOffset? _lastControlAt;
    private (JobControl Control, string Status, string Outcome, DateTimeOffset Since)? _unsettled;
    private bool _takeFailureLogged;

    internal static string TestsStopped(JobControl control) => $"Stopped the tests of {Target(control)}; no result was kept.";

    internal static string NoTestRun(JobControl control) => $"Not applied: no test run of {Target(control)} is running.";

    internal static string TestsFinished(JobControl control) => $"Not applied: the tests of {Target(control)} had already finished; their result stands.";

    internal static string TestsDidNotEnd(JobControl control) =>
        $"Not confirmed: Scout asked the tests of {Target(control)} to stop and they did not end within 90 seconds.";

    internal static string TestsStoppedNotice(JobControl control) => $"The tests of {Target(control)} ended at your request; no result was kept.";

    internal static string Unknown(JobControl control) => $"Not applied: Scout does not {control.Action} a {control.Kind} job.";

    /// <summary>How long to wait before asking again: <see cref="Fast"/> within <see cref="QuietWindow"/> of the last click or of the start, <see cref="Slow"/> after.</summary>
    internal TimeSpan NextDelay()
    {
        _lastControlAt ??= _time.GetUtcNow();
        return _time.GetUtcNow() - _lastControlAt.Value > QuietWindow ? Slow : Fast;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _lastControlAt = _time.GetUtcNow();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                while (await RunOnceAsync(stoppingToken).ConfigureAwait(false))
                {
                    stoppingToken.ThrowIfCancellationRequested();
                }

                await Task.Delay(NextDelay(), _time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Settle the control whose record failed last time, else apply the next control waiting and settle it; false when nothing was done.</summary>
    internal async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        if (_unsettled is { } pending)
        {
            if (_time.GetUtcNow() - pending.Since <= PendingSettleLimit)
            {
                return await SettleAsync(pending.Control, pending.Status, pending.Outcome, ct).ConfigureAwait(false);
            }

            logger.LogWarning("The owner's control {ControlId} could not be recorded for two minutes; the control plane records it as not confirmed.", pending.Control.Id);
            _unsettled = null;
            return false;
        }

        JobControl? control;
        try
        {
            control = await controls.TakeNextAsync(ct).ConfigureAwait(false);
            _takeFailureLogged = false;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (!_takeFailureLogged)
            {
                logger.LogWarning("Scout cannot read the owner's controls ({ExceptionType}).", ex.GetType().Name);
                _takeFailureLogged = true;
            }

            return false;
        }

        if (control is null)
        {
            return false;
        }

        _lastControlAt = _time.GetUtcNow();
        (string Status, string Outcome) applied;
        try
        {
            applied = await ApplyAsync(control, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("The owner's control {ControlId} failed in Scout ({ExceptionType}).", control.Id, ex.GetType().Name);
            applied = (NotApplied, $"Not applied: it failed in Scout ({ex.GetType().Name}).");
        }

        await SettleAsync(control, applied.Status, applied.Outcome, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> SettleAsync(JobControl control, string status, string outcome, CancellationToken ct)
    {
        try
        {
            await controls.SettleAsync(control.Id, status, outcome, ct).ConfigureAwait(false);
            _unsettled = null;
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("The owner's control {ControlId} was applied but could not be recorded yet ({ExceptionType}).", control.Id, ex.GetType().Name);
            _unsettled = (control, status, outcome, _unsettled?.Since ?? _time.GetUtcNow());
            return false;
        }
    }

    private async Task<(string Status, string Outcome)> ApplyAsync(JobControl control, CancellationToken ct)
    {
        switch (control.Kind, control.Action)
        {
            case ("audit", "pause" or "continue" or "stop") when audits is null:
                return (NotApplied, NoAudits);
            case ("audit", "pause" or "continue" or "stop"):
                var answer = await audits!.ControlAsync(control.Owner, control.Repo, control.Action, ct).ConfigureAwait(false);
                return (answer.Applied ? Applied : NotApplied, answer.Sentence);
            case ("tests", "stop") when control.Number is { } number:
                return await StopTestsAsync(control, number, ct).ConfigureAwait(false);
            default:
                return (NotApplied, Unknown(control));
        }
    }

    private async Task<(string Status, string Outcome)> StopTestsAsync(JobControl control, int number, CancellationToken ct)
    {
        if (tests is null)
        {
            return (NotApplied, NoTestRun(control));
        }

        using var deadline = new CancellationTokenSource(StopWait, _time);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        IReadOnlyList<StoppedRun> runs;
        try
        {
            runs = await tests.StopAsync(control.Owner, control.Repo, number, wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (NotConfirmed, TestsDidNotEnd(control));
        }

        if (runs.Count == 0)
        {
            return (NotApplied, NoTestRun(control));
        }

        if (!runs.Any(r => r.Stopped))
        {
            return (NotApplied, TestsFinished(control));
        }

        foreach (var origin in runs.Where(r => r.Stopped).Select(r => r.Origin).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            await NotifyAsync(origin, TestsStoppedNotice(control), ct).ConfigureAwait(false);
        }

        return (Applied, TestsStopped(control));
    }

    private async Task NotifyAsync(string origin, string text, CancellationToken ct)
    {
        try
        {
            if (!await notifier.PostAsync(origin, text, ct).ConfigureAwait(false))
            {
                logger.LogWarning("The notice of a stopped test run could not be posted to the conversation it was asked from.");
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("The notice of a stopped test run could not be posted ({ExceptionType}).", ex.GetType().Name);
        }
    }

    private static string Target(JobControl control) =>
        control.Number is { } number ? $"{control.Owner}/{control.Repo}#{number}" : $"{control.Owner}/{control.Repo}";
}
