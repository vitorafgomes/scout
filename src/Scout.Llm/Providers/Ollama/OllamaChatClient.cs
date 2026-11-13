using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using OllamaSharp.Models.Chat;
using OllamaSharp.Models.Exceptions;

namespace Scout.Llm.Providers.Ollama;

/// <summary>
/// Keeps the Ollama client's HTTP failures inside this folder: an answer with a status becomes Semantic Kernel's
/// <see cref="HttpOperationException"/> with that status, which the gateway maps like every other provider's. A 400,
/// which the client raises as its own exception carrying the server's sentence (a model that does not support tools
/// among them), becomes one with status 400 too. The server's text never travels: the message names only the status,
/// and the client's exception stays only as the inner exception. A 404 carries Scout's own
/// <c>{"error":{"code":"model_not_found"}}</c> as its response content, because Ollama's chat endpoint answers 404 for a
/// model the server does not have and the client drops the server's sentence. A failure with no status, such as a
/// refused connection or a reset, passes through unchanged. A stream cancelled before it ends throws
/// <see cref="OperationCanceledException"/>, because the client's reader stops on a cancelled token as if the answer had
/// ended. An answer that ends without the server's done line, which is how the client reads an <c>{"error":...}</c>
/// line inside a started answer (the model runner stopped, memory ran out), becomes an
/// <see cref="HttpOperationException"/> with status 502 and no server text, so a cut answer is never taken as finished.
/// </summary>
internal sealed class OllamaChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private const string MissingModel = """{"error":{"code":"model_not_found"}}""";
    private const string Incomplete = "Ollama stopped answering before the answer was complete.";

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is { } status)
        {
            throw Translated(status, ex);
        }
        catch (OllamaException ex)
        {
            throw Translated(HttpStatusCode.BadRequest, ex);
        }
        catch (InvalidOperationException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new HttpOperationException(HttpStatusCode.BadGateway, null, Incomplete, ex);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var e = base.GetStreamingResponseAsync(messages, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        var done = false;
        while (true)
        {
            try
            {
                if (!await e.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode is { } status)
            {
                throw Translated(status, ex);
            }
            catch (OllamaException ex)
            {
                throw Translated(HttpStatusCode.BadRequest, ex);
            }

            done |= e.Current.RawRepresentation is ChatResponseStream { Done: true };
            yield return e.Current;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!done)
        {
            throw new HttpOperationException(HttpStatusCode.BadGateway, null, Incomplete, innerException: null);
        }
    }

    private static HttpOperationException Translated(HttpStatusCode status, Exception ex)
        => new(
            status,
            status == HttpStatusCode.NotFound ? MissingModel : null,
            $"Ollama answered HTTP {(int)status}.",
            ex);
}
