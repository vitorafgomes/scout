namespace Scout.Plugin.WhatToWatch.Abstractions;

/// <summary>
/// The result of a next-episode lookup: the matched show plus its next scheduled
/// episode, if any. A null <see cref="NextEpisode"/> means the show was found but
/// has no upcoming episode (between seasons or ended).
/// </summary>
/// <param name="ShowName">The matched show's title, e.g. "Severance".</param>
/// <param name="NextEpisode">The next scheduled episode, or null when none is planned.</param>
public sealed record ShowEpisode(
    string ShowName,
    Episode? NextEpisode);
