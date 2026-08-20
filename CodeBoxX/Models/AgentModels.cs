namespace CodeBoxX.Models;

public enum AgentChangeOperation
{
    Create,
    Modify,
    Delete
}

public sealed class AgentFileChange
{
    public AgentChangeOperation Operation { get; init; }
    public string RelativePath { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;

    public string DisplayName => $"{Operation}: {RelativePath}";
    public bool RequiresDeletionConfirmation => Operation == AgentChangeOperation.Delete;
}

public sealed class AgentProposal
{
    public string Summary { get; init; } = string.Empty;
    public string Explanation { get; init; } = string.Empty;
    public IReadOnlyList<AgentFileChange> Changes { get; init; } = [];

    public bool HasChanges => Changes.Count > 0;
    public bool RequiresLargeChangeConfirmation => Changes.Count > 8;
    public bool RequiresDeletionConfirmation => Changes.Any(change => change.RequiresDeletionConfirmation);
    public string ModifiedFilesSummary => HasChanges
        ? string.Join(Environment.NewLine, Changes.Select(change => change.DisplayName))
        : "No file changes were proposed.";
}

public sealed class AgentWorkspaceSnapshot
{
    public bool IsAvailable { get; init; }
    public string WorkspacePath { get; init; } = string.Empty;
    public int FileCount { get; init; }
    public int IncludedContentFileCount { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string UserMessage { get; init; } = string.Empty;
}

public sealed class AgentSearchHit
{
    public string RelativePath { get; init; } = string.Empty;
    public int LineNumber { get; init; }
    public string Preview { get; init; } = string.Empty;

    public string DisplayName => $"{RelativePath}:{LineNumber}  {Preview}";
}

public sealed class AgentApplyResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<string> ChangedPaths { get; init; } = [];

    public static AgentApplyResult Ok(string message, IReadOnlyList<string> changedPaths) => new()
    {
        Success = true,
        Message = message,
        ChangedPaths = changedPaths
    };

    public static AgentApplyResult Fail(string message) => new() { Message = message };
}
