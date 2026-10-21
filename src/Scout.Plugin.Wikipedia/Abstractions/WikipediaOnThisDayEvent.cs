namespace Scout.Plugin.Wikipedia.Abstractions;

/// <summary>
/// A notable historical event from Wikipedia's "On this day" feed. Minimal
/// projection. <paramref name="Text"/> is relayed as data; source markup is
/// stripped by the client.
/// </summary>
/// <param name="Text">Plain-text description of the event.</param>
/// <param name="Year">Year the event occurred, or null when absent.</param>
public sealed record WikipediaOnThisDayEvent(string Text, int? Year);
