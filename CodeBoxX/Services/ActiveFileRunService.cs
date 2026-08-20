using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace CodeBoxX.Services;

/// <summary>
/// Resolves a safe local command line for the active document without executing it.
/// Runtime and compiler paths are discovered from the user's PATH (or GODOT_PATH),
/// while generated C++ and single-file C# build outputs stay inside the user's temp folder.
/// </summary>
public static class ActiveFileRunService
{
    public static ActiveRunResolution Resolve(string filePath, string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return ActiveRunResolution.Fail("Save the active file before running it.");
        }

        filePath = Path.GetFullPath(filePath);
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".py" => ResolvePython(filePath),
            ".cs" or ".csproj" => ResolveCSharp(filePath, workspacePath),
            _ => ActiveRunResolution.Fail($"Run Active File supports Python and C# files. '{Path.GetExtension(filePath)}' is not configured.")
        };
    }

    private static ActiveRunResolution ResolvePython(string filePath)
    {
        var python = FindOnPath("python.exe", "python");
        if (python is not null)
        {
            return ActiveRunResolution.Success(new ActiveRunRequest(
                $"{Quote(python)} {Quote(filePath)}",
                Path.GetDirectoryName(filePath)!,
                "Python"));
        }

        var launcher = FindOnPath("py.exe", "py");
        if (launcher is not null)
        {
            return ActiveRunResolution.Success(new ActiveRunRequest(
                $"{Quote(launcher)} -3 {Quote(filePath)}",
                Path.GetDirectoryName(filePath)!,
                "Python"));
        }

        return ActiveRunResolution.Fail("Python was not found. Install Python for Windows and enable the python.exe or py.exe launcher, then restart CodeBox X.");
    }

    private static ActiveRunResolution ResolveCSharp(string filePath, string? workspacePath)
    {
        var dotnet = FindOnPath("dotnet.exe", "dotnet");
        if (dotnet is null)
        {
            return ActiveRunResolution.Fail("The .NET SDK was not found. Install a supported .NET SDK, then restart CodeBox X.");
        }

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (extension == ".csproj")
        {
            return ActiveRunResolution.Success(new ActiveRunRequest(
                $"{Quote(dotnet)} run --project {Quote(filePath)}",
                Path.GetDirectoryName(filePath)!,
                "C# (.NET project)"));
        }

        var nearbyProject = FindClosestProject(filePath, workspacePath);
        var projectPath = nearbyProject is not null && Path.GetFileName(filePath).Equals("Program.cs", StringComparison.OrdinalIgnoreCase)
            ? nearbyProject
            : CreateSingleFileProject(filePath);
        var displayName = ReferenceEquals(projectPath, nearbyProject) ? "C# (.NET project)" : "C# active file (.NET SDK)";
        return ActiveRunResolution.Success(new ActiveRunRequest(
            $"{Quote(dotnet)} run --project {Quote(projectPath)}",
            Path.GetDirectoryName(filePath)!,
            displayName));
    }

    private static string? FindClosestProject(string sourceFilePath, string? workspacePath)
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        var workspaceRoot = string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath)
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));

        while (current is not null)
        {
            var project = current.EnumerateFiles("*.csproj", SearchOption.TopDirectoryOnly).OrderBy(file => file.Name).FirstOrDefault();
            if (project is not null) return project.FullName;
            if (workspaceRoot is not null && string.Equals(Path.TrimEndingDirectorySeparator(current.FullName), workspaceRoot, StringComparison.OrdinalIgnoreCase)) break;
            current = current.Parent;
        }

        return null;
    }

    private static string CreateSingleFileProject(string sourceFilePath)
    {
        var directory = CreateTemporaryBuildDirectory("csharp", sourceFilePath);
        var projectPath = Path.Combine(directory, "CodeBoxXActiveFile.csproj");
        var escapedSource = SecurityElement.Escape(sourceFilePath) ?? sourceFilePath;
        var project = $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="{{escapedSource}}" Link="Program.cs" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(projectPath, project, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return projectPath;
    }

    private static string CreateTemporaryBuildDirectory(string language, string sourcePath)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(sourcePath))))[..16];
        var directory = Path.Combine(Path.GetTempPath(), "CodeBoxX", "active-run", language, hash);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string? FindOnPath(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "where.exe",
                        Arguments = Quote(candidate),
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) continue;
                var path = output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(File.Exists);
                if (!string.IsNullOrWhiteSpace(path)) return Path.GetFullPath(path);
            }
            catch
            {
                // A missing where.exe or inaccessible PATH entry should result in a clear resolver message, not a failed editor process.
            }
        }

        return null;
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}

public sealed record ActiveRunRequest(string Command, string WorkingDirectory, string DisplayName);

public sealed record ActiveRunResolution(bool IsSuccess, ActiveRunRequest? Request, string Message)
{
    public static ActiveRunResolution Success(ActiveRunRequest request) => new(true, request, string.Empty);
    public static ActiveRunResolution Fail(string message) => new(false, null, message);
}
