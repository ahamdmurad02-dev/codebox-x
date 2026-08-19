using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeBoxX.Models;

namespace CodeBoxX.Services;

public sealed class ExtensionMarketplaceService
{
    private const int MaximumPackageBytes = 1_000_000;
    private static readonly HashSet<string> AllowedPermissions = new(StringComparer.OrdinalIgnoreCase)
    {
        "editor.theme",
        "editor.diagnostics",
        "editor.productivity"
    };

    private readonly string _stateDirectory;
    private readonly string _catalogDirectory;
    private readonly string _installedDirectory;
    private readonly string _statePath;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly Dictionary<string, ExtensionPackage> _catalog = new(StringComparer.OrdinalIgnoreCase);
    private List<InstalledExtension> _installed = [];

    public event EventHandler? ExtensionsChanged;

    public ExtensionMarketplaceService()
    {
        _stateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeBoxX", "Extensions");
        _catalogDirectory = Path.Combine(_stateDirectory, "MarketplaceCatalog");
        _installedDirectory = Path.Combine(_stateDirectory, "Installed");
        _statePath = Path.Combine(_stateDirectory, "installed.json");
        Directory.CreateDirectory(_catalogDirectory);
        Directory.CreateDirectory(_installedDirectory);
        EnsureSamplePackages();
        Refresh();
    }

    public IReadOnlyList<ExtensionPackage> Catalog => _catalog.Values.OrderByDescending(package => package.Manifest.IsFeatured).ThenBy(package => package.Manifest.Name).ToList();
    public IReadOnlyList<InstalledExtension> InstalledState => _installed.OrderBy(item => item.Id).ToList();

    public void Refresh()
    {
        _catalog.Clear();
        foreach (var packageFile in Directory.EnumerateFiles(_catalogDirectory, "*.cbxext"))
        {
            if (TryReadVerifiedPackage(packageFile, out var package, out _)) _catalog[package.Manifest.Id] = package;
        }
        _installed = LoadInstalledState();
        ExtensionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<string> GetCategories() => Catalog.Select(package => package.Manifest.Category).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(category => category).ToList();

    public bool IsInstalled(string id) => _installed.Any(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
    public bool IsEnabled(string id) => _installed.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))?.IsEnabled == true;

    public ExtensionPackage? GetPackage(string id)
    {
        if (_catalog.TryGetValue(id, out var catalogPackage)) return catalogPackage;
        var installedPath = GetInstalledPackagePath(id);
        return TryReadVerifiedPackage(installedPath, out var package, out _) ? package : null;
    }

    public IReadOnlyList<(ExtensionPackage Package, InstalledExtension State, bool UpdateAvailable)> GetInstalledPackages()
    {
        var results = new List<(ExtensionPackage Package, InstalledExtension State, bool UpdateAvailable)>();
        foreach (var state in _installed)
        {
            var package = ReadInstalledPackage(state.Id) ?? GetPackage(state.Id);
            if (package is null) continue;
            var updateTarget = package.Manifest.UpdateVersion ?? package.Manifest.Version;
            results.Add((package, state, CompareVersions(state.Version, updateTarget) < 0));
        }
        return results.OrderBy(item => item.Package.Manifest.Name).ToList();
    }

    public bool TryInstall(string id, out string message)
    {
        if (!_catalog.TryGetValue(id, out var package))
        {
            message = "The selected extension is not available in the local marketplace catalog.";
            return false;
        }

        var sourcePath = GetCatalogPackagePath(id);
        if (!TryReadVerifiedPackage(sourcePath, out package, out message)) return false;

        try
        {
            File.Copy(sourcePath, GetInstalledPackagePath(id), overwrite: true);
            var existing = _installed.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (existing is null) _installed.Add(new InstalledExtension { Id = id, Version = package.Manifest.Version, IsEnabled = true });
            else { existing.Version = package.Manifest.Version; existing.IsEnabled = true; }
            SaveInstalledState();
            ExtensionsChanged?.Invoke(this, EventArgs.Empty);
            message = $"Installed {package.Manifest.Name} {package.Manifest.Version}.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Installation failed safely: {ex.Message}";
            return false;
        }
    }

    public bool TryUninstall(string id, out string message)
    {
        var state = _installed.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        if (state is null)
        {
            message = "The extension is not installed.";
            return false;
        }
        try
        {
            var installedPath = GetInstalledPackagePath(id);
            if (File.Exists(installedPath)) File.Delete(installedPath);
            _installed.Remove(state);
            SaveInstalledState();
            ExtensionsChanged?.Invoke(this, EventArgs.Empty);
            message = "Extension uninstalled.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Uninstall failed safely: {ex.Message}";
            return false;
        }
    }

    public bool TrySetEnabled(string id, bool enabled, out string message)
    {
        var state = _installed.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        if (state is null)
        {
            message = "Install the extension before changing its status.";
            return false;
        }
        state.IsEnabled = enabled;
        SaveInstalledState();
        ExtensionsChanged?.Invoke(this, EventArgs.Empty);
        message = enabled ? "Extension enabled." : "Extension disabled.";
        return true;
    }

    public bool TryUpdate(string id, out string message)
    {
        var state = _installed.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        if (state is null || !_catalog.TryGetValue(id, out var package))
        {
            message = "The extension cannot be updated because it is not installed or catalog data is unavailable.";
            return false;
        }
        var targetVersion = package.Manifest.UpdateVersion;
        if (string.IsNullOrWhiteSpace(targetVersion) || CompareVersions(state.Version, targetVersion) >= 0)
        {
            message = "The extension is already up to date.";
            return false;
        }

        try
        {
            var updated = ClonePackage(package);
            updated.Manifest.Version = targetVersion;
            updated.Manifest.UpdateVersion = null;
            WriteVerifiedPackage(GetInstalledPackagePath(id), updated);
            state.Version = targetVersion;
            SaveInstalledState();
            ExtensionsChanged?.Invoke(this, EventArgs.Empty);
            message = $"Updated {updated.Manifest.Name} to {targetVersion}.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Update failed safely: {ex.Message}";
            return false;
        }
    }

    public ThemeDefinition? GetEnabledTheme(string? preferredId)
    {
        if (!string.IsNullOrWhiteSpace(preferredId) && IsEnabled(preferredId))
        {
            var preferred = ReadInstalledPackage(preferredId);
            if (preferred?.Theme is not null) return preferred.Theme;
        }
        return null;
    }

    public IReadOnlyList<EditorDiagnostic> Analyze(EditorDocument document)
    {
        var diagnostics = new List<EditorDiagnostic>();
        var source = document.Text;
        if (source.Length > 500_000) return diagnostics;

        foreach (var (package, state, _) in GetInstalledPackages().Where(item => item.State.IsEnabled && item.Package.Manifest.Types.Contains(ExtensionType.Linter)))
        {
            foreach (var rule in package.LinterRules.Where(rule => rule.Languages.Count == 0 || rule.Languages.Contains(document.LanguageId, StringComparer.OrdinalIgnoreCase)))
            {
                try
                {
                    foreach (Match match in Regex.Matches(source, rule.Pattern, RegexOptions.Multiline))
                    {
                        var line = source[..match.Index].Count(character => character == '\n') + 1;
                        var lastNewLine = source.LastIndexOf('\n', Math.Max(0, match.Index - 1));
                        var column = match.Index - lastNewLine;
                        diagnostics.Add(new EditorDiagnostic
                        {
                            ExtensionId = package.Manifest.Id,
                            ExtensionName = package.Manifest.Name,
                            Severity = rule.Severity,
                            Line = line,
                            Column = column,
                            Length = Math.Max(1, match.Length),
                            Message = rule.Message,
                            FilePath = document.FilePath
                        });
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed package rule is ignored at analysis time; package validation protects normal installs.
                }
            }
        }
        return diagnostics.OrderByDescending(diagnostic => diagnostic.Severity).ThenBy(diagnostic => diagnostic.Line).ToList();
    }

    public bool TryValidateExternalPackage(string packagePath, out ExtensionPackage? package, out string message)
    {
        package = null;
        if (!string.Equals(Path.GetExtension(packagePath), ".cbxext", StringComparison.OrdinalIgnoreCase))
        {
            message = "Only .cbxext data packages are supported. Executable and script packages are rejected.";
            return false;
        }
        if (!File.Exists(packagePath))
        {
            message = "The selected package does not exist.";
            return false;
        }
        if (new FileInfo(packagePath).Length > MaximumPackageBytes)
        {
            message = "The extension package exceeds the 1 MB safety limit.";
            return false;
        }
        if (!TryReadVerifiedPackage(packagePath, out var parsedPackage, out message)) return false;
        package = parsedPackage;
        return true;
    }

    private ExtensionPackage? ReadInstalledPackage(string id) => TryReadVerifiedPackage(GetInstalledPackagePath(id), out var package, out _) ? package : null;

    private bool TryReadVerifiedPackage(string path, out ExtensionPackage package, out string message)
    {
        package = new ExtensionPackage();
        try
        {
            if (!File.Exists(path)) { message = "Package file was not found."; return false; }
            var data = File.ReadAllBytes(path);
            if (data.Length == 0 || data.Length > MaximumPackageBytes) { message = "Package size is invalid."; return false; }
            package = JsonSerializer.Deserialize<ExtensionPackage>(data, _json) ?? throw new InvalidDataException("Package content is empty.");
            if (!ValidatePackage(package, out message)) return false;
            var expectedHash = ComputePackageHash(package);
            if (!string.Equals(package.Manifest.PackageHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                message = "Package integrity validation failed. The package may be corrupted or modified.";
                return false;
            }
            message = "Package validated.";
            return true;
        }
        catch (JsonException)
        {
            message = "Package is not valid JSON extension data.";
            return false;
        }
        catch (Exception ex)
        {
            message = $"Package validation failed: {ex.Message}";
            return false;
        }
    }

    private static bool ValidatePackage(ExtensionPackage package, out string message)
    {
        var manifest = package.Manifest;
        if (manifest.SchemaVersion != "1.0" || !Regex.IsMatch(manifest.Id ?? string.Empty, "^[a-z0-9]+(?:[.-][a-z0-9]+)+$"))
        {
            message = "Package manifest has an unsupported schema or invalid identifier.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.Version) || manifest.Types.Count == 0)
        {
            message = "Package manifest is missing required metadata.";
            return false;
        }
        if (manifest.RequiredPermissions.Any(permission => !AllowedPermissions.Contains(permission)))
        {
            message = "Package requests unsupported permissions and was rejected.";
            return false;
        }
        if (manifest.Types.Contains(ExtensionType.SyntaxTheme) && package.Theme is null)
        {
            message = "Theme extension has no theme definition.";
            return false;
        }
        if (manifest.Types.Contains(ExtensionType.Linter) && package.LinterRules.Count == 0)
        {
            message = "Linter extension has no validated rules.";
            return false;
        }
        if (package.LinterRules.Any(rule => string.IsNullOrWhiteSpace(rule.Pattern) || string.IsNullOrWhiteSpace(rule.Message)))
        {
            message = "Linter rules are incomplete.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(manifest.PackageHash))
        {
            message = "Package does not include an integrity hash.";
            return false;
        }
        message = "Package manifest is valid.";
        return true;
    }

    private void EnsureSamplePackages()
    {
        WriteSampleIfMissing(new ExtensionPackage
        {
            Manifest = new ExtensionManifest
            {
                Id = "codebox.midnight-aurora",
                Name = "Midnight Aurora Theme",
                Version = "1.0.0",
                Description = "A low-contrast midnight syntax theme with aurora accents for focused coding.",
                Publisher = "CodeBox X Samples",
                Category = "Syntax Themes",
                Types = [ExtensionType.SyntaxTheme],
                RequiredPermissions = ["editor.theme"],
                IsFeatured = true,
                IsPopular = true,
                PublishedAt = DateTimeOffset.UtcNow.AddDays(-14)
            },
            Theme = new ThemeDefinition
            {
                Name = "Midnight Aurora",
                WindowBackground = "#111827", PanelBackground = "#172033", EditorBackground = "#0F172A", Surface = "#1E293B", Border = "#334155",
                Text = "#E2E8F0", MutedText = "#94A3B8", Accent = "#38BDF8", AccentHover = "#7DD3FC", Selection = "#164E63",
                TerminalBackground = "#020617", TerminalText = "#CFFAFE", Comment = "#94A3B8", String = "#FCA5A5", Number = "#FDE68A", Keyword = "#67E8F9"
            }
        });

        WriteSampleIfMissing(new ExtensionPackage
        {
            Manifest = new ExtensionManifest
            {
                Id = "codebox.clean-code-linter",
                Name = "Clean Code Linter",
                Version = "1.0.0",
                Description = "Flags TODO markers and trailing whitespace in Python, C#, JavaScript, and TypeScript files.",
                Publisher = "CodeBox X Samples",
                Category = "Linters",
                Types = [ExtensionType.Linter],
                RequiredPermissions = ["editor.diagnostics"],
                SupportedLanguages = ["Python", "C#", "JavaScript", "TypeScript"],
                IsFeatured = true,
                IsPopular = true,
                PublishedAt = DateTimeOffset.UtcNow.AddDays(-7)
            },
            LinterRules =
            [
                new LinterRule { Id = "todo-marker", Pattern = "TODO", Message = "Resolve or track this TODO before release.", Severity = DiagnosticSeverity.Warning, Languages = ["Python", "C#", "JavaScript", "TypeScript"] },
                new LinterRule { Id = "trailing-whitespace", Pattern = @"[ \t]+$", Message = "Trailing whitespace detected.", Severity = DiagnosticSeverity.Information, Languages = ["Python", "C#", "JavaScript", "TypeScript"] }
            ]
        });

        WriteSampleIfMissing(new ExtensionPackage
        {
            Manifest = new ExtensionManifest
            {
                Id = "codebox.focus-tools",
                Name = "Focus Tools",
                Version = "1.0.0",
                UpdateVersion = "1.1.0",
                Description = "Adds a focused coding note template for lightweight project planning.",
                Publisher = "CodeBox X Samples",
                Category = "Productivity",
                Types = [ExtensionType.Productivity, ExtensionType.EditorTool],
                RequiredPermissions = ["editor.productivity"],
                IsPopular = true,
                PublishedAt = DateTimeOffset.UtcNow.AddDays(-2)
            },
            Productivity = new ProductivityDefinition { CommandName = "Insert Focus Note", Description = "Inserts a structured focus note in the active editor.", TemplateText = "# Focus Note\n\n- Goal:\n- Next step:\n- Blocker:\n" }
        });
    }

    private void WriteSampleIfMissing(ExtensionPackage package)
    {
        var path = GetCatalogPackagePath(package.Manifest.Id);
        if (File.Exists(path) && TryReadVerifiedPackage(path, out _, out _)) return;
        WriteVerifiedPackage(path, package);
    }

    private void WriteVerifiedPackage(string path, ExtensionPackage package)
    {
        package.Manifest.PackageHash = ComputePackageHash(package);
        File.WriteAllText(path, JsonSerializer.Serialize(package, _json), new UTF8Encoding(false));
    }

    private string ComputePackageHash(ExtensionPackage package)
    {
        var clone = ClonePackage(package);
        clone.Manifest.PackageHash = string.Empty;
        var payload = JsonSerializer.SerializeToUtf8Bytes(clone, _json);
        return Convert.ToHexString(SHA256.HashData(payload));
    }

    private ExtensionPackage ClonePackage(ExtensionPackage package) => JsonSerializer.Deserialize<ExtensionPackage>(JsonSerializer.Serialize(package, _json), _json) ?? new ExtensionPackage();
    private List<InstalledExtension> LoadInstalledState()
    {
        try { return File.Exists(_statePath) ? JsonSerializer.Deserialize<List<InstalledExtension>>(File.ReadAllText(_statePath), _json) ?? [] : []; }
        catch { return []; }
    }
    private void SaveInstalledState() => File.WriteAllText(_statePath, JsonSerializer.Serialize(_installed, _json));
    private string GetCatalogPackagePath(string id) => Path.Combine(_catalogDirectory, id + ".cbxext");
    private string GetInstalledPackagePath(string id) => Path.Combine(_installedDirectory, id + ".cbxext");

    private static int CompareVersions(string left, string right)
    {
        return Version.TryParse(left, out var parsedLeft) && Version.TryParse(right, out var parsedRight) ? parsedLeft.CompareTo(parsedRight) : string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
