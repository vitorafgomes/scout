namespace Scout.Plugin.WebSearch.Abstractions;

/// <summary>
/// The outcome of fetching a URL. Read-only projection. When
/// <see cref="IsReadable"/> is false the body was not downloaded because the
/// media type is not text (e.g. an image or a PDF). When
/// <see cref="IsHtml"/> is true the <see cref="Content"/> is raw HTML that still
/// needs stripping to readable text. The content is untrusted external text and
/// must be fenced before it reaches the model.
/// </summary>
/// <param name="IsReadable">True when the response carried readable text.</param>
/// <param name="IsHtml">True when the content is HTML needing tag stripping.</param>
/// <param name="MediaType">The response media type, e.g. "text/html".</param>
/// <param name="Content">The (bounded) raw body, or empty when not readable.</param>
public sealed record FetchedPage(bool IsReadable, bool IsHtml, string MediaType, string Content);
