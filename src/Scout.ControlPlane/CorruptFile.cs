using System.Globalization;

namespace Scout.ControlPlane;

/// <summary>Moves a file that does not parse out of the way, never over a file moved aside before it.</summary>
internal static class CorruptFile
{
    /// <summary>
    /// Move <paramref name="path"/> to <c>&lt;path&gt;.corrupt-&lt;UTC yyyyMMddHHmmss&gt;</c>, the time read from
    /// <paramref name="time"/>, or, while that name is taken, to the same name followed by <c>-1</c>, <c>-2</c> and so on;
    /// answers the path it moved the file to. A move that fails is the caller's failure and the file stays where it is.
    /// </summary>
    public static string MoveAside(string path, TimeProvider time)
    {
        var stamped = path + ".corrupt-" + time.GetUtcNow().UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var aside = stamped;
        for (var taken = 1; File.Exists(aside); taken++)
        {
            aside = stamped + "-" + taken.ToString(CultureInfo.InvariantCulture);
        }

        File.Move(path, aside, overwrite: false);
        return aside;
    }
}
