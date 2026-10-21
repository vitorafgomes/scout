using System.Net;

namespace Scout.Plugin.Recipes;

/// <summary>
/// Raised when the recipes API returns a non-success response or an unreadable
/// body. The <see cref="Exception.Message"/> is user-safe and suitable for
/// surfacing in chat. <see cref="StatusCode"/> is the originating HTTP status when
/// known.
/// </summary>
public sealed class RecipesApiException : Exception
{
    /// <summary>Creates the exception with a user-safe message and optional status.</summary>
    public RecipesApiException(string message, HttpStatusCode? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>HTTP status that produced the failure, when known.</summary>
    public HttpStatusCode? StatusCode { get; }
}
