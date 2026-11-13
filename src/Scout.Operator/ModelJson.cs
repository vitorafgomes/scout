using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Scout.Operator;

/// <summary>
/// Asks the chat model for one JSON answer, offering it no function, and asks once more when the answer is not
/// the JSON asked for, with the same system prompt and request and a retry instruction but never the bad answer,
/// so the second ask costs what the first did; a second bad answer or a model that does not answer is an
/// <see cref="InvalidOperationException"/> with a sentence the owner can read.
/// </summary>
internal static class ModelJson
{
    internal const string Retry =
        "Your previous answer was not valid JSON in the shape asked for. Answer again with the JSON only.";

    internal const string Unreachable = "The AI model did not answer; check the AI Source in Scout Desktop.";

    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static async Task<T> AskAsync<T>(
        IChatCompletionService chat,
        string system,
        string user,
        Func<T, bool> isValid,
        string failure,
        CancellationToken ct)
        where T : class
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var history = new ChatHistory();
            history.AddSystemMessage(system);
            history.AddUserMessage(user);
            if (attempt > 0)
            {
                history.AddUserMessage(Retry);
            }

            string text;
            try
            {
                var reply = await chat.GetChatMessageContentAsync(history, executionSettings: null, kernel: null, ct)
                    .ConfigureAwait(false);
                text = reply.Content ?? string.Empty;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException(Unreachable, ex);
            }

            var value = Parse<T>(text);
            if (value is not null && isValid(value))
            {
                return value;
            }
        }

        throw new InvalidOperationException(failure);
    }

    private static T? Parse<T>(string text)
        where T : class
    {
        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(text[start..(end + 1)], Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
