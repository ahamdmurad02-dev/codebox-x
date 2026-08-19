using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeBoxX.Models;

namespace CodeBoxX.Services;

public sealed class GeminiService : IDisposable
{
    public const string ModelName = "gemini-3.1-flash-lite";
    private const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/models/" + ModelName + ":generateContent";
    private readonly Func<string?> _apiKeyProvider;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public GeminiService(Func<string?> apiKeyProvider, HttpClient? client = null)
    {
        _apiKeyProvider = apiKeyProvider;
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        _ownsClient = client is null;
    }

    public Task<GeminiResult> TestConnectionAsync(CancellationToken cancellationToken = default) => TestConnectionAsync(null, cancellationToken);

    public async Task<GeminiResult> TestConnectionAsync(string? temporaryApiKey, CancellationToken cancellationToken = default)
    {
        return await GenerateAsync(new AiRequestContext
        {
            Action = AiEditorAction.Chat,
            UserPrompt = "Reply exactly with: CodeBox X connection successful."
        }, cancellationToken, temporaryApiKey);
    }

    public Task<GeminiResult> GenerateAsync(AiRequestContext context, CancellationToken cancellationToken = default) => GenerateAsync(context, cancellationToken, null);

    private async Task<GeminiResult> GenerateAsync(AiRequestContext context, CancellationToken cancellationToken, string? temporaryApiKey)
    {
        var apiKey = (temporaryApiKey ?? _apiKeyProvider())?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return GeminiResult.Fail(GeminiFailureKind.MissingApiKey, "Add a Gemini API key in AI Settings before sending a request.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Add("x-goog-api-key", apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var payload = new
            {
                systemInstruction = new
                {
                    parts = new[] { new { text = "You are CodeBox X AI Assistant. Help with local software development. Be concise, correct, and explain uncertainty. Never claim to have executed code or accessed files you were not given. For code transformations, return the complete replacement code in one fenced code block followed by a short explanation." } }
                },
                contents = new[]
                {
                    new { role = "user", parts = new[] { new { text = BuildPrompt(context) } } }
                },
                generationConfig = new { temperature = 0.2, maxOutputTokens = 8192 }
            };
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) return GeminiResult.Fail(MapStatus(response.StatusCode), MessageForStatus(response.StatusCode));

            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("promptFeedback", out var feedback) && feedback.TryGetProperty("blockReason", out var reason) && reason.ValueKind == JsonValueKind.String)
            {
                return GeminiResult.Fail(GeminiFailureKind.SafetyBlocked, "Gemini blocked this request. Rephrase the prompt or remove sensitive content.");
            }

            var text = ExtractText(document.RootElement);
            return string.IsNullOrWhiteSpace(text)
                ? GeminiResult.Fail(GeminiFailureKind.EmptyResponse, "Gemini returned an empty response. Try a more specific request.")
                : GeminiResult.Ok(text.Trim());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GeminiResult.Fail(GeminiFailureKind.Timeout, "The Gemini request timed out. Check your connection and try again.");
        }
        catch (HttpRequestException)
        {
            return GeminiResult.Fail(GeminiFailureKind.Network, "CodeBox X could not reach Gemini. Check your internet connection and try again.");
        }
        catch (JsonException)
        {
            return GeminiResult.Fail(GeminiFailureKind.EmptyResponse, "Gemini returned an unreadable response. Try again later.");
        }
        catch (Exception)
        {
            return GeminiResult.Fail(GeminiFailureKind.Unknown, "The Gemini request could not be completed. Check AI Settings and try again.");
        }
    }

    public static string ExtractInsertableCode(string response)
    {
        var start = response.IndexOf("```", StringComparison.Ordinal);
        if (start < 0) return response.Trim();
        var contentStart = response.IndexOf('\n', start);
        if (contentStart < 0) return response.Trim();
        var end = response.IndexOf("```", contentStart + 1, StringComparison.Ordinal);
        return (end < 0 ? response[(contentStart + 1)..] : response[(contentStart + 1)..end]).TrimEnd();
    }

    private static string BuildPrompt(AiRequestContext context)
    {
        var source = string.IsNullOrWhiteSpace(context.SelectedCode) ? "(No code is selected.)" : TrimForRequest(context.SelectedCode, 50_000);
        var fileContext = $"Current file: {context.CurrentFileName ?? "unsaved file"}; language: {context.CurrentLanguage ?? "plain text"}.";
        var task = context.Action switch
        {
            AiEditorAction.Explain => "Explain the selected code clearly, including purpose, important logic, and likely pitfalls.",
            AiEditorAction.Fix => "Find defects in the selected code and produce a corrected replacement. State assumptions briefly.",
            AiEditorAction.Refactor => "Refactor the selected code for readability, maintainability, and safe behavior. Preserve intended behavior.",
            AiEditorAction.Generate => "Generate production-ready code that satisfies the requested description. Return only the generated code in a fenced code block, then concise notes.",
            AiEditorAction.AddComments => "Add clear, useful comments to the selected code without changing its behavior. Return a complete replacement.",
            AiEditorAction.ProjectQuestion => "Answer the project question using only the provided project summary and current-file context. State when information is unavailable.",
            _ => "Answer the developer question using the provided code and project context."
        };
        var project = string.IsNullOrWhiteSpace(context.ProjectSummary) ? string.Empty : $"\nProject summary:\n{TrimForRequest(context.ProjectSummary, 12_000)}";
        return $"{task}\n\n{fileContext}\n\nUser request:\n{context.UserPrompt}\n\nSelected/current code:\n```\n{source}\n```{project}";
    }

    private static string ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array) return string.Empty;
        var output = new StringBuilder();
        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts)) continue;
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String) output.Append(text.GetString());
            }
        }
        return output.ToString();
    }

    private static GeminiFailureKind MapStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => GeminiFailureKind.InvalidApiKey,
        (HttpStatusCode)429 => GeminiFailureKind.RateLimited,
        HttpStatusCode.NotFound => GeminiFailureKind.ModelUnavailable,
        _ => GeminiFailureKind.Unknown
    };

    private static string MessageForStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Gemini rejected the API key. Verify it in AI Settings.",
        (HttpStatusCode)429 => "Gemini rate limit reached. Wait a moment and try again.",
        HttpStatusCode.NotFound => "Gemini 3.1 Flash-Lite is unavailable for this API key or endpoint.",
        HttpStatusCode.BadRequest => "Gemini could not process this request. Try a shorter or more specific prompt.",
        _ => "Gemini returned an unexpected service error. Try again later."
    };

    private static string TrimForRequest(string text, int limit) => text.Length <= limit ? text : text[..limit] + "\n[Truncated by CodeBox X]";

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}
