using System.IO.Compression;

namespace CodeBoxX.Services;

public sealed class WebsitePublishService
{
    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "obj", "node_modules", "publish"
    };

    public PublishResult Publish(string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return PublishResult.Fail("Open a website project folder before publishing.");
        }

        var indexPath = Path.Combine(workspacePath, "index.html");
        if (!File.Exists(indexPath))
        {
            return PublishResult.Fail("Publish requires an index.html file in the workspace root. Create or save index.html, then try again.");
        }

        try
        {
            var outputDirectory = Path.Combine(workspacePath, "publish");
            Directory.CreateDirectory(outputDirectory);
            var outputPath = Path.Combine(outputDirectory, "site.zip");
            var temporaryPath = Path.Combine(outputDirectory, $"site-{Guid.NewGuid():N}.zip");

            using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
            {
                foreach (var sourcePath in Directory.EnumerateFiles(workspacePath, "*", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(workspacePath, sourcePath);
                    if (ShouldExclude(relativePath)) continue;
                    archive.CreateEntryFromFile(sourcePath, relativePath, CompressionLevel.Optimal);
                }
            }

            File.Move(temporaryPath, outputPath, overwrite: true);
            return PublishResult.Ok(outputPath);
        }
        catch (Exception ex)
        {
            return PublishResult.Fail($"CodeBox X could not create the publish package: {ex.Message}");
        }
    }

    private static bool ShouldExclude(string relativePath)
    {
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => IgnoredDirectoryNames.Contains(segment));
    }
}

public sealed class PublishResult
{
    public bool Success { get; init; }
    public string OutputPath { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;

    public static PublishResult Ok(string outputPath) => new()
    {
        Success = true,
        OutputPath = outputPath,
        Message = $"Website publish package created: {outputPath}"
    };

    public static PublishResult Fail(string message) => new() { Message = message };
}
