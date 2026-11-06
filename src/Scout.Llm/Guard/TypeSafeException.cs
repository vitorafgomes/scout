using System.Net;

namespace Scout.Llm.Guard;

/// <summary>
/// Raised when a TypeSafe call fails. The message carries the HTTP status and the
/// provider's validation detail only, never the API key.
/// </summary>
/// <remarks>Creates the exception with a key-free message, the status when known, and the cause.</remarks>
internal sealed class TypeSafeException(string message, HttpStatusCode? statusCode = null, Exception? innerException = null) : Exception(message, innerException)
{

    /// <summary>HTTP status that produced the failure, when known.</summary>
    public HttpStatusCode? StatusCode { get; } = statusCode;
}
