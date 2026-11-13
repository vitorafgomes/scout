namespace Scout.Llm.Providers.OpenAI;

/// <summary>
/// The vector size the OpenAI-family factories ask an embedding model for: the 768 values the project index stores, and
/// only from a text-embedding-3 model, the one family that accepts the option; any other model is asked for its own size.
/// </summary>
internal static class OpenAIEmbeddingDimensions
{
    private const int Stored = 768;

    /// <summary>768 for a text-embedding-3 model id (an <c>openai/</c> prefix allowed), otherwise null.</summary>
    internal static int? For(string model)
        => model[(model.LastIndexOf('/') + 1)..].StartsWith("text-embedding-3", StringComparison.OrdinalIgnoreCase) ? Stored : null;
}
