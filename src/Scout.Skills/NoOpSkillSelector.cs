namespace Scout.Skills;

/// <summary>
/// Skill selector that selects nothing. Registered when the skills feature is
/// disabled so consumers can depend on <see cref="ISkillSelector"/> uniformly.
/// </summary>
public sealed class NoOpSkillSelector : ISkillSelector
{
    /// <inheritdoc />
    public Task<IReadOnlyList<Skill>> SelectAsync(string message, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<Skill>>([]);
}
