namespace CodeBoxX.Models;

public enum MpmProviderKind
{
    Python,
    Node,
    DotNet,
    Unsupported
}

public enum MpmOperationKind
{
    Search,
    Info,
    List,
    Install,
    Uninstall,
    Update,
    Restore,
    Refresh
}

public sealed class MpmPackage
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public bool IsInstalled { get; set; }
    public bool UpdateAvailable { get; set; }
    public bool IsCompatible { get; set; } = true;
    public string CompatibilityMessage { get; set; } = string.Empty;
    public string Status => !IsCompatible ? "Compatibility warning" : !IsInstalled ? "Available" : UpdateAvailable ? "Update available" : "Installed";
    public string VersionLabel => string.IsNullOrWhiteSpace(Version) ? "Version unavailable" : UpdateAvailable && !string.IsNullOrWhiteSpace(LatestVersion) ? $"{Version} → {LatestVersion}" : Version;
}

public sealed class MpmProjectContext
{
    public string WorkspacePath { get; init; } = string.Empty;
    public MpmProviderKind Provider { get; init; }
    public string ProviderLabel { get; init; } = string.Empty;
    public string SourceName { get; init; } = string.Empty;
    public string SourceUrl { get; init; } = string.Empty;
    public string DependencyFilePath { get; init; } = string.Empty;
    public string DetectedProjectPath { get; init; } = string.Empty;
    public string DetectionDetail { get; init; } = string.Empty;
    public string? ProjectFilePath { get; init; }
    public string? InterpreterPath { get; init; }
    public bool IsAvailable => Provider != MpmProviderKind.Unsupported;
    public string UnavailableReason { get; init; } = string.Empty;
}

public sealed class MpmOperationResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string Output { get; init; } = string.Empty;
    public IReadOnlyList<MpmPackage> Packages { get; init; } = [];
    public static MpmOperationResult Ok(string message, string output = "", IReadOnlyList<MpmPackage>? packages = null) => new() { Success = true, Message = message, Output = output, Packages = packages ?? [] };
    public static MpmOperationResult Fail(string message, string output = "") => new() { Message = message, Output = output };
}

public sealed class MpmDependencyConfiguration
{
    public string SchemaVersion { get; set; } = "1.0";
    public string Provider { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<MpmDependency> Dependencies { get; set; } = [];
}

public sealed class MpmDependency
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
}

public sealed class MpmProgressEventArgs : EventArgs
{
    public MpmOperationKind Operation { get; init; }
    public bool IsError { get; init; }
    public string Message { get; init; } = string.Empty;
}
