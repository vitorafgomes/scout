using Scout.Conversation.Abstractions;

namespace Scout.Operator.ScreenText;

/// <summary>
/// Where the text reader finds the bundled <c>tesseract</c> and its language data, and the app's locale, bound from
/// <see cref="Section"/>. Without both paths no text reader is registered.
/// </summary>
public sealed class ToolsOptions
{
    /// <summary>The configuration section bound to these options.</summary>
    public const string Section = "Scout:Tools";

    /// <summary>The bundled <c>tesseract</c> program.</summary>
    public string TesseractPath { get; set; } = string.Empty;

    /// <summary>The folder of tesseract's <c>&lt;lang&gt;.traineddata</c> files.</summary>
    public string TessdataPath { get; set; } = string.Empty;

    /// <summary>The app's locale, such as <c>pt-BR</c> or <c>en-GB</c>; <c>en</c> unless configured.</summary>
    public string Locale { get; set; } = "en";
}

/// <summary>
/// <see cref="IScreenTextReader"/> over the bundled <c>tesseract</c>: the JPEG goes on its standard input and the text
/// comes back on its standard output, <c>stdin stdout --psm 6 -l &lt;languages&gt; --tessdata-dir &lt;folder&gt;</c>, so no
/// file is written and the image never touches the disk. The languages are <c>por+eng</c> when the locale starts with
/// <c>pt</c>, else <c>eng</c>. A run gets <see cref="Timeout"/>; one that runs past it, exits other than zero or reads no
/// text throws. The text has every control character but the newline turned into a space, is trimmed, and is cut at
/// <see cref="MaxChars"/> characters.
/// </summary>
internal sealed class TesseractScreenReader(IProcessRunner runner, ToolsOptions options) : IScreenTextReader
{
    internal const int MaxChars = 5000;

    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    internal const string NoText = "tesseract found no text on the screenshot.";

    internal static string Failed(int exitCode, string errorTail) =>
        $"tesseract exited with code {exitCode} ({FfmpegToolset.LastLine(errorTail)}).";

    internal static string Languages(string locale) =>
        locale.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ? "por+eng" : "eng";

    public async Task<string> ReadAsync(ReadOnlyMemory<byte> jpeg, CancellationToken ct)
    {
        var result = await runner.RunAsync(
            options.TesseractPath.Trim(),
            ["stdin", "stdout", "--psm", "6", "-l", Languages(options.Locale), "--tessdata-dir", options.TessdataPath.Trim()],
            Timeout,
            ct,
            inputBytes: jpeg.ToArray()).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(Failed(result.ExitCode, result.ErrorTail));
        }

        var text = new string([.. result.Output.Select(c => char.IsControl(c) && c != '\n' ? ' ' : c)]).Trim();
        if (text.Length == 0)
        {
            throw new InvalidOperationException(NoText);
        }

        return text.Length > MaxChars ? text[..MaxChars] : text;
    }
}
