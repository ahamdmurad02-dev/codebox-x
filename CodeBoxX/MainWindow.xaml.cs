using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CodeBoxX.Controls;
using CodeBoxX.Dialogs;
using CodeBoxX.Models;
using CodeBoxX.Services;
using CodeBoxX.Views;
using Microsoft.Win32;

namespace CodeBoxX;

public partial class MainWindow : Window
{
    private readonly List<EditorDocument> _documents = [];
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _autoSaveTimer;
    private readonly DispatcherTimer _previewRefreshTimer;
    private readonly DispatcherTimer _diagnosticTimer;
    private readonly TerminalSession _terminal = new();
    private readonly PythonPreviewSession _pythonPreview = new();
    private readonly MpmService _mpm = new();
    private readonly WebsitePublishService _websitePublisher = new();
    private readonly UpdateService _updates = new();
    private long _visibleTerminalSessionId;
    private readonly ExtensionMarketplaceService _extensions;
    private readonly GeminiService _gemini;
    private readonly List<EditorDiagnostic> _currentDiagnostics = [];
    private PreviewWindow? _previewWindow;
    private PythonPreviewWindow? _pythonPreviewWindow;
    private MpmWindow? _mpmWindow;
    private MarketplaceWindow? _marketplaceWindow;
    private AiAssistantWindow? _aiAssistantWindow;
    private string? _workspacePath;
    private Process? _activeProcess;
    private ActiveRunRequest? _lastRunRequest;
    private bool _isFullScreen;
    private WindowState _priorWindowState;
    private WindowStyle _priorWindowStyle;
    private bool _suppressSettingsEvents;

    public MainWindow()
    {
        // XAML assigns initial values while controls are being constructed. Load
        // preferences and suppress those events first so no handler can access
        // a control that has not yet been created.
        _settings = AppSettings.Load();
        _extensions = new ExtensionMarketplaceService();
        _gemini = new GeminiService(_settings.GetGeminiApiKey);
        _suppressSettingsEvents = true;
        InitializeComponent();
        _suppressSettingsEvents = false;

        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;
        _previewRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _previewRefreshTimer.Tick += PreviewRefreshTimer_Tick;
        _diagnosticTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _diagnosticTimer.Tick += DiagnosticTimer_Tick;
        _terminal.DataReceived += Terminal_DataReceived;
        _extensions.ExtensionsChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!string.IsNullOrWhiteSpace(_settings.ActiveExtensionThemeId) && (!_extensions.IsInstalled(_settings.ActiveExtensionThemeId) || !_extensions.IsEnabled(_settings.ActiveExtensionThemeId))) ResetExtensionTheme();
            QueueDiagnostics();
        });
        ApplySettings();
        AppendOutput("CodeBox X ready. Open a folder or file to begin.\n", OutputKind.Info);
        StartNewTerminal();
    }

    private CodeEditor? ActiveEditor => (OpenTabs.SelectedItem as TabItem)?.Content as CodeEditor;
    private EditorDocument? ActiveDocument => ActiveEditor?.Document;

    private void ApplySettings()
    {
        _suppressSettingsEvents = true;
        ThemeComboBox.SelectedIndex = _settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        AutoSaveBox.IsChecked = _settings.AutoSave;
        FontSizeSlider.Value = _settings.FontSize;
        FontSizeLabel.Text = $"{_settings.FontSize:0} px";
        _suppressSettingsEvents = false;
        ApplyTheme(_settings.Theme);
        if (_extensions.GetEnabledTheme(_settings.ActiveExtensionThemeId) is { } extensionTheme) ApplyExtensionTheme(extensionTheme, _settings.ActiveExtensionThemeId ?? string.Empty, persist: false);
        UpdateEditorFonts();
    }

    private void ApplyTheme(string theme)
    {
        var dark = theme == "Dark";
        var resources = Application.Current.Resources;
        SetBrush(resources, "WindowBackgroundBrush", dark ? "#1A1C1F" : "#F7F7F8");
        SetBrush(resources, "PanelBackgroundBrush", dark ? "#222529" : "#FFFFFF");
        SetBrush(resources, "EditorBackgroundBrush", dark ? "#181A1D" : "#FFFFFF");
        SetBrush(resources, "SurfaceBrush", dark ? "#2B2F34" : "#F1F3F5");
        SetBrush(resources, "BorderBrush", dark ? "#3D4248" : "#D5D9DE");
        SetBrush(resources, "TextBrush", dark ? "#E6E9ED" : "#1C1F23");
        SetBrush(resources, "MutedTextBrush", dark ? "#A9B0B8" : "#626971");
        SetBrush(resources, "AccentBrush", dark ? "#4AA0F3" : "#0067C0");
        SetBrush(resources, "AccentHoverBrush", dark ? "#70B7FF" : "#005AAB");
        SetBrush(resources, "SelectionBrush", dark ? "#264C71" : "#D9EAFA");
        SetBrush(resources, "TerminalBackgroundBrush", dark ? "#0D1013" : "#111418");
        SetBrush(resources, "TerminalTextBrush", "#D5E1EA");
        SetBrush(resources, "SyntaxCommentBrush", dark ? "#7F9F7F" : "#6A737D");
        SetBrush(resources, "SyntaxStringBrush", dark ? "#CE9178" : "#A31515");
        SetBrush(resources, "SyntaxNumberBrush", dark ? "#B5CEA8" : "#098658");
        SetBrush(resources, "SyntaxKeywordBrush", dark ? "#569CD6" : "#0000CC");
    }

        private static void SetBrush(ResourceDictionary resources, string key, string color) => resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

    private void ApplyExtensionTheme(ThemeDefinition theme, string extensionId, bool persist)
    {
        var resources = Application.Current.Resources;
        SetBrush(resources, "WindowBackgroundBrush", theme.WindowBackground);
        SetBrush(resources, "PanelBackgroundBrush", theme.PanelBackground);
        SetBrush(resources, "EditorBackgroundBrush", theme.EditorBackground);
        SetBrush(resources, "SurfaceBrush", theme.Surface);
        SetBrush(resources, "BorderBrush", theme.Border);
        SetBrush(resources, "TextBrush", theme.Text);
        SetBrush(resources, "MutedTextBrush", theme.MutedText);
        SetBrush(resources, "AccentBrush", theme.Accent);
        SetBrush(resources, "AccentHoverBrush", theme.AccentHover);
        SetBrush(resources, "SelectionBrush", theme.Selection);
        SetBrush(resources, "TerminalBackgroundBrush", theme.TerminalBackground);
        SetBrush(resources, "TerminalTextBrush", theme.TerminalText);
        SetBrush(resources, "SyntaxCommentBrush", theme.Comment);
        SetBrush(resources, "SyntaxStringBrush", theme.String);
        SetBrush(resources, "SyntaxNumberBrush", theme.Number);
        SetBrush(resources, "SyntaxKeywordBrush", theme.Keyword);
        if (persist)
        {
            _settings.ActiveExtensionThemeId = extensionId;
            _settings.Save();
        }
        RefreshEditorPresentation();
    }

    private void ResetExtensionTheme()
    {
        _settings.ActiveExtensionThemeId = null;
        _settings.Save();
        ApplyTheme(_settings.Theme);
        RefreshEditorPresentation();
    }

    private void RefreshEditorPresentation()
    {
        foreach (var editor in OpenTabs.Items.OfType<TabItem>().Select(tab => tab.Content).OfType<CodeEditor>())
        {
            editor.SetDiagnostics(ReferenceEquals(editor.Document, ActiveDocument) ? _currentDiagnostics : []);
        }
    }

    private void Mpm_Click(object sender, RoutedEventArgs e)
    {
        _mpmWindow ??= new MpmWindow(_mpm, () => _workspacePath) { Owner = this };
        _mpmWindow.ShowManager();
    }

    private void Marketplace_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_marketplaceWindow is null)
            {
                _marketplaceWindow = new MarketplaceWindow(_extensions) { Owner = this };
                _marketplaceWindow.ThemeRequested += (theme, extensionId, persist) => ApplyExtensionTheme(theme, extensionId, persist);
                _marketplaceWindow.ResetThemeRequested += ResetExtensionTheme;
            }

            _marketplaceWindow.ShowMarketplace();
            StatusText.Text = "Local Marketplace ready.";
        }
        catch (Exception ex)
        {
            const string message = "The local Marketplace could not be opened. It does not require an internet connection. Close and reopen CodeBox X, then try again.";
            AppendOutput($"{message}{Environment.NewLine}{ex.Message}{Environment.NewLine}", OutputKind.Error);
            MessageBox.Show(message, "Marketplace", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Publish_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_workspacePath) || !Directory.Exists(_workspacePath))
        {
            const string message = "Open a website project folder that contains index.html before using Publish Website. This creates a local site.zip package; it does not publish an extension to an online Marketplace.";
            AppendOutput(message + Environment.NewLine, OutputKind.Warning);
            MessageBox.Show(message, "Publish Website", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!SaveWorkspaceDocumentsBeforePublish()) return;

        var result = _websitePublisher.Publish(_workspacePath);
        if (!result.Success)
        {
            AppendOutput(result.Message + Environment.NewLine, OutputKind.Error);
            MessageBox.Show(result.Message, "Publish Website", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RefreshExplorer();
        AppendOutput("[Publish] " + result.Message + Environment.NewLine, OutputKind.Info);
        StatusText.Text = "Website publish package created.";
        if (MessageBox.Show($"A deployable website ZIP was created.\n\n{result.OutputPath}\n\nOpen its location?", "Publish Website", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{result.OutputPath}\"", UseShellExecute = true });
            }
            catch
            {
                // The package has already been created even when Explorer cannot be opened.
            }
        }
    }

    private bool SaveWorkspaceDocumentsBeforePublish()
    {
        if (string.IsNullOrWhiteSpace(_workspacePath)) return false;
        var workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_workspacePath)) + Path.DirectorySeparatorChar;
        var changedWorkspaceDocuments = _documents
            .Where(document => document.IsDirty
                && !string.IsNullOrWhiteSpace(document.FilePath)
                && Path.GetFullPath(document.FilePath).StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var document in changedWorkspaceDocuments)
        {
            if (!SaveDocument(document)) return false;
        }

        return true;
    }

    private void AiAssistant_Click(object sender, RoutedEventArgs e)
    {
        _aiAssistantWindow ??= new AiAssistantWindow(_gemini, CreateAiRequestContext, InsertAiGeneratedCode, OpenAiSettings);
        _aiAssistantWindow.ShowAssistant();
    }

    private void AiSettings_Click(object sender, RoutedEventArgs e) => OpenAiSettings();

    private void OpenAiSettings()
    {
        var settingsWindow = new AiSettingsWindow(_settings, _gemini) { Owner = this };
        settingsWindow.ShowDialog();
    }

    private AiRequestContext CreateAiRequestContext(AiEditorAction action, string userPrompt)
    {
        var document = ActiveDocument;
        var selected = ActiveEditor?.SelectedText;
        var source = !string.IsNullOrWhiteSpace(selected) ? selected : action is AiEditorAction.Explain or AiEditorAction.Fix or AiEditorAction.Refactor or AiEditorAction.AddComments ? document?.Text : null;
        return new AiRequestContext
        {
            Action = action,
            UserPrompt = userPrompt,
            SelectedCode = source,
            CurrentFileName = document?.FilePath is null ? document?.FileNameHint : Path.GetFileName(document.FilePath),
            CurrentLanguage = document?.LanguageId,
            ProjectSummary = BuildAiProjectSummary()
        };
    }

    private string BuildAiProjectSummary()
    {
        if (string.IsNullOrWhiteSpace(_workspacePath) || !Directory.Exists(_workspacePath)) return "No workspace folder is open.";
        try
        {
            var files = Directory.EnumerateFiles(_workspacePath, "*", SearchOption.AllDirectories)
                .Where(path => !path.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .Take(80)
                .Select(path => Path.GetRelativePath(_workspacePath, path));
            return $"Workspace: {Path.GetFileName(_workspacePath)}\nFiles:\n" + string.Join('\n', files);
        }
        catch
        {
            return $"Workspace: {Path.GetFileName(_workspacePath)}";
        }
    }

    private void InsertAiGeneratedCode(string code)
    {
        if (ActiveEditor is null)
        {
            StatusText.Text = "Open or create a file before inserting AI-generated code.";
            return;
        }
        ActiveEditor.InsertOrReplaceSelection(code);
        QueueDiagnostics();
        StatusText.Text = "AI-generated code inserted.";
    }

    private void NewFile_Click(object sender, RoutedEventArgs e) => CreateNewDocument();

    private void CreateNewDocument()
    {
        var dialog = new NewFileDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var fileName = dialog.FileName;
        if (!Path.HasExtension(fileName)) fileName += dialog.SelectedExtension;
        var existingTab = OpenTabs.Items.OfType<TabItem>().FirstOrDefault(tab =>
            tab.Tag is EditorDocument document && document.FilePath is null && string.Equals(document.FileNameHint, fileName, StringComparison.OrdinalIgnoreCase));
        if (existingTab is not null)
        {
            OpenTabs.SelectedItem = existingTab;
            (existingTab.Content as CodeEditor)?.FocusEditor();
            StatusText.Text = $"{fileName} is already open";
            return;
        }

        var starter = StarterContent(fileName);
        var document = new EditorDocument(initialText: starter, displayName: fileName);
        if (!string.IsNullOrEmpty(starter)) document.IsDirty = true;
        AddDocument(document);
        StatusText.Text = $"Created {fileName}";
    }

    private static string StarterContent(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".py" => "def main():\n    print(\"Hello from CodeBox X\")\n\n\nif __name__ == \"__main__\":\n    main()\n",
        ".cs" => "using System;\n\nConsole.WriteLine(\"Hello from CodeBox X\");\n",
        ".js" => "console.log(\"Hello from CodeBox X\");\n",
        ".ts" => "console.log(\"Hello from CodeBox X\");\n",
        ".json" => "{\n  \"name\": \"CodeBox X\"\n}\n",
        ".html" or ".htm" => "<!doctype html>\n<html>\n<head><meta charset=\"utf-8\"><title>CodeBox X Preview</title></head>\n<body><h1>Hello from CodeBox X</h1></body>\n</html>\n",
        ".md" or ".markdown" => "# CodeBox X\n\nStart writing here.\n",
        _ => string.Empty
    };

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Open a file", Filter = "Code and text files|*.py;*.cs;*.csproj;*.cpp;*.cc;*.cxx;*.c;*.h;*.hpp;*.java;*.js;*.ts;*.json;*.xml;*.sql;*.lua;*.gd;*.md;*.txt|All files|*.*", Multiselect = true };
        if (dialog.ShowDialog(this) == true) foreach (var file in dialog.FileNames) OpenDocument(file);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Open project folder", Multiselect = false };
        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName)) OpenWorkspace(dialog.FolderName);
    }

    private void RecentMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        RecentMenu.Items.Clear();
        var projects = _settings.RecentProjects.Where(Directory.Exists).ToList();
        var files = _settings.RecentFiles.Where(File.Exists).ToList();
        if (projects.Count == 0 && files.Count == 0)
        {
            RecentMenu.Items.Add(new MenuItem { Header = "No recent items", IsEnabled = false });
            return;
        }
        if (projects.Count > 0)
        {
            RecentMenu.Items.Add(new MenuItem { Header = "Projects", IsEnabled = false });
            foreach (var path in projects)
            {
                var item = new MenuItem { Header = path, ToolTip = path };
                item.Click += (_, _) => OpenWorkspace(path);
                RecentMenu.Items.Add(item);
            }
        }
        if (projects.Count > 0 && files.Count > 0) RecentMenu.Items.Add(new Separator());
        if (files.Count > 0)
        {
            RecentMenu.Items.Add(new MenuItem { Header = "Files", IsEnabled = false });
            foreach (var path in files)
            {
                var item = new MenuItem { Header = path, ToolTip = path };
                item.Click += (_, _) => OpenDocument(path);
                RecentMenu.Items.Add(item);
            }
        }
    }

    private void OpenWorkspace(string path)
    {
        if (!Directory.Exists(path)) return;
        _workspacePath = path;
        ProjectTitleText.Text = Path.GetFileName(path);
        ExplorerPathText.Text = path;
        _settings.AddRecentProject(path);
        _settings.Save();
        RefreshExplorer();
        StartNewTerminal();
        StatusText.Text = $"Workspace opened: {path}";
    }

    private void OpenDocument(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
            var existingTab = OpenTabs.Items.OfType<TabItem>().FirstOrDefault(tab => tab.Tag is EditorDocument doc && string.Equals(doc.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (existingTab is not null) { OpenTabs.SelectedItem = existingTab; (existingTab.Content as CodeEditor)?.FocusEditor(); return; }
            var text = ReadText(path, out var encoding);
            var document = new EditorDocument(path, text) { Encoding = encoding };
            document.MarkSaved();
            AddDocument(document);
            _settings.AddRecentFile(path);
            _settings.Save();
            StatusText.Text = $"Opened {Path.GetFileName(path)}";
        }
        catch (Exception ex) { ShowError("Could not open the file", ex); }
    }

    private static string ReadText(string path, out Encoding encoding)
    {
        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        encoding = reader.CurrentEncoding;
        return text;
    }

    private void AddDocument(EditorDocument document)
    {
        _documents.Add(document);
        var editor = new CodeEditor(document);
        editor.ContentChanged += Editor_ContentChanged;
        editor.CaretChanged += (_, _) => UpdateStatus();
        editor.FileDropped += (_, path) => OpenDocument(path);
        var headerText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 190 };
        headerText.SetBinding(TextBlock.TextProperty, new Binding(nameof(EditorDocument.DisplayName)) { Source = document });
        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
        headerPanel.Children.Add(headerText);

        var tab = new TabItem { Content = editor, Tag = document, ToolTip = document.Tooltip };
        var closeButton = new Button { Style = (Style)FindResource("TabCloseButton"), Tag = tab };
        closeButton.Click += TabCloseButton_Click;
        headerPanel.Children.Add(closeButton);
        tab.Header = headerPanel;

        document.PropertyChanged += (_, args) => { if (args.PropertyName is nameof(EditorDocument.Tooltip) or nameof(EditorDocument.FilePath)) tab.ToolTip = document.Tooltip; };
        OpenTabs.Items.Add(tab);
        OpenTabs.SelectedItem = tab;
        WelcomePanel.Visibility = Visibility.Collapsed;
        UpdateEditorFonts();
        UpdateStatus();
        Dispatcher.BeginInvoke(editor.FocusEditor, DispatcherPriority.Background);
    }

    private void Editor_ContentChanged(object? sender, EventArgs e)
    {
        UpdateStatus();
        if (_settings.AutoSave) { _autoSaveTimer.Stop(); _autoSaveTimer.Start(); }
        QueueDiagnostics();
        if (sender is CodeEditor editor && _previewWindow?.CurrentDocument == editor.Document)
        {
            _previewRefreshTimer.Stop();
            _previewRefreshTimer.Start();
        }
    }

    private void PreviewRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _previewRefreshTimer.Stop();
        if (_previewWindow?.CurrentDocument is { } document) _previewWindow.RefreshIfShowing(document);
    }

    private void QueueDiagnostics()
    {
        _diagnosticTimer.Stop();
        _diagnosticTimer.Start();
    }

    private void DiagnosticTimer_Tick(object? sender, EventArgs e)
    {
        _diagnosticTimer.Stop();
        RunDiagnostics();
    }

    private void RunDiagnostics()
    {
        _currentDiagnostics.Clear();
        if (ActiveDocument is not null) _currentDiagnostics.AddRange(_extensions.Analyze(ActiveDocument));
        ProblemsListBox.ItemsSource = null;
        ProblemsListBox.ItemsSource = _currentDiagnostics;
        RefreshEditorPresentation();
        if (_currentDiagnostics.Count > 0) StatusText.Text = $"{_currentDiagnostics.Count} problem(s) found";
    }

    private void ToggleProblems_Click(object sender, RoutedEventArgs e)
    {
        var showProblems = ProblemsPanel.Visibility != Visibility.Visible;
        ProblemsPanel.Visibility = showProblems ? Visibility.Visible : Visibility.Collapsed;
        if (showProblems) RunDiagnostics();
    }

    private void ProblemsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProblemsListBox.SelectedItem is not EditorDiagnostic diagnostic) return;
        var tab = OpenTabs.Items.OfType<TabItem>().FirstOrDefault(item => item.Tag is EditorDocument document && string.Equals(document.FilePath, diagnostic.FilePath, StringComparison.OrdinalIgnoreCase));
        if (tab is not null) OpenTabs.SelectedItem = tab;
        ActiveEditor?.GoTo(diagnostic.Line, diagnostic.Column);
    }

    private void AutoSaveTimer_Tick(object? sender, EventArgs e)
    {
        _autoSaveTimer.Stop();
        if (ActiveDocument is { IsDirty: true, FilePath: not null } document) SaveDocument(document, true);
    }

    private void Save_Click(object sender, RoutedEventArgs e) { if (ActiveDocument is not null) SaveDocument(ActiveDocument); }
    private bool SaveDocument(EditorDocument document, bool quiet = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(document.FilePath)) return SaveDocumentAs(document);
            File.WriteAllText(document.FilePath, document.Text, document.Encoding);
            document.MarkSaved();
            _settings.AddRecentFile(document.FilePath);
            _settings.Save();
            if (!quiet) StatusText.Text = $"Saved {Path.GetFileName(document.FilePath)}";
            if (_previewWindow?.CurrentDocument == document) _previewWindow.RefreshIfShowing(document);
            if (_pythonPreviewWindow?.CurrentDocument == document) _pythonPreviewWindow.RestartIfShowing(document);
            return true;
        }
        catch (Exception ex) { ShowError("Could not save the file", ex); return false; }
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e) { if (ActiveDocument is not null) SaveDocumentAs(ActiveDocument); }
    private bool SaveDocumentAs(EditorDocument document)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save file as",
            FileName = document.FilePath is null ? document.FileNameHint : Path.GetFileName(document.FilePath),
            Filter = "All files|*.*|Python|*.py|C# source|*.cs|C# project|*.csproj|C++ source|*.cpp;*.cc;*.cxx|GDScript|*.gd|JavaScript|*.js|TypeScript|*.ts|JSON|*.json|HTML|*.html|Markdown|*.md|Text|*.txt"
        };
        if (dialog.ShowDialog(this) != true) return false;

        var destination = Path.GetFullPath(dialog.FileName);
        var duplicate = _documents.FirstOrDefault(item => !ReferenceEquals(item, document) && item.FilePath is not null && string.Equals(item.FilePath, destination, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
        {
            var existingTab = OpenTabs.Items.OfType<TabItem>().FirstOrDefault(tab => ReferenceEquals(tab.Tag, duplicate));
            if (existingTab is not null) OpenTabs.SelectedItem = existingTab;
            MessageBox.Show("That file is already open in another tab.", "CodeBox X", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        try
        {
            File.WriteAllText(destination, document.Text, document.Encoding);
            document.SetPath(destination);
            document.MarkSaved();
            _settings.AddRecentFile(destination);
            _settings.Save();
            StatusText.Text = $"Saved {Path.GetFileName(destination)}";
            if (_previewWindow?.CurrentDocument == document) _previewWindow.RefreshIfShowing(document);
            if (_pythonPreviewWindow?.CurrentDocument == document) _pythonPreviewWindow.RestartIfShowing(document);
            return true;
        }
        catch (Exception ex)
        {
            ShowError("Could not save the file", ex);
            return false;
        }
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e) => CloseActiveDocument();

    private void TabCloseButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: TabItem tab }) CloseDocument(tab);
    }

    private bool CloseActiveDocument()
    {
        return OpenTabs.SelectedItem is not TabItem tab || CloseDocument(tab);
    }

    private bool CloseDocument(TabItem tab, bool promptToSave = true)
    {
        if (tab.Tag is not EditorDocument document) return true;
        if (promptToSave && !ResolveUnsavedDocument(document)) return false;
        RemoveDocumentTab(tab, document);
        return true;
    }

    private bool ResolveUnsavedDocument(EditorDocument document)
    {
        if (!document.IsDirty) return true;
        var dialog = new UnsavedChangesDialog(document.FileNameHint) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Choice == UnsavedChangesChoice.Cancel) return false;
        return dialog.Choice != UnsavedChangesChoice.Save || SaveDocument(document);
    }

    private void RemoveDocumentTab(TabItem tab, EditorDocument document)
    {
        _documents.Remove(document);
        OpenTabs.Items.Remove(tab);
        WelcomePanel.Visibility = OpenTabs.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatus();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => ActiveEditor?.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => ActiveEditor?.Redo();
    private void ToggleSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchPanel.Visibility = SearchPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (SearchPanel.Visibility == Visibility.Visible) { FindBox.Focus(); FindBox.SelectAll(); }
    }
    private void FindNext_Click(object sender, RoutedEventArgs e) => FindNext();
    private void FindBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) FindNext(); }
    private void FindNext() { if (ActiveEditor is not null && !ActiveEditor.FindNext(FindBox.Text, MatchCaseBox.IsChecked == true)) StatusText.Text = "No match found"; }
    private void ReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveEditor is null) return;
        var count = ActiveEditor.ReplaceAll(FindBox.Text, ReplaceBox.Text, MatchCaseBox.IsChecked == true);
        StatusText.Text = count == 0 ? "No matches replaced" : $"Replaced {count} occurrence(s)";
    }

    private async void Run_Click(object sender, RoutedEventArgs e) => await RunActiveFileAsync();

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProcess is { HasExited: false } runningProcess)
        {
            StopActiveProcess("Restarting active process.");
            for (var attempt = 0; attempt < 20 && !runningProcess.HasExited; attempt++) await Task.Delay(50);
            if (!runningProcess.HasExited)
            {
                AppendOutput("The previous process is still stopping. Try Restart again in a moment.\n", OutputKind.Warning);
                return;
            }
        }

        if (_lastRunRequest is not null)
        {
            await ExecuteCommandAsync(_lastRunRequest.Command, _lastRunRequest.WorkingDirectory, _lastRunRequest.DisplayName);
            return;
        }

        await RunActiveFileAsync();
    }

    private async Task RunActiveFileAsync()
    {
        if (ActiveDocument is null)
        {
            AppendOutput("No active file to run.\n", OutputKind.Warning);
            return;
        }

        if (!SaveDocument(ActiveDocument) || ActiveDocument.FilePath is null) return;
        var resolution = ActiveFileRunService.Resolve(ActiveDocument.FilePath, _workspacePath);
        if (!resolution.IsSuccess || resolution.Request is null)
        {
            OutputRow.Height = new GridLength(190);
            AppendOutput($"[Run Active File] {resolution.Message}\n", OutputKind.Warning);
            StatusText.Text = "Runtime or compiler required";
            MessageBox.Show(resolution.Message, "Run Active File", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _lastRunRequest = resolution.Request;
        await ExecuteCommandAsync(resolution.Request.Command, resolution.Request.WorkingDirectory, resolution.Request.DisplayName);
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopActiveProcess("Process stopped.");

    private void StopActiveProcess(string message)
    {
        try
        {
            if (_activeProcess is not { HasExited: false })
            {
                AppendOutput("No active run process is running.\n", OutputKind.Info);
                return;
            }

            _activeProcess.Kill(entireProcessTree: true);
            AppendOutput($"\n{message}\n", OutputKind.Warning);
            StatusText.Text = "Process stopped";
        }
        catch (Exception ex)
        {
            AppendOutput($"Could not stop process: {ex.Message}\n", OutputKind.Error);
        }
    }

    private async Task ExecuteCommandAsync(string command, string? workingDirectory, string label)
    {
        if (_activeProcess is { HasExited: false }) { AppendOutput("A process is already running. Stop it before starting another command.\n", OutputKind.Warning); return; }
        AppendOutput($"\n[{label}] {command}\n", OutputKind.Info);
        var process = new Process { StartInfo = new ProcessStartInfo { FileName = "cmd.exe", Arguments = $"/d /s /c \"{command}\"", WorkingDirectory = !string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory) ? workingDirectory : Environment.CurrentDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 }, EnableRaisingEvents = true };
        _activeProcess = process;
        process.OutputDataReceived += (_, args) => { if (args.Data is not null) Dispatcher.Invoke(() => AppendOutput(args.Data + Environment.NewLine, OutputKind.Normal)); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) Dispatcher.Invoke(() => AppendOutput(args.Data + Environment.NewLine, OutputKind.Error)); };
        try
        {
            process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine(); await process.WaitForExitAsync();
            AppendOutput($"[{label} finished with exit code {process.ExitCode}]\n", process.ExitCode == 0 ? OutputKind.Info : OutputKind.Error);
        }
        catch (Exception ex) { AppendOutput($"Unable to execute command: {ex.Message}\n", OutputKind.Error); }
        finally { process.Dispose(); if (ReferenceEquals(_activeProcess, process)) _activeProcess = null; }
    }

    private void NewTerminal_Click(object sender, RoutedEventArgs e) => StartNewTerminal();

    private void StartNewTerminal()
    {
        // Invalidate UI callbacks from the previous process before it is stopped.
        // OutputDataReceived is asynchronous and can otherwise arrive after a new
        // terminal header, which was the source of the misleading ended-session UI.
        _visibleTerminalSessionId = 0;
        _terminal.Stop();
        OutputBox.Clear();
        var directory = !string.IsNullOrWhiteSpace(_workspacePath) && Directory.Exists(_workspacePath)
            ? Path.GetFullPath(_workspacePath)
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (_terminal.Start(directory, out var error))
        {
            _visibleTerminalSessionId = _terminal.CurrentSessionId;
            OutputRow.Height = new GridLength(190);
            AppendOutput($"CodeBox X Terminal — {directory}{Environment.NewLine}", OutputKind.Info);
            StatusText.Text = "Terminal ready";
            TerminalCommandBox.Focus();
        }
        else
        {
            AppendOutput($"Unable to start terminal: {error}{Environment.NewLine}", OutputKind.Error);
        }
    }

    private async void TerminalCommandBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(TerminalCommandBox.Text)) return;
        var command = TerminalCommandBox.Text.Trim();
        TerminalCommandBox.Clear();
        e.Handled = true;

        if (string.Equals(command, "cls", StringComparison.OrdinalIgnoreCase))
        {
            OutputBox.Clear();
            return;
        }

        if (IsMpmCommand(command))
        {
            await ExecuteMpmTerminalCommandAsync(command);
            return;
        }

        AppendOutput($"> {command}{Environment.NewLine}", OutputKind.Normal);
        if (!_terminal.Send(command, out var error)) AppendOutput($"{error}{Environment.NewLine}", OutputKind.Error);
    }

    private void Terminal_DataReceived(object? sender, TerminalData args)
    {
        if (args.SessionId != _visibleTerminalSessionId) return;
        var kind = args.Stream == TerminalStream.Error ? OutputKind.Error : OutputKind.Normal;
        Dispatcher.BeginInvoke(() =>
        {
            if (args.SessionId == _visibleTerminalSessionId) AppendOutput(args.Text + Environment.NewLine, kind);
        });
    }

    private static bool IsMpmCommand(string command) => command.Equals("mpm", StringComparison.OrdinalIgnoreCase) || command.StartsWith("mpm ", StringComparison.OrdinalIgnoreCase);

    private async Task ExecuteMpmTerminalCommandAsync(string command)
    {
        AppendOutput($"> {command}{Environment.NewLine}", OutputKind.Info);
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts[1].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            AppendOutput("MPM commands:\n  mpm search <package>\n  mpm info <package>\n  mpm list\n  mpm install <package>\n  mpm uninstall <package>\n  mpm update\n  mpm restore\n", OutputKind.Info);
            return;
        }

        var context = _mpm.DetectProject(_workspacePath);
        if (!context.IsAvailable)
        {
            AppendOutput(context.UnavailableReason + Environment.NewLine, OutputKind.Warning);
            return;
        }

        var action = parts[1].ToLowerInvariant();
        var packageName = parts.Length == 3 ? parts[2] : string.Empty;
        if (parts.Length > 3)
        {
            AppendOutput("MPM accepts one package identifier only; version expressions, shell switches, and additional arguments are rejected for safety.\n", OutputKind.Warning);
            return;
        }

        MpmOperationResult result;
        switch (action)
        {
            case "search" when !string.IsNullOrWhiteSpace(packageName):
                result = await _mpm.SearchAsync(context, packageName);
                break;
            case "info" when !string.IsNullOrWhiteSpace(packageName):
                result = await _mpm.InfoAsync(context, packageName);
                break;
            case "list" when string.IsNullOrWhiteSpace(packageName):
                result = await _mpm.ListAsync(context);
                break;
            case "install" when !string.IsNullOrWhiteSpace(packageName):
                if (MessageBox.Show($"Install '{packageName}' from {context.SourceName}?\n\nSource: {context.SourceUrl}{packageName}\n\nMPM disables package scripts where supported and rejects shell text.", "Confirm MPM Installation", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    AppendOutput("MPM installation cancelled.\n", OutputKind.Info);
                    return;
                }
                result = await _mpm.InstallAsync(context, packageName);
                break;
            case "uninstall" when !string.IsNullOrWhiteSpace(packageName):
                if (MessageBox.Show($"Remove '{packageName}' from this project?", "Confirm MPM Removal", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    AppendOutput("MPM removal cancelled.\n", OutputKind.Info);
                    return;
                }
                result = await _mpm.UninstallAsync(context, packageName);
                break;
            case "update" when string.IsNullOrWhiteSpace(packageName):
                if (MessageBox.Show("Update every dependency recorded in .codebox-mpm.json?", "Confirm MPM Update", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    AppendOutput("MPM update cancelled.\n", OutputKind.Info);
                    return;
                }
                result = await _mpm.UpdateAsync(context);
                break;
            case "restore" when string.IsNullOrWhiteSpace(packageName):
                if (MessageBox.Show("Restore dependencies recorded in .codebox-mpm.json?", "Confirm MPM Restore", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    AppendOutput("MPM restore cancelled.\n", OutputKind.Info);
                    return;
                }
                result = await _mpm.RestoreAsync(context);
                break;
            default:
                AppendOutput("Invalid MPM command. Type 'mpm help' for supported commands.\n", OutputKind.Warning);
                return;
        }

        if (!string.IsNullOrWhiteSpace(result.Output)) AppendOutput(RedactMpmOutput(result.Output) + Environment.NewLine, result.Success ? OutputKind.Normal : OutputKind.Error);
        AppendOutput(result.Message + Environment.NewLine, result.Success ? OutputKind.Info : OutputKind.Error);
        StatusText.Text = result.Success ? "MPM command completed." : "MPM command failed safely.";
        if (_mpmWindow?.IsVisible == true) _mpmWindow.ShowManager();
    }

    private static string RedactMpmOutput(string value) => System.Text.RegularExpressions.Regex.Replace(value, @"(?i)(api[_-]?key|token|password|secret)\s*([=:])\s*[^\s]+", "$1$2[redacted]");

    private void FocusTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (!_terminal.IsRunning) StartNewTerminal();
        TerminalCommandBox.Focus();
    }

    private void ClearTerminal_Click(object sender, RoutedEventArgs e) => OutputBox.Clear();

    private void LivePreview_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveDocument is null)
        {
            const string message = "Open or create a supported file before using Live Preview.";
            AppendOutput(message + Environment.NewLine, OutputKind.Warning);
            MessageBox.Show(message, "Live Preview", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var extension = Path.GetExtension(ActiveDocument.FilePath ?? ActiveDocument.FileNameHint);
        if (string.Equals(extension, ".py", StringComparison.OrdinalIgnoreCase))
        {
            _pythonPreviewWindow ??= new PythonPreviewWindow(_pythonPreview, document => SaveDocument(document)) { Owner = this };
            _pythonPreviewWindow.ShowDocument(ActiveDocument);
            return;
        }

        _previewWindow ??= new PreviewWindow { Owner = this };
        _previewWindow.ShowDocument(ActiveDocument);
    }

    private void AppendOutput(string message, OutputKind kind)
    {
        OutputBox.AppendText(message);
        OutputBox.ScrollToEnd();
        if (kind == OutputKind.Error) StatusText.Text = "Process reported an error";
    }

    private void ToggleOutput_Click(object sender, RoutedEventArgs e) => OutputRow.Height = new GridLength(OutputRow.Height.Value > 0 ? 0 : 190);
    private void ToggleExplorer_Click(object sender, RoutedEventArgs e)
    {
        var hiding = ExplorerColumn.Width.Value > 0; ExplorerColumn.Width = new GridLength(hiding ? 0 : 260); ExplorerSplitterColumn.Width = new GridLength(hiding ? 0 : 5); ExplorerSplitter.Visibility = hiding ? Visibility.Collapsed : Visibility.Visible;
    }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var open = SettingsPanel.Visibility != Visibility.Visible; SettingsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed; SettingsSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed; SettingsColumn.Width = new GridLength(open ? 300 : 0); SettingsSplitterColumn.Width = new GridLength(open ? 5 : 0);
    }
    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsEvents || ThemeComboBox.SelectedItem is not ComboBoxItem item) return; _settings.Theme = item.Content?.ToString() ?? "System"; _settings.ActiveExtensionThemeId = null; ApplyTheme(_settings.Theme); _settings.Save(); RefreshEditorPresentation();
    }
    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // The slider is initialized before the label appears in the XAML tree.
        // Ignore this construction-time event; ApplySettings synchronizes both
        // controls once the window is fully initialized.
        if (FontSizeLabel is null || _suppressSettingsEvents) return;
        FontSizeLabel.Text = $"{e.NewValue:0} px";
        _settings.FontSize = e.NewValue;
        UpdateEditorFonts();
        _settings.Save();
    }
    private void AutoSaveBox_Changed(object sender, RoutedEventArgs e) { if (!_suppressSettingsEvents) { _settings.AutoSave = AutoSaveBox.IsChecked == true; _settings.Save(); } }
    private void UpdateEditorFonts() { foreach (var editor in OpenTabs.Items.OfType<TabItem>().Select(tab => tab.Content).OfType<CodeEditor>()) editor.SetEditorFontSize(_settings.FontSize); }

    private void RefreshExplorer_Click(object sender, RoutedEventArgs e) => RefreshExplorer();
    private void RefreshExplorer()
    {
        ExplorerTree.Items.Clear(); if (string.IsNullOrWhiteSpace(_workspacePath) || !Directory.Exists(_workspacePath)) return; ExplorerTree.Items.Add(CreateExplorerNode(_workspacePath, true));
    }
    private TreeViewItem CreateExplorerNode(string path, bool isRoot = false)
    {
        var isDirectory = Directory.Exists(path);
        var item = new TreeViewItem { Header = isRoot ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) : Path.GetFileName(path), Tag = path, FontWeight = isDirectory ? FontWeights.SemiBold : FontWeights.Normal };
        if (isDirectory) { item.ContextMenu = BuildFolderMenu(path); item.Expanded += (_, _) => PopulateDirectory(item, path); item.Items.Add(new TreeViewItem { Header = "Loading...", IsEnabled = false }); } else item.ContextMenu = BuildFileMenu(path);
        return item;
    }
    private void PopulateDirectory(TreeViewItem item, string path)
    {
        if (item.Items.Count != 1 || item.Items[0] is not TreeViewItem { Header: "Loading..." }) return;
        item.Items.Clear();
        try
        {
            foreach (var entry in Directory.EnumerateDirectories(path).OrderBy(p => p).Concat(Directory.EnumerateFiles(path).OrderBy(p => p)))
            {
                try { var attributes = File.GetAttributes(entry); if (!attributes.HasFlag(FileAttributes.Hidden) && !attributes.HasFlag(FileAttributes.System)) item.Items.Add(CreateExplorerNode(entry)); } catch { }
            }
        }
        catch (Exception ex) { item.Items.Add(new TreeViewItem { Header = $"Cannot read folder: {ex.Message}", IsEnabled = false }); }
    }
    private ContextMenu BuildFolderMenu(string path)
    {
        var menu = new ContextMenu(); menu.Items.Add(CreateMenuItem("New File...", (_, _) => CreateWorkspaceFile(path))); menu.Items.Add(CreateMenuItem("New Folder...", (_, _) => CreateWorkspaceFolder(path))); menu.Items.Add(new Separator()); menu.Items.Add(CreateMenuItem("Rename...", (_, _) => RenamePath(path))); menu.Items.Add(CreateMenuItem("Delete", (_, _) => DeletePath(path))); menu.Items.Add(new Separator()); menu.Items.Add(CreateMenuItem("Refresh", (_, _) => RefreshExplorer())); return menu;
    }
    private ContextMenu BuildFileMenu(string path)
    {
        var menu = new ContextMenu(); menu.Items.Add(CreateMenuItem("Open", (_, _) => OpenDocument(path))); menu.Items.Add(CreateMenuItem("Rename...", (_, _) => RenamePath(path))); menu.Items.Add(CreateMenuItem("Delete", (_, _) => DeletePath(path))); return menu;
    }
    private static MenuItem CreateMenuItem(string header, RoutedEventHandler handler) { var item = new MenuItem { Header = header }; item.Click += handler; return item; }
    private void NewExplorerFile_Click(object sender, RoutedEventArgs e) => CreateWorkspaceFile(SelectedExplorerDirectory());
    private void NewExplorerFolder_Click(object sender, RoutedEventArgs e) => CreateWorkspaceFolder(SelectedExplorerDirectory());

    private void DeleteSelectedExplorerItem_Click(object sender, RoutedEventArgs e)
    {
        if (ExplorerTree.SelectedItem is not TreeViewItem { Tag: string path })
        {
            MessageBox.Show("Select a file or folder in the File Explorer first.", "Delete", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DeletePath(path);
    }

    private string? SelectedExplorerDirectory()
    {
        if (ExplorerTree.SelectedItem is not TreeViewItem { Tag: string path }) return _workspacePath; return Directory.Exists(path) ? path : Path.GetDirectoryName(path);
    }
    private void CreateWorkspaceFile(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) { MessageBox.Show("Open a workspace folder first.", "CodeBox X"); return; }
        var dialog = new InputDialog("File name (for example, main.py):", "New File"); if (dialog.ShowDialog() != true) return;
        try { var path = ValidateChildPath(directory, dialog.Value); File.WriteAllText(path, string.Empty, new UTF8Encoding(false)); RefreshExplorer(); OpenDocument(path); } catch (Exception ex) { ShowError("Could not create the file", ex); }
    }
    private void CreateWorkspaceFolder(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) { MessageBox.Show("Open a workspace folder first.", "CodeBox X"); return; }
        var dialog = new InputDialog("Folder name:", "New Folder"); if (dialog.ShowDialog() != true) return;
        try { Directory.CreateDirectory(ValidateChildPath(directory, dialog.Value)); RefreshExplorer(); } catch (Exception ex) { ShowError("Could not create the folder", ex); }
    }
    private void RenamePath(string path)
    {
        var dialog = new InputDialog("New name:", "Rename", Path.GetFileName(path)); if (dialog.ShowDialog() != true) return;
        try { var target = ValidateChildPath(Path.GetDirectoryName(path)!, dialog.Value); if (File.Exists(path)) File.Move(path, target); else Directory.Move(path, target); foreach (var document in _documents.Where(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase))) document.SetPath(target); RefreshExplorer(); } catch (Exception ex) { ShowError("Could not rename the item", ex); }
    }
    private void DeletePath(string path)
    {
        var isFile = File.Exists(path);
        var isFolder = Directory.Exists(path);
        if (!isFile && !isFolder)
        {
            RefreshExplorer();
            return;
        }

        var affectedTabs = OpenTabs.Items.OfType<TabItem>()
            .Where(tab => tab.Tag is EditorDocument doc && doc.FilePath is not null && IsPathOrChild(doc.FilePath, path, isFolder))
            .ToList();
        var itemKind = isFolder ? "folder" : "file";
        var warning = $"Delete the {itemKind} '{Path.GetFileName(path)}'? This cannot be undone.";
        if (affectedTabs.Count > 0) warning += $"{Environment.NewLine}{Environment.NewLine}{affectedTabs.Count} open editor tab(s) will close. Unsaved changes in those deleted files will be discarded.";
        if (MessageBox.Show(warning, "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        try
        {
            if (isFile) File.Delete(path);
            else Directory.Delete(path, recursive: true);

            foreach (var tab in affectedTabs)
            {
                if (tab.Tag is EditorDocument document) RemoveDocumentTab(tab, document);
            }

            if (_previewWindow?.CurrentDocument is { } previewDocument && previewDocument.FilePath is not null && IsPathOrChild(previewDocument.FilePath, path, isFolder))
            {
                _previewWindow.Hide();
            }
            if (_pythonPreviewWindow?.CurrentDocument is { } pythonPreviewDocument && pythonPreviewDocument.FilePath is not null && IsPathOrChild(pythonPreviewDocument.FilePath, path, isFolder))
            {
                _pythonPreview.Stop();
                _pythonPreviewWindow.Hide();
            }

            RefreshExplorer();
            StatusText.Text = $"Deleted {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            ShowError("Could not delete the item", ex);
        }
    }

    private static bool IsPathOrChild(string candidatePath, string rootPath, bool rootIsDirectory)
    {
        var candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)) return true;
        return rootIsDirectory && candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static string ValidateChildPath(string directory, string childName)
    {
        if (childName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || childName.Contains(Path.DirectorySeparatorChar) || childName.Contains(Path.AltDirectorySeparatorChar)) throw new ArgumentException("Enter a valid file or folder name."); return Path.Combine(directory, childName);
    }
    private void ExplorerTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) { }
    private void ExplorerTree_MouseDoubleClick(object sender, MouseButtonEventArgs e) { var item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject); if (item?.Tag is string path && File.Exists(path)) OpenDocument(path); }
    private void OpenTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateStatus();
        QueueDiagnostics();
    }
    private void UpdateStatus()
    {
        if (ActiveDocument is null) { LanguageStatusText.Text = "Plain Text"; CursorStatusText.Text = "Ln 1, Col 1"; return; }
        LanguageStatusText.Text = ActiveDocument.LanguageId; if (ActiveEditor is not null) CursorStatusText.Text = $"Ln {ActiveEditor.CaretLine}, Col {ActiveEditor.CaretColumn}";
    }
    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();
    private void ToggleFullScreen()
    {
        if (!_isFullScreen) { _priorWindowState = WindowState; _priorWindowStyle = WindowStyle; WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; _isFullScreen = true; } else { WindowStyle = _priorWindowStyle; WindowState = _priorWindowState; _isFullScreen = false; }
    }
    private void Shortcuts_Click(object sender, RoutedEventArgs e) => MessageBox.Show("Ctrl+N   New file\nCtrl+O   Open file\nCtrl+Shift+O   Open folder\nCtrl+S   Save\nCtrl+Shift+S   Save as\nCtrl+W   Close tab\nCtrl+H   Find / replace\nCtrl+Z / Ctrl+Y   Undo / redo\nF5   Run active file\nCtrl+F5   Restart active run\nShift+F5   Stop process\nF11   Full screen\nCtrl+`   Focus terminal", "Keyboard Shortcuts", MessageBoxButton.OK, MessageBoxImage.Information);
    private void UpdateCodeBoxX_Click(object sender, RoutedEventArgs e)
    {
        var updateWindow = new UpdateWindow(_updates) { Owner = this };
        updateWindow.ShowDialog();
    }
    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow(_updates) { Owner = this }.ShowDialog();
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { Stop_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.F5 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Restart_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.F5) { Run_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (e.Key == Key.N) { CreateNewDocument(); e.Handled = true; } else if (e.Key == Key.O && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { OpenFolder_Click(this, new RoutedEventArgs()); e.Handled = true; } else if (e.Key == Key.O) { OpenFile_Click(this, new RoutedEventArgs()); e.Handled = true; } else if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { SaveAs_Click(this, new RoutedEventArgs()); e.Handled = true; } else if (e.Key == Key.S) { Save_Click(this, new RoutedEventArgs()); e.Handled = true; } else if (e.Key == Key.W) { CloseActiveDocument(); e.Handled = true; } else if (e.Key == Key.H) { ToggleSearch_Click(this, new RoutedEventArgs()); e.Handled = true; } else if (e.Key == Key.Z) { ActiveEditor?.Undo(); e.Handled = true; } else if (e.Key == Key.Y) { ActiveEditor?.Redo(); e.Handled = true; } else if (e.Key == Key.Oem3) { TerminalCommandBox.Focus(); e.Handled = true; }
        }
        else if (e.Key == Key.F11) { ToggleFullScreen(); e.Handled = true; } else if (e.Key == Key.Escape && _isFullScreen) { ToggleFullScreen(); e.Handled = true; }
    }
    private void Window_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return; foreach (var path in paths) { if (File.Exists(path)) OpenDocument(path); else if (Directory.Exists(path)) OpenWorkspace(path); }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        foreach (var tab in OpenTabs.Items.OfType<TabItem>().ToList())
        {
            OpenTabs.SelectedItem = tab;
            if (!CloseActiveDocument()) { e.Cancel = true; return; }
        }

        _previewRefreshTimer.Stop();
        _diagnosticTimer.Stop();
        _terminal.Dispose();
        _pythonPreview.Dispose();
        _mpm.Dispose();
        _gemini.Dispose();
        _updates.Dispose();
        Stop_Click(this, new RoutedEventArgs());
        _settings.Save();
    }
    private void ShowError(string title, Exception ex) { AppendOutput($"{title}: {ex.Message}\n", OutputKind.Error); MessageBox.Show($"{title}.\n\n{ex.Message}", "CodeBox X", MessageBoxButton.OK, MessageBoxImage.Error); }
    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject { while (source is not null) { if (source is T typed) return typed; source = VisualTreeHelper.GetParent(source); } return null; }
    private enum OutputKind { Normal, Info, Warning, Error }
}
