using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

/// <summary>
/// The worker tells the desktop that a voice message arrived without a speech model as one <c>SCOUT_LOCAL</c> line
/// with a null id and the chat platform only, the shape the desktop's notification center parses.
/// </summary>
public sealed class StdoutVoiceNoticeTests
{
    [Fact]
    public void A_Voice_Message_Without_A_Model_Writes_One_Line_With_The_Platform_And_Nothing_Else()
    {
        using var stdout = new StringWriter();

        new StdoutVoiceNotice(stdout).NoSpeechModel("discord");

        stdout.ToString().ShouldBe(
            """SCOUT_LOCAL {"id":null,"event":"voice-no-model","platform":"discord"}""" + Environment.NewLine);
    }
}
