using System.Diagnostics;

namespace Scout.Operator.Tests;

public sealed class ProcessRunnerTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Arguments_Reach_The_Program_One_By_One_Without_A_Shell_Splitting_Or_Expanding_Them()
    {
        var result = await new ProcessRunner().RunAsync(
            "/bin/sh", ["-c", "printf '%s|' \"$@\"", "sh", "two words", "a;b", "$HOME", "*"], Generous, CancellationToken.None);

        (result.ExitCode, result.Output).ShouldBe((0, "two words|a;b|$HOME|*|"));
    }

    [Fact]
    public async Task The_Program_Reads_No_Input_Because_Its_Standard_Input_Is_Closed_At_Once()
    {
        var watch = Stopwatch.StartNew();

        var result = await new ProcessRunner().RunAsync("/bin/sh", ["-c", "cat; echo done"], TimeSpan.FromSeconds(5), CancellationToken.None);

        (result.ExitCode, result.Output).ShouldBe((0, "done\n"));
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task The_Given_Input_Reaches_The_Programs_Standard_Input_As_UTF8_Without_A_Byte_Order_Mark_And_Then_Ends()
    {
        var result = await new ProcessRunner().RunAsync(
            "/bin/sh", ["-c", "od -An -tx1 | tr -s ' \n' ' '"], Generous, CancellationToken.None, "Pão;\n$HOME");

        result.ExitCode.ShouldBe(0);
        result.Output.Trim().ShouldBe("50 c3 a3 6f 3b 0a 24 48 4f 4d 45");
    }

    [Fact]
    public async Task A_Program_That_Exits_Without_Reading_Its_Input_Still_Returns_Its_Exit_Code_And_Error_Output()
    {
        var input = new string('x', 1_000_000);

        var result = await new ProcessRunner().RunAsync(
            "/bin/sh", ["-c", "echo 'piper: no model' >&2; exit 3"], Generous, CancellationToken.None, input);

        (result.ExitCode, result.ErrorTail).ShouldBe((3, "piper: no model\n"));
    }

    [Fact]
    public async Task A_Program_That_Exits_With_An_Error_Returns_Its_Exit_Code_And_The_End_Of_Its_Error_Output()
    {
        var result = await new ProcessRunner().RunAsync(
            "/bin/sh",
            ["-c", "i=0; while [ $i -lt 2000 ]; do echo \"noise line $i\" >&2; i=$((i+1)); done; echo 'Error opening input file scene-s1.png.' >&2; exit 3"],
            Generous,
            CancellationToken.None);

        result.ExitCode.ShouldBe(3);
        result.ErrorTail.Length.ShouldBe(ProcessRunner.TailLength);
        result.ErrorTail.ShouldEndWith("Error opening input file scene-s1.png.\n");
        FfmpegToolset.LastLine(result.ErrorTail).ShouldBe("Error opening input file scene-s1.png.");
    }

    [Fact]
    public async Task A_Program_That_Does_Not_Exist_Fails_With_A_Sentence_Naming_It()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => new ProcessRunner().RunAsync("/nonexistent/scout/ffmpeg", ["-version"], Generous, CancellationToken.None));

        ex.Message.ShouldBe("Scout could not start ffmpeg.");
    }

    [Fact]
    public async Task A_Program_That_Runs_Past_Its_Timeout_Is_Killed_And_Fails_With_A_Sentence_Saying_So()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"scout-runner-{Guid.NewGuid():N}");
        var watch = Stopwatch.StartNew();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => new ProcessRunner().RunAsync(
            "/bin/sh", ["-c", $"sleep 3; touch '{marker}'"], TimeSpan.FromMilliseconds(300), CancellationToken.None));
        await Task.Delay(TimeSpan.FromSeconds(4));

        ex.Message.ShouldBe("sh ran longer than 0 seconds and was stopped.");
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(8));
        File.Exists(marker).ShouldBeFalse();
    }

    [Fact]
    public async Task A_Cancelled_Run_Is_Killed_And_Throws_The_Cancellation()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();

        await Should.ThrowAsync<OperationCanceledException>(
            () => new ProcessRunner().RunAsync("/bin/sh", ["-c", "sleep 10"], Generous, cancel.Token));

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task Input_Bytes_Reach_The_Programs_Stdin()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x0A, 0x80, 0xFF, 0xD9];

        var result = await new ProcessRunner().RunAsync(
            "/bin/sh", ["-c", "od -An -tx1 | tr -s ' \n' ' '"], Generous, CancellationToken.None, inputBytes: jpeg);

        result.ExitCode.ShouldBe(0);
        result.Output.Trim().ShouldBe("ff d8 ff e0 00 0a 80 ff d9");
    }
}
