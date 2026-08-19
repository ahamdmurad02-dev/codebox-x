using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeBoxX.Models;

namespace CodeBoxX.Services;

public static class PreviewRenderer
{
    public static bool TryRender(EditorDocument document, out string html, out string title, out string error)
    {
        var fileName = document.FilePath ?? document.FileNameHint;
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        title = Path.GetFileName(fileName);
        error = string.Empty;

        try
        {
            switch (extension)
            {
                case ".html":
                case ".htm":
                    html = document.Text;
                    if (string.IsNullOrWhiteSpace(html)) html = BuildPage(title, "<p class=\"empty\">This HTML file is empty.</p>");
                    else html = AddLocalResourceBase(document, html);
                    return true;

                case ".md":
                case ".markdown":
                    html = BuildPage(title, RenderMarkdown(document.Text));
                    return true;

                case ".json":
                    using (var parsedJson = JsonDocument.Parse(document.Text))
                    {
                        var prettyJson = JsonSerializer.Serialize(parsedJson.RootElement, new JsonSerializerOptions { WriteIndented = true });
                        html = BuildPage(title, $"<pre>{WebUtility.HtmlEncode(prettyJson)}</pre>");
                        return true;
                    }

                case ".xml":
                case ".xaml":
                    var xml = XDocument.Parse(document.Text);
                    html = BuildPage(title, $"<pre>{WebUtility.HtmlEncode(xml.ToString())}</pre>");
                    return true;

                case ".txt":
                case ".log":
                    html = BuildPage(title, $"<pre>{WebUtility.HtmlEncode(document.Text)}</pre>");
                    return true;

                default:
                    html = string.Empty;
                    error = $"Live Preview supports HTML, Markdown, JSON, XML/XAML, and text files. '{extension}' cannot be previewed.";
                    return false;
            }
        }
        catch (JsonException ex)
        {
            html = string.Empty;
            error = $"JSON preview could not be generated: {ex.Message}";
            return false;
        }
        catch (System.Xml.XmlException ex)
        {
            html = string.Empty;
            error = $"XML preview could not be generated: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            html = string.Empty;
            error = $"Preview could not be generated: {ex.Message}";
            return false;
        }
    }

    private static string AddLocalResourceBase(EditorDocument document, string html)
    {
        // WebBrowser.NavigateToString uses an about: document. Without a base
        // URI, <script src="script.js"> becomes about:script.js instead of a
        // path beside the saved HTML file. Respect an explicit author-defined
        // base element and add one only for existing local HTML files.
        if (string.IsNullOrWhiteSpace(document.FilePath) || Regex.IsMatch(html, @"<\s*base\b", RegexOptions.IgnoreCase)) return html;
        try
        {
            var directory = Path.GetDirectoryName(document.FilePath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return html;
            var absoluteDirectory = Path.GetFullPath(directory);
            if (!Path.EndsInDirectorySeparator(absoluteDirectory)) absoluteDirectory += Path.DirectorySeparatorChar;
            var baseTag = $"<base href=\"{new Uri(absoluteDirectory).AbsoluteUri}\" />";
            var headPattern = new Regex(@"<\s*head\b[^>]*>", RegexOptions.IgnoreCase);
            if (headPattern.IsMatch(html))
            {
                return headPattern.Replace(html, match => match.Value + baseTag, 1);
            }
            var htmlPattern = new Regex(@"<\s*html\b[^>]*>", RegexOptions.IgnoreCase);
            if (htmlPattern.IsMatch(html))
            {
                return htmlPattern.Replace(html, match => match.Value + "<head>" + baseTag + "</head>", 1);
            }
            return baseTag + html;
        }
        catch
        {
            // A preview must still render even when a local path cannot be normalized.
            return html;
        }
    }

    private static string BuildPage(string title, string body) => $@"<!doctype html>
<html>
<head>
<meta charset=""utf-8"" />
<title>{WebUtility.HtmlEncode(title)}</title>
<style>
  body {{ margin: 0; padding: 24px; background: #ffffff; color: #1c1f23; font: 15px 'Segoe UI', Arial, sans-serif; line-height: 1.55; }}
  pre {{ margin: 0; padding: 16px; overflow: auto; border: 1px solid #d5d9de; background: #f7f7f8; font: 13px Consolas, 'Cascadia Mono', monospace; white-space: pre-wrap; }}
  code {{ font-family: Consolas, 'Cascadia Mono', monospace; }}
  .empty {{ color: #626971; }}
  blockquote {{ margin-left: 0; padding: 8px 14px; border-left: 3px solid #0067c0; color: #454b52; background: #f7f7f8; }}
</style>
</head>
<body>{body}</body>
</html>";

    private static string RenderMarkdown(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var result = new StringBuilder();
        var inCodeBlock = false;
        var paragraph = new StringBuilder();

        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            result.Append("<p>").Append(InlineMarkdown(paragraph.ToString().Trim())).AppendLine("</p>");
            paragraph.Clear();
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith("```"))
            {
                FlushParagraph();
                result.Append(inCodeBlock ? "</code></pre>" : "<pre><code>");
                inCodeBlock = !inCodeBlock;
                continue;
            }
            if (inCodeBlock)
            {
                result.Append(WebUtility.HtmlEncode(rawLine)).AppendLine();
                continue;
            }
            if (string.IsNullOrWhiteSpace(line)) { FlushParagraph(); continue; }
            var headingLevel = line.TakeWhile(c => c == '#').Count();
            if (headingLevel is >= 1 and <= 6 && line.Length > headingLevel && line[headingLevel] == ' ')
            {
                FlushParagraph();
                result.Append($"<h{headingLevel}>").Append(InlineMarkdown(line[(headingLevel + 1)..])).AppendLine($"</h{headingLevel}>");
            }
            else if (line.StartsWith("> "))
            {
                FlushParagraph();
                result.Append("<blockquote>").Append(InlineMarkdown(line[2..])).AppendLine("</blockquote>");
            }
            else
            {
                if (paragraph.Length > 0) paragraph.Append("<br />");
                paragraph.Append(line);
            }
        }
        FlushParagraph();
        if (inCodeBlock) result.Append("</code></pre>");
        return result.Length == 0 ? "<p class=\"empty\">This Markdown file is empty.</p>" : result.ToString();
    }

    private static string InlineMarkdown(string value)
    {
        var encoded = WebUtility.HtmlEncode(value);
        encoded = System.Text.RegularExpressions.Regex.Replace(encoded, @"`([^`]+)`", "<code>$1</code>");
        encoded = System.Text.RegularExpressions.Regex.Replace(encoded, @"\*\*([^*]+)\*\*", "<strong>$1</strong>");
        encoded = System.Text.RegularExpressions.Regex.Replace(encoded, @"\*([^*]+)\*", "<em>$1</em>");
        return encoded;
    }
}
