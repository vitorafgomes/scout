using Scout.Host.Console;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

/// <summary>
/// Verifies the argument parser maps the process args to a command decision the
/// host entry point branches on: index a named project, or run the host.
/// </summary>
public sealed class ConsoleCommandTests
{
    [Fact]
    public void Index_With_Name_Parses_To_Index_Command()
    {
        var parsed = ConsoleCommand.Parse(["index", "scout"]);

        parsed.Command.ShouldBe(Command.Index);
        parsed.Project.ShouldBe("scout");
    }

    [Fact]
    public void No_Args_Parses_To_Run_Command()
    {
        var parsed = ConsoleCommand.Parse([]);

        parsed.Command.ShouldBe(Command.Run);
        parsed.Project.ShouldBeNull();
    }

    [Fact]
    public void Index_Without_Name_Parses_To_Index_With_Null_Project()
    {
        var parsed = ConsoleCommand.Parse(["index"]);

        parsed.Command.ShouldBe(Command.Index);
        parsed.Project.ShouldBeNull();
    }

    [Fact]
    public void Unknown_First_Arg_Parses_To_Run_Command()
    {
        var parsed = ConsoleCommand.Parse(["serve"]);

        parsed.Command.ShouldBe(Command.Run);
        parsed.Project.ShouldBeNull();
    }
}
