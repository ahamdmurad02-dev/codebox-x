namespace CodeBoxX.Models;

public enum AiEditorAction
{
    Chat,
    Explain,
    Fix,
    Refactor,
    Generate,
    AddComments,
    ProjectQuestion
}

public enum GeminiFailureKind
{
    None,
    MissingApiKey,
    InvalidApiKey,
    RateLimited,
    ModelUnavailable,
    Network,
    Timeout,
    EmptyResponse,
    SafetyBlocked,
    Unknown
}

public sealed class GeminiResult
{
    public bool Success { get; init; }
    public string Text { get; init; } = string.Empty;
    public GeminiFailureKind FailureKind { get; init; }
    public string UserMessage { get; init; } = string.Empty;

    public static GeminiResult Ok(string text) => new() { Success = true, Text = text };
    public static GeminiResult Fail(GeminiFailureKind kind, string userMessage) => new() { Success = false, FailureKind = kind, UserMessage = userMessage };
}

public sealed class AiChatMessage
{
    public string Role { get; init; } = "Assistant";
    public string Text { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public bool IsAssistant => string.Equals(Role, "Assistant", StringComparison.OrdinalIgnoreCase);
    public string Header => IsAssistant ? "Gemini 3.1 Flash-Lite" : "You";
}

public sealed class AiRequestContext
{
    public AiEditorAction Action { get; init; }
    public string UserPrompt { get; init; } = string.Empty;
    public string? SelectedCode { get; init; }
    public string? CurrentFileName { get; init; }
    public string? CurrentLanguage { get; init; }
    public string? ProjectSummary { get; init; }
}
