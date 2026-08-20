namespace CodeBoxX.Services;

public static class LanguageService
{
    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        [".py"] = "Python", [".cs"] = "C#", [".csproj"] = "C#", [".cpp"] = "C++", [".cxx"] = "C++", [".cc"] = "C++", [".c"] = "C++",
        [".h"] = "C++", [".hpp"] = "C++", [".java"] = "Java", [".js"] = "JavaScript", [".mjs"] = "JavaScript",
        [".ts"] = "TypeScript", [".tsx"] = "TypeScript", [".json"] = "JSON", [".xml"] = "XML", [".xaml"] = "XML",
        [".sql"] = "SQL", [".lua"] = "Lua", [".gd"] = "GDScript", [".md"] = "Markdown", [".markdown"] = "Markdown",
        [".txt"] = "Plain Text", [".log"] = "Plain Text", [".yml"] = "Plain Text", [".yaml"] = "Plain Text"
    };

    public static string GetLanguageId(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Plain Text";
        return Languages.TryGetValue(Path.GetExtension(path), out var language) ? language : "Plain Text";
    }

    public static string GetRunCommand(string filePath)
    {
        var escaped = $"\"{filePath}\"";
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".py" => $"python {escaped}",
            ".js" or ".mjs" => $"node {escaped}",
            ".ts" => $"npx ts-node {escaped}",
            ".cs" => "dotnet run",
            ".java" => $"java {escaped}",
            ".lua" => $"lua {escaped}",
            ".gd" => $"godot --editor {escaped}",
            ".sql" => $"sqlite3 :memory: < {escaped}",
            _ => string.Empty
        };
    }
}
