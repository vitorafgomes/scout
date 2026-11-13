using System.Runtime.Versioning;
using Scout.ControlPlane.Providers;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Providers;

public sealed class PathBinaryLocatorTests
{
    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public void A_File_On_Path_Without_An_Execute_Bit_Is_Not_Reported_As_The_Binary()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var notExecutable = Directory.CreateTempSubdirectory("scout-path-plain-").FullName;
        var executable = Directory.CreateTempSubdirectory("scout-path-exec-").FullName;
        try
        {
            var plain = Path.Combine(notExecutable, "ollama");
            var program = Path.Combine(executable, "ollama");
            File.WriteAllText(plain, string.Empty);
            File.WriteAllText(program, string.Empty);
            File.SetUnixFileMode(plain, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var onlyPlain = PathBinaryLocator.Find(notExecutable, "ollama");
            var plainFirst = PathBinaryLocator.Find(notExecutable + Path.PathSeparator + executable, "ollama");

            onlyPlain.ShouldBeNull();
            plainFirst.ShouldBe(program);
        }
        finally
        {
            Directory.Delete(notExecutable, recursive: true);
            Directory.Delete(executable, recursive: true);
        }
    }
}
