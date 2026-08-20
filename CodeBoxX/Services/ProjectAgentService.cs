using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeBoxX.Models;

namespace CodeBoxX.Services;

public sealed class ProjectAgentService
{
    private const int MaxWorkspaceFiles = 120;
    private const int MaxIncludedFiles = 24;
    private const int MaxCharactersPerFile = 9_000;
    private const int MaxWorkspaceCharacters = 80_000;
    private const int MaxProposedChanges = 40;
    private const int MaxProposedFileCharacters = 180_000;
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "dist", "build", ".codebox"
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".zip", ".7z", ".rar", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".pdf", ".mp3", ".mp4", ".webm", ".woff", ".woff2", ".ttf", ".db"
    };

    private static readonly Regex SensitiveAssignmentPattern = new(@"(?im)(?<key>\b(?:api[_-]?key|token|password|secret|private[_-]?key|connectionstring)\b\s*(?:=|:)\s*)(?<value>[^\r\n]+)", RegexOptions.Compiled);
    private static readonly Regex SensitiveJsonPattern = new(@"(?im)(?<key>\x22(?:api[_-]?key|token|password|secret|private[_-]?key|connectionstring)\x22\s*:\s*)\x22[^\x22]*\x22", RegexOptions.Compiled);

    private AgentUndoEntry? _lastUndo;

    public AgentWorkspaceSnapshot CreateWorkspaceSnapshot(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return new AgentWorkspaceSnapshot
            {
                UserMessage = "Open a workspace folder, then grant the Agent permission to read it.",
                Summary = "No workspace folder is open."
            };
        }

        try
        {
            var root = Path.GetFullPath(workspacePath);
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => IsWorkspaceTextFile(root, path))
                .Take(MaxWorkspaceFiles)
                .ToList();

            var summary = new StringBuilder();
            summary.AppendLine($"Workspace: {Path.GetFileName(root)}");
            summary.AppendLine("Files visible to the Agent:");
            foreach (var file in files)
            {
                summary.Append("- ").Append(Path.GetRelativePath(root, file)).AppendLine();
            }

            var includedFiles = 0;
            var includedCharacters = 0;
            foreach (var file in files)
            {
                if (includedFiles >= MaxIncludedFiles || includedCharacters >= MaxWorkspaceCharacters) break;
                var content = TryReadText(file, MaxCharactersPerFile);
                if (content is null) continue;
                var relative = Path.GetRelativePath(root, file);
                var safeContent = RedactSensitiveValues(content);
                var remaining = MaxWorkspaceCharacters - includedCharacters;
                if (safeContent.Length > remaining) safeContent = safeContent[..remaining] + "\n[Truncated by CodeBox X Agent]";
                summary.AppendLine().Append("FILE: ").AppendLine(relative).AppendLine("```").AppendLine(safeContent).AppendLine("```");
                includedFiles++;
                includedCharacters += safeContent.Length;
            }

            return new AgentWorkspaceSnapshot
            {
                IsAvailable = true,
                WorkspacePath = root,
                FileCount = files.Count,
                IncludedContentFileCount = includedFiles,
                Summary = summary.ToString(),
                UserMessage = files.Count == 0
                    ? "Agent workspace access granted. This workspace is empty, but the Agent can still prepare a reviewed plan to create a new safe project file."
                    : $"Agent workspace access granted: {files.Count} file(s) listed and {includedFiles} file(s) read with sensitive values redacted."
            };
        }
        catch (UnauthorizedAccessException)
        {
            return new AgentWorkspaceSnapshot { WorkspacePath = workspacePath, UserMessage = "CodeBox X does not have permission to read this workspace.", Summary = "Workspace access was denied." };
        }
        catch (IOException)
        {
            return new AgentWorkspaceSnapshot { WorkspacePath = workspacePath, UserMessage = "CodeBox X could not read the workspace. Close any locked files and try again.", Summary = "Workspace could not be read." };
        }
    }

    public IReadOnlyList<AgentSearchHit> SearchWorkspace(string? workspacePath, string query)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath) || string.IsNullOrWhiteSpace(query)) return [];
        var root = Path.GetFullPath(workspacePath);
        var hits = new List<AgentSearchHit>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(path => IsWorkspaceTextFile(root, path)).Take(MaxWorkspaceFiles))
            {
                var content = TryReadText(path, MaxCharactersPerFile);
                if (content is null) continue;
                var lines = RedactSensitiveValues(content).Replace("\r\n", "\n").Split('\n');
                for (var index = 0; index < lines.Length; index++)
                {
                    if (!lines[index].Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                    hits.Add(new AgentSearchHit
                    {
                        RelativePath = Path.GetRelativePath(root, path),
                        LineNumber = index + 1,
                        Preview = lines[index].Trim()[..Math.Min(lines[index].Trim().Length, 180)]
                    });
                    if (hits.Count >= 60) return hits;
                }
            }
        }
        catch
        {
            // Search failures are represented by an empty result rather than interrupting the editor.
        }
        return hits;
    }

    public bool TryParseProposal(string response, string? workspacePath, out AgentProposal proposal, out string userMessage)
    {
        proposal = new AgentProposal();
        userMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            userMessage = "Open a workspace folder before asking the Agent to prepare file changes.";
            return false;
        }

        try
        {
            var json = ExtractJsonObject(response);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var summary = ReadString(root, "summary");
            var explanation = ReadString(root, "explanation");
            if (!root.TryGetProperty("changes", out var changesElement) || changesElement.ValueKind != JsonValueKind.Array)
            {
                userMessage = "The Agent response did not include a valid change plan. Ask it to propose the changes again.";
                return false;
            }

            var changes = new List<AgentFileChange>();
            foreach (var item in changesElement.EnumerateArray())
            {
                if (changes.Count >= MaxProposedChanges)
                {
                    userMessage = $"The Agent proposed more than {MaxProposedChanges} file changes. Split the request into smaller changes.";
                    return false;
                }

                var operationText = ReadString(item, "operation");
                var relativePath = ReadString(item, "path");
                var content = ReadString(item, "content");
                var reason = ReadString(item, "reason");
                if (!Enum.TryParse<AgentChangeOperation>(operationText, ignoreCase: true, out var operation))
                {
                    userMessage = "The Agent proposed an unsupported file operation. Only create, modify, and delete can be reviewed.";
                    return false;
                }
                if (!TryResolveWorkspacePath(workspacePath, relativePath, out var _, out var normalizedPath) || !IsSafeProposedFile(normalizedPath))
                {
                    userMessage = "The Agent proposed a file outside the safe workspace scope or a protected/binary file. No changes were staged.";
                    return false;
                }
                if (operation != AgentChangeOperation.Delete && string.IsNullOrWhiteSpace(content))
                {
                    userMessage = $"The proposed {operation.ToString().ToLowerInvariant()} change for '{normalizedPath}' has no file content.";
                    return false;
                }
                if (content.Length > MaxProposedFileCharacters)
                {
                    userMessage = $"The proposed file '{normalizedPath}' is too large to apply safely. Split the request into smaller changes.";
                    return false;
                }
                if (changes.Any(change => string.Equals(change.RelativePath, normalizedPath, StringComparison.OrdinalIgnoreCase)))
                {
                    userMessage = $"The Agent proposed multiple operations for '{normalizedPath}'. Ask it for one clear change per file.";
                    return false;
                }

                changes.Add(new AgentFileChange
                {
                    Operation = operation,
                    RelativePath = normalizedPath,
                    Content = operation == AgentChangeOperation.Delete ? string.Empty : content,
                    Reason = string.IsNullOrWhiteSpace(reason) ? "No explanation supplied." : reason.Trim()
                });
            }

            proposal = new AgentProposal
            {
                Summary = string.IsNullOrWhiteSpace(summary) ? "Agent proposed workspace changes." : summary.Trim(),
                Explanation = string.IsNullOrWhiteSpace(explanation) ? "Review each file before accepting these changes." : explanation.Trim(),
                Changes = changes
            };
            if (!proposal.HasChanges) userMessage = "The Agent did not propose any file changes.";
            return true;
        }
        catch (JsonException)
        {
            userMessage = "The Agent did not return a valid structured change plan. No files were changed.";
            return false;
        }
        catch
        {
            userMessage = "CodeBox X could not validate the Agent change plan safely. No files were changed.";
            return false;
        }
    }

    public AgentApplyResult ApplyAcceptedProposal(AgentProposal proposal, string? workspacePath)
    {
        if (!proposal.HasChanges) return AgentApplyResult.Fail("There are no Agent changes to apply.");
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath)) return AgentApplyResult.Fail("The workspace is no longer available. No changes were applied.");

        var prepared = new List<(AgentFileChange Change, string FullPath)>();
        foreach (var change in proposal.Changes)
        {
            if (!TryResolveWorkspacePath(workspacePath, change.RelativePath, out var fullPath, out var normalizedPath) || !string.Equals(normalizedPath, change.RelativePath, StringComparison.OrdinalIgnoreCase) || !IsSafeProposedFile(normalizedPath))
                return AgentApplyResult.Fail("An Agent change no longer passes workspace safety validation. No changes were applied.");
            prepared.Add((change, fullPath));
        }

        var undoEntries = new List<AgentUndoEntryItem>();
        try
        {
            foreach (var (change, fullPath) in prepared)
            {
                var existed = File.Exists(fullPath);
                var original = existed ? File.ReadAllBytes(fullPath) : null;
                undoEntries.Add(new AgentUndoEntryItem(fullPath, existed, original));

                switch (change.Operation)
                {
                    case AgentChangeOperation.Create:
                    case AgentChangeOperation.Modify:
                        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                        File.WriteAllText(fullPath, change.Content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                        break;
                    case AgentChangeOperation.Delete:
                        if (existed) File.Delete(fullPath);
                        break;
                }
            }

            _lastUndo = new AgentUndoEntry(undoEntries);
            return AgentApplyResult.Ok($"Applied {prepared.Count} accepted Agent change(s). Use Undo Agent Changes to restore the prior files.", prepared.Select(item => item.FullPath).ToList());
        }
        catch (UnauthorizedAccessException)
        {
            RestoreEntries(undoEntries);
            return AgentApplyResult.Fail("CodeBox X does not have permission to apply one or more Agent changes. Previous files were restored.");
        }
        catch (IOException)
        {
            RestoreEntries(undoEntries);
            return AgentApplyResult.Fail("CodeBox X could not write one or more Agent changes. Previous files were restored.");
        }
        catch
        {
            RestoreEntries(undoEntries);
            return AgentApplyResult.Fail("CodeBox X could not apply the Agent change plan safely. Previous files were restored.");
        }
    }

    public AgentApplyResult UndoLastChanges()
    {
        if (_lastUndo is null) return AgentApplyResult.Fail("There are no accepted Agent changes to undo.");
        try
        {
            RestoreEntries(_lastUndo.Items);
            var paths = _lastUndo.Items.Select(item => item.FullPath).ToList();
            _lastUndo = null;
            return AgentApplyResult.Ok($"Restored {paths.Count} file(s) changed by the Agent.", paths);
        }
        catch
        {
            return AgentApplyResult.Fail("CodeBox X could not restore the Agent changes safely. Check file permissions and try again.");
        }
    }

    private static bool IsWorkspaceTextFile(string workspacePath, string path)
    {
        var relative = Path.GetRelativePath(workspacePath, path);
        if (!TryNormalizeRelativePath(relative, out var normalized) || !IsSafeProposedFile(normalized)) return false;
        try { return new FileInfo(path).Length <= 1_000_000; }
        catch { return false; }
    }

    private static bool IsSafeProposedFile(string relativePath)
    {
        if (!TryNormalizeRelativePath(relativePath, out var normalized)) return false;
        var segments = normalized.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(segment => ExcludedDirectories.Contains(segment))) return false;
        var fileName = Path.GetFileName(normalized);
        if (fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase) || fileName.Equals("settings.json", StringComparison.OrdinalIgnoreCase)) return false;
        if (fileName.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".key", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".p12", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".keystore", StringComparison.OrdinalIgnoreCase)) return false;
        return !BinaryExtensions.Contains(Path.GetExtension(normalized));
    }

    private static bool TryResolveWorkspacePath(string workspacePath, string relativePath, out string fullPath, out string normalizedPath)
    {
        fullPath = string.Empty;
        normalizedPath = string.Empty;
        if (!TryNormalizeRelativePath(relativePath, out normalizedPath)) return false;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath)) + Path.DirectorySeparatorChar;
            fullPath = Path.GetFullPath(Path.Combine(root, normalizedPath));
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryNormalizeRelativePath(string value, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)) return false;
        var candidate = value.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (candidate.Split(Path.DirectorySeparatorChar).Any(segment => string.IsNullOrWhiteSpace(segment) || segment == "." || segment == "..")) return false;
        normalizedPath = candidate;
        return true;
    }

    private static string? TryReadText(string path, int maximumCharacters)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[maximumCharacters + 1];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            if (count == 0) return string.Empty;
            var text = new string(buffer, 0, Math.Min(count, maximumCharacters));
            return count > maximumCharacters ? text + "\n[Truncated by CodeBox X Agent]" : text;
        }
        catch
        {
            return null;
        }
    }

    private static string RedactSensitiveValues(string value)
    {
        var redactedJson = SensitiveJsonPattern.Replace(value, match => match.Groups["key"].Value + "\"[redacted]\"");
        return SensitiveAssignmentPattern.Replace(redactedJson, match => match.Groups["key"].Value + "[redacted]");
    }

    private static string ExtractJsonObject(string response)
    {
        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        if (start < 0 || end <= start) throw new JsonException();
        return response[start..(end + 1)];
    }

    private static string ReadString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static void RestoreEntries(IEnumerable<AgentUndoEntryItem> entries)
    {
        foreach (var entry in entries.Reverse())
        {
            if (entry.Existed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(entry.FullPath)!);
                File.WriteAllBytes(entry.FullPath, entry.OriginalContent ?? []);
            }
            else if (File.Exists(entry.FullPath))
            {
                File.Delete(entry.FullPath);
            }
        }
    }

    private sealed record AgentUndoEntry(IReadOnlyList<AgentUndoEntryItem> Items);
    private sealed record AgentUndoEntryItem(string FullPath, bool Existed, byte[]? OriginalContent);
}
