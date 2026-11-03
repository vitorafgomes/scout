#pragma warning disable SKEXP0001
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.TextToImage;
using Scout.Llm.Abstractions;

namespace Scout.Operator;

/// <summary>The picture of a scene: its visual source, the file in the media directory and a note.</summary>
internal sealed record VisualResult(string VisualSource, string? VisualFile, string? Note);

/// <summary>
/// Makes a scene's picture with the connected provider's text-to-image service, saved as <c>scene-&lt;id&gt;.png</c>
/// in the project's media directory, from the bytes the service returns; a picture returned only as a link is
/// refused and never fetched. A scene shows a text card instead, with a note, when there is no image service, when
/// it comes after the <see cref="MaxScenes"/>th scene of the video, or when <see cref="PerDay"/> pictures were
/// already made since midnight Pacific time in this process. A picture the provider fails to make, or returns without
/// bytes, gives its place in the day's count back.
/// </summary>
internal sealed class VisualGenerator(Kernel kernel, MediaCapabilities media, TimeProvider time)
{
    internal const int PerDay = 40;

    internal const int MaxScenes = 30;

    internal const string NoImages =
        "No picture: the connected AI Source has no image service in Scout; the scene shows a text card, or attach your own image in Scout Desktop, Production.";

    internal const string PastMaxScenes =
        "Scout makes pictures for at most 30 scenes of a video; this scene shows a text card.";

    internal const string DaySpent =
        "Scout made today's 40 pictures; this scene shows a text card. Regenerate it after midnight Pacific time.";

    private readonly SearchBudget _today = new(time, PerDay);

    internal static string Failed(int order) =>
        $"The AI Source could not make the picture of scene {order}; regenerate it in Scout Desktop, Production.";

    /// <summary>Make the picture of <paramref name="scene"/>, the <paramref name="position"/>th scene of the video.</summary>
    public async Task<VisualResult> GenerateAsync(OperatorScene scene, int position, string directory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var images = kernel.Services.GetService<ITextToImageService>();
        if (images is null)
        {
            return new VisualResult(OperatorPipeline.TextCard, null, NoImages);
        }

        if (position > MaxScenes)
        {
            return new VisualResult(OperatorPipeline.TextCard, null, PastMaxScenes);
        }

        if (!_today.TryTake())
        {
            return new VisualResult(OperatorPipeline.TextCard, null, DaySpent);
        }

        ReadOnlyMemory<byte>? bytes;
        try
        {
            var made = await images.GetImageContentsAsync(new TextContent(scene.VisualPrompt), media.ImageSettings, kernel: null, ct)
                .ConfigureAwait(false);
            bytes = made.Count > 0 ? made[0].Data : null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _today.GiveBack();
            throw new InvalidOperationException(Failed(scene.Order), ex);
        }

        if (bytes is not { IsEmpty: false } data)
        {
            _today.GiveBack();
            throw new InvalidOperationException(Failed(scene.Order));
        }

        var file = $"scene-{scene.Id}.png";
        await MediaFiles.WriteAsync(directory, file, data, ct).ConfigureAwait(false);
        return new VisualResult(OperatorPipeline.Generated, file, null);
    }

    /// <summary>
    /// Make a picture for the thumbnail of a video titled <paramref name="title"/> and save it as
    /// <paramref name="fileName"/>, counted against the same day's <see cref="PerDay"/> pictures; false, with nothing
    /// saved, when there is no image service, the day's pictures are spent, or the service fails or returns no bytes,
    /// which gives its place back.
    /// </summary>
    public Task<bool> ThumbnailAsync(string title, string directory, string fileName, CancellationToken ct)
        => PictureAsync(ThumbnailPrompt(title), directory, fileName, ct);

    /// <summary>
    /// Make a picture from <paramref name="prompt"/> and save it as <paramref name="fileName"/>, counted against the same
    /// day's <see cref="PerDay"/> pictures; false, with nothing saved, when there is no image service, the day's pictures are
    /// spent, or the service fails or returns no bytes, which gives its place back.
    /// </summary>
    public async Task<bool> PictureAsync(string prompt, string directory, string fileName, CancellationToken ct)
    {
        var images = kernel.Services.GetService<ITextToImageService>();
        if (images is null || !_today.TryTake())
        {
            return false;
        }

        ReadOnlyMemory<byte>? bytes;
        try
        {
            var made = await images.GetImageContentsAsync(new TextContent(prompt), media.ImageSettings, kernel: null, ct)
                .ConfigureAwait(false);
            bytes = made.Count > 0 ? made[0].Data : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            bytes = null;
        }

        if (bytes is not { IsEmpty: false } data)
        {
            _today.GiveBack();
            return false;
        }

        await MediaFiles.WriteAsync(directory, fileName, data, ct).ConfigureAwait(false);
        return true;
    }

    internal static string ThumbnailPrompt(string title) =>
        $"A YouTube thumbnail picture for a video titled \"{title}\": one bold, simple subject, strong contrast, no text, " +
        "no letters, no logos, no trademarks, no real person's likeness and no public figure.";
}
