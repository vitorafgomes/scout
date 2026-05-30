namespace Scout.Skills;

/// <summary>
/// Selects the skills most relevant to a user message via embedding cosine
/// similarity. Implementations never throw: on any failure they return an empty
/// list so the conversation degrades gracefully.
/// </summary>
public interface ISkillSelector
{
    /// <summary>
    /// Returns the skills whose descriptions best match the message, ordered by
    /// descending similarity, limited and thresholded by configuration.
    /// </summary>
    /// <param name="message">The user message text.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The selected skills, possibly empty.</returns>
    Task<IReadOnlyList<Skill>> SelectAsync(string message, CancellationToken cancellationToken);
}
