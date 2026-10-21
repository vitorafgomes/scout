namespace Scout.Host.Console;

/// <summary>The host entry point's top-level command.</summary>
internal enum Command
{
    /// <summary>Run the host and start configured adapters.</summary>
    Run,

    /// <summary>Index a configured project, then exit.</summary>
    Index,
}

/// <summary>A parsed command-line decision: the command and its optional target.</summary>
internal readonly record struct ConsoleCommand(Command Command, string? Project)
{
    /// <summary>
    /// Maps process args to a command decision. <c>index &lt;name&gt;</c> selects
    /// indexing; anything else runs the host. Parsing only: no side effects.
    /// </summary>
    /// <param name="args">The process arguments.</param>
    /// <returns>The parsed command.</returns>
    public static ConsoleCommand Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length > 0 && string.Equals(args[0], "index", StringComparison.OrdinalIgnoreCase))
        {
            var project = args.Length > 1 ? args[1] : null;
            return new ConsoleCommand(Command.Index, project);
        }

        return new ConsoleCommand(Command.Run, null);
    }
}
