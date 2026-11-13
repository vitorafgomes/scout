using System.Text;

namespace Scout.Operator;

/// <summary>
/// Makes <see cref="FileName"/>: a picture from the connected provider's image service for the video's first title, or,
/// when that cannot be had, the frame of the video at a tenth of its length. A picture is made once per title: the
/// title it was made for is kept beside it in <see cref="TitleFile"/>, and a picture already there for the same title
/// is used again without asking the image service; a title file that cannot be read or written only means the picture
/// is made again next time. The thumbnail of a video made from the owner's own recording is always that frame.
/// </summary>
internal sealed class ThumbnailMaker(VisualGenerator visuals, FfmpegToolset ffmpeg)
{
    internal const string FileName = "thumbnail.png";

    internal const string TitleFile = "thumbnail.txt";

    internal const string FrameFailed = "Scout could not take the thumbnail from the video";

    /// <summary>Make the thumbnail and return its file name; <paramref name="frameOnly"/> takes the frame without asking the image service.</summary>
    public async Task<string> MakeAsync(FfmpegTools tools, string directory, string title, AssembledVideo video, bool frameOnly, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(video);
        if (MediaFiles.Exists(directory, FileName) && await MadeForAsync(directory, ct).ConfigureAwait(false) == title)
        {
            return FileName;
        }

        if (!frameOnly && await visuals.ThumbnailAsync(title, directory, FileName, ct).ConfigureAwait(false))
        {
            try
            {
                await MediaFiles.WriteAsync(directory, TitleFile, Encoding.UTF8.GetBytes(title), ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                MediaFiles.Delete(directory, TitleFile);
            }

            return FileName;
        }

        MediaFiles.Delete(directory, TitleFile);
        var source = MediaFiles.PathFor(directory, video.File);
        await MediaFiles.RenderAsync(
            directory,
            FileName,
            FrameFailed,
            temp => ffmpeg.RunAsync(tools, FrameArguments(source, video.DurationMs / 10, temp), 0, FrameFailed, ct),
            ct).ConfigureAwait(false);
        return FileName;
    }

    internal static List<string> FrameArguments(string video, int atMs, string output)
        => ["-hide_banner", "-nostdin", "-y", "-ss", FfmpegToolset.Seconds(atMs), "-i", video, "-frames:v", "1", output];

    private static async Task<string?> MadeForAsync(string directory, CancellationToken ct)
    {
        try
        {
            return await File.ReadAllTextAsync(MediaFiles.PathFor(directory, TitleFile), Encoding.UTF8, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
