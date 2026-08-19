using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeBoxX.Models;

namespace CodeBoxX.Services;

/// <summary>
/// Local-first project package manager. Commands are executed without a shell,
/// package names are validated, and script hooks are disabled for Python/Node installs.
/// </summary>
public sealed class MpmService : IDisposable
{
    private static readonly Regex PackageNamePattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public event EventHandler<MpmProgressEventArgs>? ProgressChanged;

    public MpmProjectContext DetectProject(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return Unsupported("Open a project folder before using MPM.");
        }

        try
        {
            var root = Path.GetFullPath(workspacePath);

            var dotnetProject = FindProjectMarker(root, "*.csproj", "*.fsproj", "*.sln");
            var dotnetSource = FindProjectMarker(root, "*.cs");
            if (dotnetProject is not null || dotnetSource is not null)
            {
                var marker = dotnetProject ?? dotnetSource!;
                var detail = dotnetProject is not null
                    ? $"Detected .NET project file: {Path.GetFileName(dotnetProject)}"
                    : $"Detected C# source file: {Path.GetFileName(dotnetSource)}. Add a .csproj or .fsproj file to enable full .NET dependency operations.";
                return CreateContext(root, MpmProviderKind.DotNet, "NuGet / .NET", "NuGet", "https://www.nuget.org/packages/", dotnetProject, null, marker, detail);
            }

            var nodeProject = FindProjectMarker(root, "package.json", "package-lock.json");
            var nodeSource = FindProjectMarker(root, "*.js", "*.ts", "*.tsx");
            if (nodeProject is not null || nodeSource is not null)
            {
                var marker = nodeProject ?? nodeSource!;
                var detail = nodeProject is not null
                    ? $"Detected Node.js project file: {Path.GetFileName(nodeProject)}"
                    : $"Detected JavaScript or TypeScript source file: {Path.GetFileName(nodeSource)}. Add package.json to track npm dependencies.";
                return CreateContext(root, MpmProviderKind.Node, "npm / Node.js", "npm Registry", "https://www.npmjs.com/package/", null, null, marker, detail);
            }

            var pythonProject = FindProjectMarker(root, "requirements.txt", "pyproject.toml");
            var pythonSource = FindProjectMarker(root, "*.py");
            if (pythonProject is not null || pythonSource is not null)
            {
                var marker = pythonProject ?? pythonSource!;
                var detail = pythonProject is not null
                    ? $"Detected Python project file: {Path.GetFileName(pythonProject)}"
                    : $"Detected Python source file: {Path.GetFileName(pythonSource)}. MPM will create .codebox-mpm.json to track managed dependencies.";
                var environmentPython = Path.Combine(root, ".codebox-mpm", "python", "Scripts", "python.exe");
                return CreateContext(root, MpmProviderKind.Python, "pip / Python", "PyPI", "https://pypi.org/project/", null, environmentPython, marker, detail);
            }
        }
        catch (Exception ex)
        {
            return Unsupported($"MPM could not inspect the project: {ex.Message}");
        }

        return Unsupported("Add a supported project file or source file, then refresh MPM.");
    }

    public Task<MpmOperationResult> AddProjectFileAsync(string? workspacePath, MpmProviderKind provider, string fileName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath)) return Task.FromResult(MpmOperationResult.Fail("Open a workspace folder before creating a project file."));
        if (string.IsNullOrWhiteSpace(fileName) || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)) return Task.FromResult(MpmOperationResult.Fail("Enter a file name only; folder paths and command text are not accepted."));

        var root = Path.GetFullPath(workspacePath);
        if (!IsAllowedProjectFile(provider, fileName)) return Task.FromResult(MpmOperationResult.Fail("Choose a supported MPM project file: requirements.txt or pyproject.toml for Python, package.json for Node.js, or .csproj/.fsproj for .NET."));
        var target = Path.Combine(root, fileName);
        if (File.Exists(target)) return Task.FromResult(MpmOperationResult.Fail($"{fileName} already exists. MPM did not overwrite it."));

        try
        {
            File.WriteAllText(target, BuildProjectFileContent(root, provider, fileName), new UTF8Encoding(false));
            return Task.FromResult(MpmOperationResult.Ok($"Created {fileName}. Refresh MPM to detect and load this project.", target));
        }
        catch (Exception ex)
        {
            return Task.FromResult(MpmOperationResult.Fail($"MPM could not create {fileName}: {ex.Message}"));
        }
    }

    public async Task<MpmOperationResult> SearchAsync(MpmProjectContext context, string query, CancellationToken cancellationToken = default)
    {
        if (!ValidateContextAndPackage(context, query, out var error)) return MpmOperationResult.Fail(error);
        return await ExecuteExclusiveAsync(MpmOperationKind.Search, async () => context.Provider switch
        {
            MpmProviderKind.Python => await SearchPythonAsync(context, query, cancellationToken),
            MpmProviderKind.Node => await SearchNodeAsync(context, query, cancellationToken),
            MpmProviderKind.DotNet => await SearchNuGetAsync(context, query, cancellationToken),
            _ => MpmOperationResult.Fail(context.UnavailableReason)
        }, cancellationToken);
    }

    public async Task<MpmOperationResult> InfoAsync(MpmProjectContext context, string packageName, CancellationToken cancellationToken = default)
    {
        var search = await SearchAsync(context, packageName, cancellationToken);
        if (!search.Success || search.Packages.Count == 0) return search;
        var package = search.Packages[0];
        var details = $"Name: {package.Name}\nVersion: {package.VersionLabel}\nStatus: {package.Status}\nSource: {package.Source}\n\n{package.Description}";
        return MpmOperationResult.Ok("Package information loaded.", details, search.Packages);
    }

    public async Task<MpmOperationResult> ListAsync(MpmProjectContext context, CancellationToken cancellationToken = default)
    {
        if (!context.IsAvailable) return MpmOperationResult.Fail(context.UnavailableReason);
        return await ExecuteExclusiveAsync(MpmOperationKind.List, async () =>
        {
            var packages = context.Provider switch
            {
                MpmProviderKind.Python => await ListPythonAsync(context, cancellationToken),
                MpmProviderKind.Node => await ListNodeAsync(context, cancellationToken),
                MpmProviderKind.DotNet => await ListDotNetAsync(context, cancellationToken),
                _ => []
            };
            var config = LoadConfiguration(context);
            ApplyConfigurationMetadata(packages, config);
            return MpmOperationResult.Ok($"{packages.Count} package(s) loaded for {context.ProviderLabel}.", string.Join(Environment.NewLine, packages.Select(p => $"{p.Name} {p.VersionLabel} — {p.Status}")), packages);
        }, cancellationToken);
    }

    public async Task<MpmOperationResult> InstallAsync(MpmProjectContext context, string packageName, CancellationToken cancellationToken = default)
    {
        if (!ValidateContextAndPackage(context, packageName, out var error)) return MpmOperationResult.Fail(error);
        return await ExecuteExclusiveAsync(MpmOperationKind.Install, async () =>
        {
            Report(MpmOperationKind.Install, false, $"Validating {packageName} from {context.SourceName}...");
            var metadata = await SearchCoreAsync(context, packageName, cancellationToken);
            if (!metadata.Success) return metadata;

            Report(MpmOperationKind.Install, false, $"Installing {packageName}. Package scripts are disabled where supported.");
            var command = context.Provider switch
            {
                MpmProviderKind.Python => await InstallPythonAsync(context, packageName, cancellationToken),
                MpmProviderKind.Node => await RunProcessAsync("npm.cmd", ["install", "--ignore-scripts", "--no-audit", "--no-fund", packageName], context.WorkspacePath, MpmOperationKind.Install, cancellationToken),
                MpmProviderKind.DotNet => await RunProcessAsync("dotnet", ["add", context.ProjectFilePath!, "package", packageName], context.WorkspacePath, MpmOperationKind.Install, cancellationToken),
                _ => CommandResult.Fail(context.UnavailableReason)
            };
            if (!command.Success) return MpmOperationResult.Fail($"Installation failed safely: {command.Summary}", command.CombinedOutput);

            var packages = await ListCoreAsync(context, cancellationToken);
            var installed = packages.FirstOrDefault(package => SamePackage(package.Name, packageName));
            UpdateConfiguration(context, configuration =>
            {
                var existing = configuration.Dependencies.FirstOrDefault(dependency => SamePackage(dependency.Name, packageName));
                if (installed is null) return;
                if (existing is null) configuration.Dependencies.Add(new MpmDependency { Name = installed.Name, Version = installed.Version });
                else { existing.Name = installed.Name; existing.Version = installed.Version; }
            });
            ApplyConfigurationMetadata(packages, LoadConfiguration(context));
            return MpmOperationResult.Ok($"Installed {packageName} from {context.SourceName}.", command.CombinedOutput, packages);
        }, cancellationToken);
    }

    public async Task<MpmOperationResult> UninstallAsync(MpmProjectContext context, string packageName, CancellationToken cancellationToken = default)
    {
        if (!ValidateContextAndPackage(context, packageName, out var error)) return MpmOperationResult.Fail(error);
        return await ExecuteExclusiveAsync(MpmOperationKind.Uninstall, async () =>
        {
            Report(MpmOperationKind.Uninstall, false, $"Removing {packageName} from the project environment...");
            var command = context.Provider switch
            {
                MpmProviderKind.Python => await UninstallPythonAsync(context, packageName, cancellationToken),
                MpmProviderKind.Node => await RunProcessAsync("npm.cmd", ["uninstall", "--ignore-scripts", "--no-audit", "--no-fund", packageName], context.WorkspacePath, MpmOperationKind.Uninstall, cancellationToken),
                MpmProviderKind.DotNet => await RunProcessAsync("dotnet", ["remove", context.ProjectFilePath!, "package", packageName], context.WorkspacePath, MpmOperationKind.Uninstall, cancellationToken),
                _ => CommandResult.Fail(context.UnavailableReason)
            };
            if (!command.Success) return MpmOperationResult.Fail($"Removal failed safely: {command.Summary}", command.CombinedOutput);

            UpdateConfiguration(context, configuration => configuration.Dependencies.RemoveAll(dependency => SamePackage(dependency.Name, packageName)));
            var packages = await ListCoreAsync(context, cancellationToken);
            ApplyConfigurationMetadata(packages, LoadConfiguration(context));
            return MpmOperationResult.Ok($"Removed {packageName} from the project.", command.CombinedOutput, packages);
        }, cancellationToken);
    }

    public async Task<MpmOperationResult> UpdateAsync(MpmProjectContext context, CancellationToken cancellationToken = default)
    {
        if (!context.IsAvailable) return MpmOperationResult.Fail(context.UnavailableReason);
        return await ExecuteExclusiveAsync(MpmOperationKind.Update, async () =>
        {
            var configured = LoadConfiguration(context).Dependencies;
            if (configured.Count == 0) return MpmOperationResult.Fail("No MPM-managed dependencies are recorded for this project. Install a package first or use Restore after creating the dependency file.");
            Report(MpmOperationKind.Update, false, $"Updating {configured.Count} recorded dependency package(s)...");
            var output = new StringBuilder();
            foreach (var dependency in configured.ToList())
            {
                var command = context.Provider switch
                {
                    MpmProviderKind.Python => await InstallPythonAsync(context, dependency.Name, cancellationToken, upgrade: true),
                    MpmProviderKind.Node => await RunProcessAsync("npm.cmd", ["install", "--ignore-scripts", "--no-audit", "--no-fund", dependency.Name + "@latest"], context.WorkspacePath, MpmOperationKind.Update, cancellationToken),
                    MpmProviderKind.DotNet => await RunProcessAsync("dotnet", ["add", context.ProjectFilePath!, "package", dependency.Name], context.WorkspacePath, MpmOperationKind.Update, cancellationToken),
                    _ => CommandResult.Fail(context.UnavailableReason)
                };
                output.AppendLine(command.CombinedOutput);
                if (!command.Success) return MpmOperationResult.Fail($"Update stopped safely for {dependency.Name}: {command.Summary}", output.ToString());
            }

            var packages = await ListCoreAsync(context, cancellationToken);
            UpdateConfiguration(context, configuration =>
            {
                foreach (var dependency in configuration.Dependencies)
                {
                    var installed = packages.FirstOrDefault(package => SamePackage(package.Name, dependency.Name));
                    if (installed is not null) dependency.Version = installed.Version;
                }
            });
            ApplyConfigurationMetadata(packages, LoadConfiguration(context));
            return MpmOperationResult.Ok("Recorded dependencies were updated.", output.ToString(), packages);
        }, cancellationToken);
    }

    public async Task<MpmOperationResult> RestoreAsync(MpmProjectContext context, CancellationToken cancellationToken = default)
    {
        if (!context.IsAvailable) return MpmOperationResult.Fail(context.UnavailableReason);
        return await ExecuteExclusiveAsync(MpmOperationKind.Restore, async () =>
        {
            var configuration = LoadConfiguration(context);
            if (configuration.Dependencies.Count == 0) return MpmOperationResult.Fail("No dependencies are recorded in .codebox-mpm.json for this project.");
            var output = new StringBuilder();
            foreach (var dependency in configuration.Dependencies)
            {
                var result = await InstallFromConfiguredVersionAsync(context, dependency, cancellationToken);
                output.AppendLine(result.CombinedOutput);
                if (!result.Success) return MpmOperationResult.Fail($"Restore stopped safely for {dependency.Name}: {result.Summary}", output.ToString());
            }
            var packages = await ListCoreAsync(context, cancellationToken);
            ApplyConfigurationMetadata(packages, LoadConfiguration(context));
            return MpmOperationResult.Ok("Project dependencies restored from .codebox-mpm.json.", output.ToString(), packages);
        }, cancellationToken);
    }

    public async Task<MpmOperationResult> RefreshAsync(MpmProjectContext context, CancellationToken cancellationToken = default) => await ListAsync(context, cancellationToken);

    private async Task<MpmOperationResult> SearchCoreAsync(MpmProjectContext context, string query, CancellationToken cancellationToken) => context.Provider switch
    {
        MpmProviderKind.Python => await SearchPythonAsync(context, query, cancellationToken),
        MpmProviderKind.Node => await SearchNodeAsync(context, query, cancellationToken),
        MpmProviderKind.DotNet => await SearchNuGetAsync(context, query, cancellationToken),
        _ => MpmOperationResult.Fail(context.UnavailableReason)
    };

    private async Task<IReadOnlyList<MpmPackage>> ListCoreAsync(MpmProjectContext context, CancellationToken cancellationToken) => context.Provider switch
    {
        MpmProviderKind.Python => await ListPythonAsync(context, cancellationToken),
        MpmProviderKind.Node => await ListNodeAsync(context, cancellationToken),
        MpmProviderKind.DotNet => await ListDotNetAsync(context, cancellationToken),
        _ => []
    };

    private async Task<MpmOperationResult> SearchPythonAsync(MpmProjectContext context, string query, CancellationToken cancellationToken)
    {
        var python = await ResolveSystemPythonAsync(cancellationToken);
        if (python is null) return MpmOperationResult.Fail("Python 3 was not found. Install Python 3 and enable PATH before using Python MPM packages.");
        var result = await RunProcessAsync(python, ["-m", "pip", "index", "versions", "--disable-pip-version-check", "--no-input", query], context.WorkspacePath, MpmOperationKind.Search, cancellationToken);
        if (!result.Success) return MpmOperationResult.Fail($"Package metadata was not found on PyPI for '{query}'.", result.CombinedOutput);
        var firstLine = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var version = Regex.Match(firstLine, @"\(([^)]+)\)").Groups[1].Value;
        var package = new MpmPackage { Id = query, Name = query, Version = version, LatestVersion = version, Description = "Python package metadata obtained from PyPI.", Source = context.SourceUrl + query };
        return MpmOperationResult.Ok("Package metadata loaded from PyPI.", result.CombinedOutput, [package]);
    }

    private async Task<MpmOperationResult> SearchNodeAsync(MpmProjectContext context, string query, CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync("npm.cmd", ["view", query, "version", "description", "dist.tarball", "--json", "--ignore-scripts"], context.WorkspacePath, MpmOperationKind.Search, cancellationToken);
        if (!result.Success) return MpmOperationResult.Fail($"Package metadata was not found in npm for '{query}'.", result.CombinedOutput);
        try
        {
            using var json = JsonDocument.Parse(result.StandardOutput);
            var root = json.RootElement;
            var version = root.TryGetProperty("version", out var versionElement) ? versionElement.GetString() ?? string.Empty : string.Empty;
            var description = root.TryGetProperty("description", out var descriptionElement) ? descriptionElement.GetString() ?? "npm package metadata." : "npm package metadata.";
            return MpmOperationResult.Ok("Package metadata loaded from npm.", result.CombinedOutput, [new MpmPackage { Id = query, Name = query, Version = version, LatestVersion = version, Description = description, Source = context.SourceUrl + query }]);
        }
        catch { return MpmOperationResult.Fail("npm returned package metadata in an unexpected format.", result.CombinedOutput); }
    }

    private async Task<MpmOperationResult> SearchNuGetAsync(MpmProjectContext context, string query, CancellationToken cancellationToken)
    {
        try
        {
            var id = query.ToLowerInvariant();
            var response = await Http.GetAsync($"https://api.nuget.org/v3-flatcontainer/{id}/index.json", cancellationToken);
            if (!response.IsSuccessStatusCode) return MpmOperationResult.Fail($"Package metadata was not found on NuGet for '{query}'.", $"NuGet returned {(int)response.StatusCode}.");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var versions = json.RootElement.GetProperty("versions").EnumerateArray().Select(item => item.GetString() ?? string.Empty).Where(item => item.Length > 0).ToList();
            var latest = versions.LastOrDefault() ?? string.Empty;
            return MpmOperationResult.Ok("Package metadata loaded from NuGet.", $"Available versions: {string.Join(", ", versions.TakeLast(12))}", [new MpmPackage { Id = query, Name = query, Version = latest, LatestVersion = latest, Description = "NuGet package metadata.", Source = context.SourceUrl + query }]);
        }
        catch (Exception ex) { return MpmOperationResult.Fail($"NuGet package metadata could not be loaded: {ex.Message}"); }
    }

    private async Task<IReadOnlyList<MpmPackage>> ListPythonAsync(MpmProjectContext context, CancellationToken cancellationToken)
    {
        var python = context.InterpreterPath;
        if (string.IsNullOrWhiteSpace(python) || !File.Exists(python)) return LoadPythonDeclaredDependencies(context);
        var result = await RunProcessAsync(python, ["-m", "pip", "list", "--format=json", "--disable-pip-version-check"], context.WorkspacePath, MpmOperationKind.List, cancellationToken, reportOutput: false);
        if (!result.Success) return LoadPythonDeclaredDependencies(context);
        try
        {
            using var json = JsonDocument.Parse(result.StandardOutput);
            return json.RootElement.EnumerateArray().Select(item => new MpmPackage
            {
                Id = item.GetProperty("name").GetString() ?? string.Empty,
                Name = item.GetProperty("name").GetString() ?? string.Empty,
                Version = item.GetProperty("version").GetString() ?? string.Empty,
                Source = context.SourceUrl + (item.GetProperty("name").GetString() ?? string.Empty),
                IsInstalled = true
            }).Where(package => !IsPythonToolingPackage(package.Name)).OrderBy(package => package.Name).ToList();
        }
        catch { return LoadPythonDeclaredDependencies(context); }
    }

    private async Task<IReadOnlyList<MpmPackage>> ListNodeAsync(MpmProjectContext context, CancellationToken cancellationToken)
    {
        if (!File.Exists(Path.Combine(context.WorkspacePath, "package.json"))) return LoadNodeDeclaredDependencies(context);
        var result = await RunProcessAsync("npm.cmd", ["ls", "--depth=0", "--json", "--ignore-scripts"], context.WorkspacePath, MpmOperationKind.List, cancellationToken, reportOutput: false);
        if (!result.Success) return LoadNodeDeclaredDependencies(context);
        try
        {
            using var json = JsonDocument.Parse(result.StandardOutput);
            if (!json.RootElement.TryGetProperty("dependencies", out var dependencies)) return LoadNodeDeclaredDependencies(context);
            return dependencies.EnumerateObject().Select(item => new MpmPackage
            {
                Id = item.Name,
                Name = item.Name,
                Version = item.Value.TryGetProperty("version", out var version) ? version.GetString() ?? string.Empty : string.Empty,
                Source = context.SourceUrl + item.Name,
                IsInstalled = true
            }).OrderBy(package => package.Name).ToList();
        }
        catch { return LoadNodeDeclaredDependencies(context); }
    }

    private async Task<IReadOnlyList<MpmPackage>> ListDotNetAsync(MpmProjectContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.ProjectFilePath)) return [];
        var result = await RunProcessAsync("dotnet", ["list", context.ProjectFilePath, "package", "--include-transitive"], context.WorkspacePath, MpmOperationKind.List, cancellationToken, reportOutput: false);
        var packages = new List<MpmPackage>();
        foreach (var line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(line, @"^\s*>?\s*([A-Za-z0-9_.-]+)\s+([0-9][A-Za-z0-9_.-]*)");
            if (match.Success) packages.Add(new MpmPackage { Id = match.Groups[1].Value, Name = match.Groups[1].Value, Version = match.Groups[2].Value, Source = context.SourceUrl + match.Groups[1].Value, IsInstalled = true });
        }
        return packages.Count > 0 ? packages.OrderBy(package => package.Name).ToList() : LoadDotNetDeclaredDependencies(context);
    }

    private static IReadOnlyList<MpmPackage> LoadPythonDeclaredDependencies(MpmProjectContext context)
    {
        try
        {
            var requirements = Path.Combine(context.WorkspacePath, "requirements.txt");
            if (File.Exists(requirements))
            {
                return File.ReadLines(requirements)
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0 && !line.StartsWith('#'))
                    .Select(line => Regex.Match(line, @"^([A-Za-z0-9_.-]+)(?:\s*(?:==|>=|<=|~=|!=|>|<)\s*([^;\s]+))?"))
                    .Where(match => match.Success)
                    .Select(match => new MpmPackage { Id = match.Groups[1].Value, Name = match.Groups[1].Value, Version = match.Groups[2].Value, Source = requirements, Description = "Declared in requirements.txt; install or restore to add it to the project environment." })
                    .OrderBy(package => package.Name).ToList();
            }

            var pyproject = Path.Combine(context.WorkspacePath, "pyproject.toml");
            if (!File.Exists(pyproject)) return [];
            var text = File.ReadAllText(pyproject);
            var dependenciesBlock = Regex.Match(text, @"dependencies\s*=\s*\[(?<items>[\s\S]*?)\]", RegexOptions.IgnoreCase).Groups["items"].Value;
            return Regex.Matches(dependenciesBlock, "[\\\"']([A-Za-z0-9_.-]+)(?:\\s*(?:==|>=|<=|~=|!=|>|<)\\s*([^\\\"'\\s,]+))?[\\\"']")
                .Select(match => new MpmPackage { Id = match.Groups[1].Value, Name = match.Groups[1].Value, Version = match.Groups[2].Value, Source = pyproject, Description = "Declared in pyproject.toml; install or restore to add it to the project environment." })
                .OrderBy(package => package.Name).ToList();
        }
        catch { return []; }
    }

    private static IReadOnlyList<MpmPackage> LoadNodeDeclaredDependencies(MpmProjectContext context)
    {
        try
        {
            var packageJson = Path.Combine(context.WorkspacePath, "package.json");
            if (!File.Exists(packageJson)) return LoadNodeLockDependencies(context);
            using var json = JsonDocument.Parse(File.ReadAllText(packageJson));
            var results = new Dictionary<string, MpmPackage>(StringComparer.OrdinalIgnoreCase);
            foreach (var sectionName in new[] { "dependencies", "devDependencies" })
            {
                if (!json.RootElement.TryGetProperty(sectionName, out var section)) continue;
                foreach (var item in section.EnumerateObject())
                {
                    results[item.Name] = new MpmPackage
                    {
                        Id = item.Name,
                        Name = item.Name,
                        Version = item.Value.GetString() ?? string.Empty,
                        Source = packageJson,
                        Description = $"Declared in package.json ({sectionName}); install or restore to add it locally."
                    };
                }
            }
            return results.Values.OrderBy(package => package.Name).ToList();
        }
        catch { return LoadNodeLockDependencies(context); }
    }

    private static IReadOnlyList<MpmPackage> LoadNodeLockDependencies(MpmProjectContext context)
    {
        try
        {
            var lockFile = Path.Combine(context.WorkspacePath, "package-lock.json");
            if (!File.Exists(lockFile)) return [];
            using var json = JsonDocument.Parse(File.ReadAllText(lockFile));
            JsonElement dependencies;
            if (json.RootElement.TryGetProperty("packages", out var packages) && packages.TryGetProperty("", out var rootPackage) && rootPackage.TryGetProperty("dependencies", out dependencies))
            {
                return dependencies.EnumerateObject().Select(item => new MpmPackage { Id = item.Name, Name = item.Name, Version = item.Value.GetString() ?? string.Empty, Source = lockFile, Description = "Declared in package-lock.json; run restore to install it locally." }).OrderBy(package => package.Name).ToList();
            }
            if (json.RootElement.TryGetProperty("dependencies", out dependencies))
            {
                return dependencies.EnumerateObject().Select(item => new MpmPackage { Id = item.Name, Name = item.Name, Version = item.Value.TryGetProperty("version", out var version) ? version.GetString() ?? string.Empty : string.Empty, Source = lockFile, Description = "Declared in package-lock.json; run restore to install it locally." }).OrderBy(package => package.Name).ToList();
            }
        }
        catch { }
        return [];
    }

    private static IReadOnlyList<MpmPackage> LoadDotNetDeclaredDependencies(MpmProjectContext context)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(context.ProjectFilePath) || context.ProjectFilePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || !File.Exists(context.ProjectFilePath)) return [];
            var project = XDocument.Load(context.ProjectFilePath);
            return project.Descendants().Where(element => element.Name.LocalName == "PackageReference")
                .Select(element =>
                {
                    var name = element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value ?? string.Empty;
                    var version = element.Attribute("Version")?.Value ?? element.Elements().FirstOrDefault(child => child.Name.LocalName == "Version")?.Value ?? string.Empty;
                    return new MpmPackage { Id = name, Name = name, Version = version, Source = context.ProjectFilePath, Description = "Declared in the .NET project file; restore packages to install it." };
                }).Where(package => !string.IsNullOrWhiteSpace(package.Name)).OrderBy(package => package.Name).ToList();
        }
        catch { return []; }
    }

    private async Task<CommandResult> InstallPythonAsync(MpmProjectContext context, string packageName, CancellationToken cancellationToken, bool upgrade = false)
    {
        var python = await EnsurePythonEnvironmentAsync(context, cancellationToken);
        if (python is null) return CommandResult.Fail("A project Python environment could not be created.");
        var arguments = new List<string> { "-m", "pip", "install", "--disable-pip-version-check", "--no-input", "--only-binary=:all:" };
        if (upgrade) arguments.Add("--upgrade");
        arguments.Add(packageName);
        return await RunProcessAsync(python, arguments, context.WorkspacePath, upgrade ? MpmOperationKind.Update : MpmOperationKind.Install, cancellationToken);
    }

    private async Task<CommandResult> UninstallPythonAsync(MpmProjectContext context, string packageName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.InterpreterPath) || !File.Exists(context.InterpreterPath)) return CommandResult.Fail("No project Python environment exists. There is no installed project package to remove.");
        return await RunProcessAsync(context.InterpreterPath, ["-m", "pip", "uninstall", "--disable-pip-version-check", "--no-input", "-y", packageName], context.WorkspacePath, MpmOperationKind.Uninstall, cancellationToken);
    }

    private async Task<CommandResult> InstallFromConfiguredVersionAsync(MpmProjectContext context, MpmDependency dependency, CancellationToken cancellationToken)
    {
        var packageReference = string.IsNullOrWhiteSpace(dependency.Version) ? dependency.Name : context.Provider == MpmProviderKind.Node ? dependency.Name + "@" + dependency.Version : dependency.Name + "==" + dependency.Version;
        return context.Provider switch
        {
            MpmProviderKind.Python => await InstallPythonAsync(context, packageReference, cancellationToken),
            MpmProviderKind.Node => await RunProcessAsync("npm.cmd", ["install", "--ignore-scripts", "--no-audit", "--no-fund", packageReference], context.WorkspacePath, MpmOperationKind.Restore, cancellationToken),
            MpmProviderKind.DotNet => await RunProcessAsync("dotnet", ["add", context.ProjectFilePath!, "package", dependency.Name, "--version", dependency.Version], context.WorkspacePath, MpmOperationKind.Restore, cancellationToken),
            _ => CommandResult.Fail(context.UnavailableReason)
        };
    }

    private async Task<string?> EnsurePythonEnvironmentAsync(MpmProjectContext context, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(context.InterpreterPath) && File.Exists(context.InterpreterPath)) return context.InterpreterPath;
        var systemPython = await ResolveSystemPythonAsync(cancellationToken);
        if (systemPython is null)
        {
            Report(MpmOperationKind.Install, true, "Python 3 was not found. Install Python 3 and enable PATH before installing project packages.");
            return null;
        }
        var environmentDirectory = Path.Combine(context.WorkspacePath, ".codebox-mpm", "python");
        Directory.CreateDirectory(Path.GetDirectoryName(environmentDirectory)!);
        Report(MpmOperationKind.Install, false, "Creating an isolated project Python environment in .codebox-mpm\\python...");
        var create = await RunProcessAsync(systemPython, ["-m", "venv", environmentDirectory], context.WorkspacePath, MpmOperationKind.Install, cancellationToken);
        return create.Success && !string.IsNullOrWhiteSpace(context.InterpreterPath) && File.Exists(context.InterpreterPath) ? context.InterpreterPath : null;
    }

    private async Task<string?> ResolveSystemPythonAsync(CancellationToken cancellationToken)
    {
        foreach (var candidate in new[] { "python.exe", "python", "py.exe", "py" })
        {
            var args = candidate.StartsWith("py", StringComparison.OrdinalIgnoreCase) ? new[] { "-3", "--version" } : new[] { "--version" };
            var result = await RunProcessAsync(candidate, args, Environment.CurrentDirectory, MpmOperationKind.Refresh, cancellationToken, reportOutput: false);
            if (result.Success && result.CombinedOutput.Contains("Python", StringComparison.OrdinalIgnoreCase)) return candidate;
        }
        return null;
    }

    private async Task<CommandResult> RunProcessAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, MpmOperationKind operation, CancellationToken cancellationToken, bool reportOutput = true)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Environment.CurrentDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            var output = new StringBuilder();
            var errors = new StringBuilder();
            process.OutputDataReceived += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(args.Data)) return;
                output.AppendLine(args.Data);
                if (reportOutput) Report(operation, false, args.Data);
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(args.Data)) return;
                errors.AppendLine(args.Data);
                if (reportOutput) Report(operation, true, args.Data);
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken);
            var combined = output + errors.ToString();
            return process.ExitCode == 0 ? CommandResult.Ok(combined) : CommandResult.Fail($"{fileName} exited with code {process.ExitCode}.", combined);
        }
        catch (Win32Exception)
        {
            return CommandResult.Fail($"Required command '{fileName}' was not found. Install the matching language toolchain or choose a supported project.");
        }
        catch (OperationCanceledException) { return CommandResult.Fail("MPM operation was cancelled safely."); }
        catch (Exception ex) { return CommandResult.Fail($"MPM command could not run: {ex.Message}"); }
    }

    private async Task<MpmOperationResult> ExecuteExclusiveAsync(MpmOperationKind operation, Func<Task<MpmOperationResult>> action, CancellationToken cancellationToken)
    {
        if (!await _operationGate.WaitAsync(0, cancellationToken)) return MpmOperationResult.Fail("Another MPM operation is already running. Wait for it to finish before starting another package action.");
        try
        {
            Report(operation, false, $"MPM {operation} started.");
            var result = await action();
            Report(operation, !result.Success, result.Success ? $"MPM {operation} completed." : $"MPM {operation} failed: {result.Message}");
            return result;
        }
        finally { _operationGate.Release(); }
    }

    private MpmDependencyConfiguration LoadConfiguration(MpmProjectContext context)
    {
        try
        {
            if (!File.Exists(context.DependencyFilePath)) return NewConfiguration(context);
            var configuration = JsonSerializer.Deserialize<MpmDependencyConfiguration>(File.ReadAllText(context.DependencyFilePath), JsonOptions) ?? NewConfiguration(context);
            return configuration.SchemaVersion == "1.0" ? configuration : NewConfiguration(context);
        }
        catch { return NewConfiguration(context); }
    }

    private void UpdateConfiguration(MpmProjectContext context, Action<MpmDependencyConfiguration> update)
    {
        var configuration = LoadConfiguration(context);
        update(configuration);
        configuration.Provider = context.ProviderLabel;
        configuration.Source = context.SourceUrl;
        configuration.UpdatedAt = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(Path.GetDirectoryName(context.DependencyFilePath)!);
        File.WriteAllText(context.DependencyFilePath, JsonSerializer.Serialize(configuration, JsonOptions), new UTF8Encoding(false));
    }

    private static MpmDependencyConfiguration NewConfiguration(MpmProjectContext context) => new() { Provider = context.ProviderLabel, Source = context.SourceUrl };

    private static void ApplyConfigurationMetadata(IReadOnlyList<MpmPackage> packages, MpmDependencyConfiguration configuration)
    {
        foreach (var package in packages)
        {
            var configured = configuration.Dependencies.FirstOrDefault(dependency => SamePackage(dependency.Name, package.Name));
            if (configured is null) continue;
            if (!string.Equals(configured.Version, package.Version, StringComparison.OrdinalIgnoreCase))
            {
                package.UpdateAvailable = true;
                package.IsCompatible = false;
                package.LatestVersion = package.Version;
                package.CompatibilityMessage = $"Configured version {configured.Version} differs from installed version {package.Version}. Review the dependency before sharing this project.";
            }
        }
    }

    private static bool ValidateContextAndPackage(MpmProjectContext context, string packageName, out string error)
    {
        if (!context.IsAvailable) { error = context.UnavailableReason; return false; }
        if (string.IsNullOrWhiteSpace(packageName) || !PackageNamePattern.IsMatch(packageName))
        {
            error = "Enter a simple package identifier containing only letters, numbers, dots, underscores, or hyphens. Version expressions, command switches, and shell text are not accepted.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static bool IsPythonToolingPackage(string name) => name.Equals("pip", StringComparison.OrdinalIgnoreCase) || name.Equals("setuptools", StringComparison.OrdinalIgnoreCase) || name.Equals("wheel", StringComparison.OrdinalIgnoreCase);
    private static bool SamePackage(string left, string right) => string.Equals(left.Replace("_", "-"), right.Replace("_", "-"), StringComparison.OrdinalIgnoreCase);

    private static string? FindProjectMarker(string root, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            var marker = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                .FirstOrDefault(path => !IsIgnoredProjectPath(path));
            if (marker is not null) return marker;
        }
        return null;
    }

    private static bool IsIgnoredProjectPath(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".codebox-mpm", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAllowedProjectFile(MpmProviderKind provider, string fileName) => provider switch
    {
        MpmProviderKind.Python => fileName.Equals("requirements.txt", StringComparison.OrdinalIgnoreCase) || fileName.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase),
        MpmProviderKind.Node => fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase),
        MpmProviderKind.DotNet => fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static string BuildProjectFileContent(string root, MpmProviderKind provider, string fileName) => provider switch
    {
        MpmProviderKind.Python when fileName.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) => "[project]\nname = \"codebox-project\"\nversion = \"0.1.0\"\nrequires-python = \">=3.9\"\ndependencies = []\n",
        MpmProviderKind.Python => "# Python dependencies managed by CodeBox X MPM\n",
        MpmProviderKind.Node => "{\n  \"name\": \"" + CreateSafeProjectName(root) + "\",\n  \"version\": \"1.0.0\",\n  \"private\": true,\n  \"dependencies\": {}\n}\n",
        MpmProviderKind.DotNet when fileName.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) => "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n",
        MpmProviderKind.DotNet => "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n",
        _ => string.Empty
    };

    private static string CreateSafeProjectName(string root)
    {
        var name = Regex.Replace(Path.GetFileName(root).ToLowerInvariant(), "[^a-z0-9-]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(name) ? "codebox-project" : name;
    }

    private static MpmProjectContext CreateContext(string root, MpmProviderKind provider, string label, string sourceName, string sourceUrl, string? projectFile, string? interpreter, string detectedProjectPath, string detectionDetail) => new()
    {
        WorkspacePath = root,
        Provider = provider,
        ProviderLabel = label,
        SourceName = sourceName,
        SourceUrl = sourceUrl,
        DependencyFilePath = Path.Combine(root, ".codebox-mpm.json"),
        DetectedProjectPath = detectedProjectPath,
        DetectionDetail = detectionDetail,
        ProjectFilePath = projectFile,
        InterpreterPath = interpreter
    };
    private static MpmProjectContext Unsupported(string reason) => new() { Provider = MpmProviderKind.Unsupported, ProviderLabel = "Unsupported project", UnavailableReason = reason, DetectionDetail = reason };
    private void Report(MpmOperationKind operation, bool isError, string message) => ProgressChanged?.Invoke(this, new MpmProgressEventArgs { Operation = operation, IsError = isError, Message = message });
    public void Dispose() => _operationGate.Dispose();

    private readonly record struct CommandResult(bool Success, string Summary, string StandardOutput, string StandardError)
    {
        public string CombinedOutput => StandardOutput + StandardError;
        public static CommandResult Ok(string output) => new(true, "Command completed.", output, string.Empty);
        public static CommandResult Fail(string summary, string output = "") => new(false, summary, string.Empty, output);
    }
}
