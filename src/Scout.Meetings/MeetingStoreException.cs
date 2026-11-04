namespace Scout.Meetings;

/// <summary>
/// The meetings database could not be opened, read or written. The message names the operation and the SQLite error
/// code only, never a title, a line or a turn.
/// </summary>
public sealed class MeetingStoreException(string message, Exception innerException) : Exception(message, innerException);
