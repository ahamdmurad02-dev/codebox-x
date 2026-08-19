using System.Text.Json.Serialization;

namespace CodeBoxX.Models;

public enum ExtensionType
{
    SyntaxTheme,
    Linter,
    Formatter,
    LanguageSupport,
    EditorTool,
    Productivity
}

public enum DiagnosticSeverity
{
    Error,
    Warning,
    Information
}

public sealed class ExtensionManifest
{
    public string SchemaVersion { get; set; } = "1.0";
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = string.Empty;
    public string Publisher { get; set; } = "CodeBox X";
    public string Category { get; set; } = "Editor Tools";
    public List<ExtensionType> Types { get; set; } = [];
    public List<string> RequiredPermissions { get; set; } = [];
    public List<string> SupportedLanguages { get; set; } = [];
    public string PackageHash { get; set; } = string.Empty;
    public string? UpdateVersion { get; set; }
    public bool IsFeatured { get; set; }
    public bool IsPopular { get; set; }
    public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public string TypeLabel => string.Join(", ", Types.Select(type => type switch
    {
        ExtensionType.SyntaxTheme => "Syntax Theme",
        ExtensionType.Linter => "Linter",
        ExtensionType.Formatter => "Formatter",
        ExtensionType.LanguageSupport => "Language Support",
        ExtensionType.EditorTool => "Editor Tool",
        ExtensionType.Productivity => "Productivity",
        _ => type.ToString()
    }));
}

public sealed class ExtensionPackage
{
    public ExtensionManifest Manifest { get; set; } = new();
    public ThemeDefinition? Theme { get; set; }
    public List<LinterRule> LinterRules { get; set; } = [];
    public ProductivityDefinition? Productivity { get; set; }
}

public sealed class ThemeDefinition
{
    public string Name { get; set; } = "Custom Theme";
    public string WindowBackground { get; set; } = "#1A1C1F";
    public string PanelBackground { get; set; } = "#222529";
    public string EditorBackground { get; set; } = "#181A1D";
    public string Surface { get; set; } = "#2B2F34";
    public string Border { get; set; } = "#3D4248";
    public string Text { get; set; } = "#E6E9ED";
    public string MutedText { get; set; } = "#A9B0B8";
    public string Accent { get; set; } = "#4AA0F3";
    public string AccentHover { get; set; } = "#70B7FF";
    public string Selection { get; set; } = "#264C71";
    public string TerminalBackground { get; set; } = "#0D1013";
    public string TerminalText { get; set; } = "#D5E1EA";
    public string Comment { get; set; } = "#7F9F7F";
    public string String { get; set; } = "#CE9178";
    public string Number { get; set; } = "#B5CEA8";
    public string Keyword { get; set; } = "#569CD6";
}

public sealed class LinterRule
{
    public string Id { get; set; } = string.Empty;
    public string Pattern { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DiagnosticSeverity Severity { get; set; } = DiagnosticSeverity.Warning;
    public List<string> Languages { get; set; } = [];
}

public sealed class ProductivityDefinition
{
    public string CommandName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string TemplateText { get; set; } = string.Empty;
}

public sealed class EditorDiagnostic
{
    public string ExtensionId { get; set; } = string.Empty;
    public string ExtensionName { get; set; } = string.Empty;
    public DiagnosticSeverity Severity { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }
    public int Length { get; set; } = 1;
    public string Message { get; set; } = string.Empty;
    public string? FilePath { get; set; }

    public string Location => $"Ln {Line}, Col {Column}";
    public string SeverityLabel => Severity.ToString();
    public string Display => $"{SeverityLabel}: {Message} ({Location})";
}

public sealed class InstalledExtension
{
    public string Id { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;
}
