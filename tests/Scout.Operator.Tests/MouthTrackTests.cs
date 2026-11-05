namespace Scout.Operator.Tests;

public sealed class MouthTrackTests
{
    [Fact]
    public void Cues_Become_An_Ffconcat_List_With_Merged_Durations_And_A_Repeated_Last_Entry()
    {
        MouthCue[] cues =
        [
            new(0.05, 0.12, "X"), new(0.12, 0.30, "B"), new(0.30, 0.45, "B"), new(0.45, 0.62, "C"), new(0.62, 1.00, "X"),
        ];

        MouthTrack.List(cues, "/opt/scout/mouths").ShouldBe(
            "ffconcat version 1.0\n" +
            "file '/opt/scout/mouths/X.png'\nduration 0.120\n" +
            "file '/opt/scout/mouths/B.png'\nduration 0.330\n" +
            "file '/opt/scout/mouths/C.png'\nduration 0.170\n" +
            "file '/opt/scout/mouths/X.png'\nduration 0.380\n" +
            "file '/opt/scout/mouths/X.png'\n");
    }

    [Fact]
    public void A_Quote_In_The_Mouths_Folder_Cannot_End_The_File_Name()
        => MouthTrack.List([new MouthCue(0, 0.5, "A")], "/home/o'neil/mouths").ShouldBe(
            "ffconcat version 1.0\nfile '/home/o'\\''neil/mouths/A.png'\nduration 0.500\nfile '/home/o'\\''neil/mouths/A.png'\n");
}
