namespace Scout.Host.Worker.Features;

/// <summary>
/// Per-feature write classification under the descriptive/executive split.
/// Phase 1 ships every feature as <see cref="None"/>; Phase 2 promotes
/// <c>github</c> to <see cref="Descriptive"/> when CreateIssue lands.
/// </summary>
public enum WriteClass
{
    /// <summary>Read-only — no external mutations of any kind.</summary>
    None,

    /// <summary>
    /// Mutations that produce content for a human/agent to read and act
    /// on (e.g. opening a GitHub issue). Permitted.
    /// </summary>
    Descriptive,

    /// <summary>
    /// Mutations that directly change infra/code/data without a human
    /// gate. Forbidden by the Scout security invariant.
    /// </summary>
    Executive,
}
