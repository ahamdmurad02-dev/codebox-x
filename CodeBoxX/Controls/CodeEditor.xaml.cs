using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using CodeBoxX.Models;

namespace CodeBoxX.Controls;

public partial class CodeEditor : UserControl
{
    private readonly EditorDocument _document;
    private bool _applyingFormatting;
    private ScrollViewer? _scrollViewer;
    private IReadOnlyList<EditorDiagnostic> _diagnostics = [];

    public event EventHandler? ContentChanged;
    public event EventHandler? CaretChanged;
    public event EventHandler<string>? FileDropped;

    public CodeEditor(EditorDocument document)
    {
        InitializeComponent();
        _document = document;
        EditorBox.UndoLimit = 1000;
        Loaded += (_, _) =>
        {
            _scrollViewer = FindVisualChild<ScrollViewer>(EditorBox);
            if (_scrollViewer is not null) _scrollViewer.ScrollChanged += EditorScrollViewer_ScrollChanged;
            SetText(document.Text, preserveUndo: false);
            EditorBox.Focus();
        };
        Unloaded += (_, _) =>
        {
            if (_scrollViewer is not null) _scrollViewer.ScrollChanged -= EditorScrollViewer_ScrollChanged;
        };
    }

    public EditorDocument Document => _document;
    public int CaretLine => EditorBox.Document.ContentStart.GetLineStartPosition(0) is null ? 1 : GetLineNumber(EditorBox.CaretPosition);
    public int CaretColumn
    {
        get
        {
            var lineStart = EditorBox.CaretPosition.GetLineStartPosition(0) ?? EditorBox.Document.ContentStart;
            return lineStart.GetOffsetToPosition(EditorBox.CaretPosition) + 1;
        }
    }

    public string Text => new TextRange(EditorBox.Document.ContentStart, EditorBox.Document.ContentEnd).Text.Replace("\r\n", "\n").TrimEnd('\r', '\n');
    public string SelectedText => EditorBox.Selection.Text.Replace("\r\n", "\n");

    public void SetText(string text, bool preserveUndo = false)
    {
        _applyingFormatting = true;
        try
        {
            if (!preserveUndo) EditorBox.UndoLimit = 0;
            new TextRange(EditorBox.Document.ContentStart, EditorBox.Document.ContentEnd).Text = text;
            UpdateLineNumbers();
            ApplyHighlighting();
        }
        finally
        {
            if (!preserveUndo) EditorBox.UndoLimit = 1000;
            _applyingFormatting = false;
        }
    }

    public void FocusEditor() => EditorBox.Focus();

    public void InsertOrReplaceSelection(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        EditorBox.Selection.Text = text.Replace("\n", Environment.NewLine);
        EditorBox.Focus();
    }

    public void SetEditorFontSize(double size)
    {
        EditorBox.FontSize = size;
        LineNumbersBox.FontSize = size;
    }

    public void SetDiagnostics(IEnumerable<EditorDiagnostic> diagnostics)
    {
        _diagnostics = diagnostics.ToList();
        ApplyHighlighting();
    }

    public void GoTo(int line, int column)
    {
        var source = Text;
        var offset = 0;
        for (var currentLine = 1; currentLine < Math.Max(1, line) && offset < source.Length; currentLine++)
        {
            var nextLine = source.IndexOf('\n', offset);
            offset = nextLine < 0 ? source.Length : nextLine + 1;
        }
        offset = Math.Min(source.Length, offset + Math.Max(0, column - 1));
        var start = GetTextPointerAtOffset(offset);
        var end = GetTextPointerAtOffset(Math.Min(source.Length, offset + 1));
        EditorBox.Selection.Select(start, end);
        EditorBox.Focus();
    }

    public void Undo() { if (EditorBox.CanUndo) EditorBox.Undo(); }
    public void Redo() { if (EditorBox.CanRedo) EditorBox.Redo(); }

    public int ReplaceAll(string find, string replace, bool matchCase)
    {
        if (string.IsNullOrEmpty(find)) return 0;
        var source = Text;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var count = 0;
        var position = 0;
        while ((position = source.IndexOf(find, position, comparison)) >= 0)
        {
            count++;
            position += find.Length;
        }
        if (count > 0) SetText(source.Replace(find, replace, comparison), preserveUndo: true);
        return count;
    }

    public bool FindNext(string term, bool matchCase)
    {
        if (string.IsNullOrEmpty(term)) return false;
        var source = Text;
        var start = Math.Min(EditorBox.CaretPosition.GetOffsetToPosition(EditorBox.Document.ContentEnd), source.Length);
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var index = source.IndexOf(term, start, comparison);
        if (index < 0) index = source.IndexOf(term, 0, comparison);
        if (index < 0) return false;
        var begin = GetTextPointerAtOffset(index);
        var end = GetTextPointerAtOffset(index + term.Length);
        EditorBox.Selection.Select(begin, end);
        EditorBox.Focus();
        return true;
    }

    private void EditorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_applyingFormatting) return;
        _document.Text = Text;
        UpdateLineNumbers();
        ApplyHighlighting();
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EditorBox_SelectionChanged(object sender, RoutedEventArgs e) => CaretChanged?.Invoke(this, EventArgs.Empty);

    private void EditorBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            EditorBox.CaretPosition.InsertTextInRun("    ");
            e.Handled = true;
        }
    }

    private void EditorBox_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void EditorBox_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var file in files.Where(File.Exists)) FileDropped?.Invoke(this, file);
        }
    }

    private void EditorScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        LineNumbersBox.ScrollToVerticalOffset(e.VerticalOffset);
    }

    private void UpdateLineNumbers()
    {
        var lines = Math.Max(1, Text.Count(c => c == '\n') + 1);
        LineNumbersBox.Text = string.Join(Environment.NewLine, Enumerable.Range(1, lines));
    }

    private void ApplyHighlighting()
    {
        if (_applyingFormatting || !IsLoaded) return;
        var selectionStart = EditorBox.Selection.Start.GetOffsetToPosition(EditorBox.Document.ContentStart);
        var selectionEnd = EditorBox.Selection.End.GetOffsetToPosition(EditorBox.Document.ContentStart);
        var source = Text;
        _applyingFormatting = true;
        try
        {
            var all = new TextRange(EditorBox.Document.ContentStart, EditorBox.Document.ContentEnd);
            all.ApplyPropertyValue(TextElement.ForegroundProperty, (Brush)Application.Current.Resources["TextBrush"]);
            all.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Normal);

            var language = _document.LanguageId;
            if (language != "Plain Text" && source.Length <= 250000)
            {
                ApplyMatches(source, @"(?m)^\s*(#|//|--).*$|/\*[\s\S]*?\*/", (Brush)Application.Current.Resources["SyntaxCommentBrush"]);
                ApplyMatches(source, "(?s)\"(?:\\.|[^\"])*\"|'(?:\\.|[^'])*'", (Brush)Application.Current.Resources["SyntaxStringBrush"]);
                ApplyMatches(source, @"\b\d+(?:\.\d+)?\b", (Brush)Application.Current.Resources["SyntaxNumberBrush"]);
                ApplyMatches(source, KeywordPattern(language), (Brush)Application.Current.Resources["SyntaxKeywordBrush"], FontWeights.SemiBold);
                if (language is "JSON" or "XML") ApplyMatches(source, "(?<=\")([A-Za-z_$][\\w$-]*)(?=\"\\s*:)", (Brush)Application.Current.Resources["SyntaxKeywordBrush"]);
            }
            ApplyDiagnosticHighlights(source);
        }
        catch
        {
            // Keep the editor responsive if formatting encounters unusual document structure.
        }
        finally
        {
            _applyingFormatting = false;
            var start = GetTextPointerAtOffset(Math.Max(0, selectionStart));
            var end = GetTextPointerAtOffset(Math.Max(0, selectionEnd));
            EditorBox.Selection.Select(start, end);
        }
    }

    private void ApplyMatches(string source, string pattern, Brush brush, FontWeight? weight = null)
    {
        foreach (Match match in Regex.Matches(source, pattern, RegexOptions.Multiline))
        {
            var range = new TextRange(GetTextPointerAtOffset(match.Index), GetTextPointerAtOffset(match.Index + match.Length));
            range.ApplyPropertyValue(TextElement.ForegroundProperty, brush);
            if (weight.HasValue) range.ApplyPropertyValue(TextElement.FontWeightProperty, weight.Value);
        }
    }

    private void ApplyDiagnosticHighlights(string source)
    {
        foreach (var diagnostic in _diagnostics)
        {
            var offset = 0;
            for (var line = 1; line < diagnostic.Line && offset < source.Length; line++)
            {
                var nextLine = source.IndexOf('\n', offset);
                offset = nextLine < 0 ? source.Length : nextLine + 1;
            }
            offset = Math.Min(source.Length, offset + Math.Max(0, diagnostic.Column - 1));
            var length = Math.Min(Math.Max(1, diagnostic.Length), Math.Max(1, source.Length - offset));
            var range = new TextRange(GetTextPointerAtOffset(offset), GetTextPointerAtOffset(Math.Min(source.Length, offset + length)));
            var resource = diagnostic.Severity switch
            {
                DiagnosticSeverity.Error => "DiagnosticErrorBrush",
                DiagnosticSeverity.Warning => "DiagnosticWarningBrush",
                _ => "DiagnosticInfoBrush"
            };
            range.ApplyPropertyValue(TextElement.BackgroundProperty, (Brush)Application.Current.Resources[resource]);
            range.ApplyPropertyValue(Inline.TextDecorationsProperty, TextDecorations.Underline);
        }
    }

    private static string KeywordPattern(string language) => language switch
    {
        "Python" => @"\b(and|as|assert|async|await|break|class|continue|def|del|elif|else|except|False|finally|for|from|global|if|import|in|is|lambda|None|not|or|pass|raise|return|True|try|while|with|yield)\b",
        "C#" or "C++" or "Java" => @"\b(abstract|bool|break|byte|case|catch|char|class|const|continue|decimal|default|do|double|else|enum|event|explicit|false|final|finally|float|for|foreach|if|int|interface|internal|long|namespace|new|null|object|operator|private|protected|public|readonly|ref|return|sealed|short|static|string|struct|switch|this|throw|true|try|typeof|using|void|while)\b",
        "JavaScript" or "TypeScript" => @"\b(async|await|break|case|catch|class|const|continue|default|delete|do|else|export|extends|false|finally|for|from|function|if|import|in|instanceof|let|new|null|of|return|static|super|switch|this|throw|true|try|typeof|undefined|var|void|while|yield)\b",
        "SQL" => @"\b(SELECT|FROM|WHERE|INSERT|INTO|VALUES|UPDATE|DELETE|CREATE|ALTER|DROP|TABLE|JOIN|LEFT|RIGHT|INNER|OUTER|ON|AS|AND|OR|NOT|NULL|ORDER|BY|GROUP|HAVING|LIMIT)\b",
        "Lua" or "GDScript" => @"\b(and|break|class|continue|do|elif|else|enum|extends|false|for|func|function|if|in|local|match|nil|not|or|pass|return|self|static|then|true|until|var|while)\b",
        "JSON" => @"\b(true|false|null)\b",
        "XML" => @"</?[A-Za-z_][\w:.-]*|/?>",
        "Markdown" => @"(?m)^#{1,6}\s.*$|`[^`]+`|\*\*[^*]+\*\*",
        _ => @"$^"
    };

    private TextPointer GetTextPointerAtOffset(int offset)
    {
        var pointer = EditorBox.Document.ContentStart;
        var remaining = Math.Max(0, offset);
        while (pointer is not null)
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var run = pointer.GetTextInRun(LogicalDirection.Forward);
                if (remaining <= run.Length) return pointer.GetPositionAtOffset(remaining) ?? EditorBox.Document.ContentEnd;
                remaining -= run.Length;
            }
            var next = pointer.GetNextContextPosition(LogicalDirection.Forward);
            if (next is null) break;
            pointer = next;
        }
        return EditorBox.Document.ContentEnd;
    }

    private int GetLineNumber(TextPointer position)
    {
        var range = new TextRange(EditorBox.Document.ContentStart, position).Text;
        return range.Count(c => c == '\n') + 1;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var found = FindVisualChild<T>(child);
            if (found is not null) return found;
        }
        return null;
    }
}
