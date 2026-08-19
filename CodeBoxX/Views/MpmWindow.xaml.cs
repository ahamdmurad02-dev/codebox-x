using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CodeBoxX.Models;
using CodeBoxX.Services;

namespace CodeBoxX.Views;

public partial class MpmWindow : Window
{
    private readonly MpmService _mpm;
    private readonly Func<string?> _getWorkspacePath;
    private readonly ObservableCollection<MpmPackage> _packages = [];
    private MpmProjectContext _context = new();
    private bool _isBusy;

    public MpmWindow(MpmService mpm, Func<string?> getWorkspacePath)
    {
        _mpm = mpm;
        _getWorkspacePath = getWorkspacePath;
        InitializeComponent();
        PackageList.ItemsSource = _packages;
        _mpm.ProgressChanged += Mpm_ProgressChanged;
        UpdateButtons();
    }

    public void ShowManager()
    {
        Owner ??= Application.Current.MainWindow;
        if (!IsVisible) Show();
        Activate();
        _ = RefreshPackagesAsync();
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateButtons();
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await SearchAsync(); } }
    private async void Info_Click(object sender, RoutedEventArgs e) => await ShowInfoAsync();
    private async void Install_Click(object sender, RoutedEventArgs e) => await InstallAsync();
    private async void Uninstall_Click(object sender, RoutedEventArgs e) => await UninstallAsync();
    private async void Update_Click(object sender, RoutedEventArgs e) => await UpdateAsync();
    private async void Restore_Click(object sender, RoutedEventArgs e) => await RestoreAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshPackagesAsync();
    private async void AddProjectFile_Click(object sender, RoutedEventArgs e) => await AddProjectFileAsync();
    private void ClearOutput_Click(object sender, RoutedEventArgs e) => ProgressBox.Clear();

    private async Task RefreshPackagesAsync()
    {
        _context = _mpm.DetectProject(_getWorkspacePath());
        ProviderText.Text = _context.IsAvailable ? _context.ProviderLabel : "Project not detected";
        DetectedTypeText.Text = _context.IsAvailable ? $"Detected: {_context.Provider}" : "Unsupported workspace";
        DetectedPathText.Text = !string.IsNullOrWhiteSpace(_context.DetectedProjectPath) ? _context.DetectedProjectPath : _getWorkspacePath() ?? "No workspace folder is open.";
        ProjectGuidanceText.Text = string.IsNullOrWhiteSpace(_context.DetectionDetail) ? "Add a supported project file or source file, then refresh MPM." : _context.DetectionDetail;
        if (!_context.IsAvailable)
        {
            _packages.Clear();
            ListTitleText.Text = "PACKAGES";
            PackageCountText.Text = "0 packages";
            StatusText.Text = _context.UnavailableReason;
            AppendProgress(_context.UnavailableReason, true);
            RefreshDetails();
            UpdateButtons();
            return;
        }

        await ExecuteAsync("Refreshing installed packages", async () => await _mpm.RefreshAsync(_context));
    }

    private async Task AddProjectFileAsync()
    {
        var workspace = _getWorkspacePath();
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            SetStatusError("Open a workspace folder before adding a project file.");
            return;
        }

        var choice = PromptForProjectFile(_context.IsAvailable ? _context.Provider : MpmProviderKind.Python);
        if (choice is null) return;
        var result = await _mpm.AddProjectFileAsync(workspace, choice.Provider, choice.FileName);
        if (!string.IsNullOrWhiteSpace(result.Output)) AppendProgress(result.Output, !result.Success);
        StatusText.Text = result.Message;
        if (!result.Success)
        {
            AppendProgress(result.Message, true);
            return;
        }

        AppendProgress(result.Message, false);
        await RefreshPackagesAsync();
    }

    private async Task SearchAsync()
    {
        var packageName = RequestedPackageName();
        if (string.IsNullOrWhiteSpace(packageName))
        {
            SetStatusError("Enter a package name before searching.");
            return;
        }
        _context = _mpm.DetectProject(_getWorkspacePath());
        if (!_context.IsAvailable) { SetStatusError(_context.UnavailableReason); return; }
        await ExecuteAsync($"Searching {packageName}", async () => await _mpm.SearchAsync(_context, packageName));
    }

    private async Task ShowInfoAsync()
    {
        var packageName = RequestedPackageName();
        if (string.IsNullOrWhiteSpace(packageName)) { SetStatusError("Select a package or enter a package name first."); return; }
        _context = _mpm.DetectProject(_getWorkspacePath());
        if (!_context.IsAvailable) { SetStatusError(_context.UnavailableReason); return; }
        await ExecuteAsync($"Loading information for {packageName}", async () => await _mpm.InfoAsync(_context, packageName), replaceList: false);
    }

    private async Task InstallAsync()
    {
        var packageName = RequestedPackageName();
        if (string.IsNullOrWhiteSpace(packageName)) { SetStatusError("Select a package or search for a package before installing."); return; }
        _context = _mpm.DetectProject(_getWorkspacePath());
        if (!_context.IsAvailable) { SetStatusError(_context.UnavailableReason); return; }
        var confirmation = $"Install '{packageName}' from { _context.SourceName }?\n\nSource: {_context.SourceUrl}{packageName}\n\nMPM runs package commands without a shell and disables package scripts where supported. Python source distributions are rejected; only binary wheels are allowed.";
        if (MessageBox.Show(confirmation, "Confirm MPM Installation", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await ExecuteAsync($"Installing {packageName}", async () => await _mpm.InstallAsync(_context, packageName));
    }

    private async Task UninstallAsync()
    {
        var packageName = (PackageList.SelectedItem as MpmPackage)?.Name;
        if (string.IsNullOrWhiteSpace(packageName)) { SetStatusError("Select an installed package to uninstall."); return; }
        _context = _mpm.DetectProject(_getWorkspacePath());
        if (!_context.IsAvailable) { SetStatusError(_context.UnavailableReason); return; }
        if (MessageBox.Show($"Remove '{packageName}' from this project?\n\nMPM will remove it from the project environment and .codebox-mpm.json.", "Confirm MPM Removal", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await ExecuteAsync($"Removing {packageName}", async () => await _mpm.UninstallAsync(_context, packageName));
    }

    private async Task UpdateAsync()
    {
        _context = _mpm.DetectProject(_getWorkspacePath());
        if (!_context.IsAvailable) { SetStatusError(_context.UnavailableReason); return; }
        if (MessageBox.Show("Update every dependency recorded in .codebox-mpm.json?\n\nMPM will use the configured project package source and keep package scripts disabled where supported.", "Confirm MPM Update", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await ExecuteAsync("Updating recorded dependencies", async () => await _mpm.UpdateAsync(_context));
    }

    private async Task RestoreAsync()
    {
        _context = _mpm.DetectProject(_getWorkspacePath());
        if (!_context.IsAvailable) { SetStatusError(_context.UnavailableReason); return; }
        if (MessageBox.Show("Restore dependencies from .codebox-mpm.json?\n\nThis installs the recorded project dependencies from the configured public package source.", "Confirm MPM Restore", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await ExecuteAsync("Restoring project dependencies", async () => await _mpm.RestoreAsync(_context));
    }

    private async Task ExecuteAsync(string activity, Func<Task<MpmOperationResult>> action, bool replaceList = true)
    {
        if (_isBusy) return;
        _isBusy = true;
        StatusText.Text = activity + "...";
        UpdateButtons();
        try
        {
            var result = await action();
            if (!string.IsNullOrWhiteSpace(result.Output)) AppendProgress(result.Output, !result.Success);
            if (replaceList && result.Packages.Count > 0)
            {
                ReplacePackages(result.Packages, activity.StartsWith("Searching", StringComparison.OrdinalIgnoreCase) ? "SEARCH RESULTS" : "INSTALLED PACKAGES");
            }
            else if (replaceList && result.Success && activity.StartsWith("Refreshing", StringComparison.OrdinalIgnoreCase))
            {
                ReplacePackages(result.Packages, "INSTALLED PACKAGES");
            }
            else if (!replaceList && result.Packages.Count > 0)
            {
                SelectOrShow(result.Packages[0]);
            }
            StatusText.Text = result.Message;
            if (!result.Success) AppendProgress(result.Message, true);
        }
        finally
        {
            _isBusy = false;
            UpdateButtons();
        }
    }

    private void ReplacePackages(IReadOnlyList<MpmPackage> packages, string title)
    {
        var selectedName = (PackageList.SelectedItem as MpmPackage)?.Name;
        _packages.Clear();
        foreach (var package in packages) _packages.Add(package);
        ListTitleText.Text = title;
        PackageCountText.Text = $"{_packages.Count} package(s)";
        PackageList.SelectedItem = _packages.FirstOrDefault(package => string.Equals(package.Name, selectedName, StringComparison.OrdinalIgnoreCase)) ?? _packages.FirstOrDefault();
        RefreshDetails();
    }

    private void SelectOrShow(MpmPackage package)
    {
        var existing = _packages.FirstOrDefault(item => string.Equals(item.Name, package.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) PackageList.SelectedItem = existing;
        else
        {
            _packages.Clear();
            _packages.Add(package);
            ListTitleText.Text = "PACKAGE INFORMATION";
            PackageCountText.Text = "1 package";
            PackageList.SelectedItem = package;
        }
        RefreshDetails();
    }

    private ProjectFileChoice? PromptForProjectFile(MpmProviderKind preferredProvider)
    {
        var choices = new[]
        {
            new ProjectFileChoice(MpmProviderKind.Python, "requirements.txt", "Python — requirements.txt"),
            new ProjectFileChoice(MpmProviderKind.Python, "pyproject.toml", "Python — pyproject.toml"),
            new ProjectFileChoice(MpmProviderKind.Node, "package.json", "Node.js / TypeScript — package.json"),
            new ProjectFileChoice(MpmProviderKind.DotNet, "App.csproj", ".NET / C# — App.csproj"),
            new ProjectFileChoice(MpmProviderKind.DotNet, "App.fsproj", ".NET / F# — App.fsproj")
        };
        var initial = choices.FirstOrDefault(choice => choice.Provider == preferredProvider) ?? choices[0];
        var dialog = new Window
        {
            Title = "Add MPM Project File",
            Owner = this,
            Width = 475,
            Height = 240,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.SystemColors.WindowBrush,
            ShowInTaskbar = false
        };
        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var prompt = new TextBlock { Text = "Choose the project file MPM should create in the current workspace. Existing files are never overwritten.", TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(prompt, 0);
        layout.Children.Add(prompt);
        var kindLabel = new TextBlock { Text = "Project type", Margin = new Thickness(0, 14, 0, 4) };
        Grid.SetRow(kindLabel, 1);
        layout.Children.Add(kindLabel);
        var typeBox = new ComboBox { ItemsSource = choices, SelectedItem = initial, DisplayMemberPath = nameof(ProjectFileChoice.Label), MinWidth = 320 };
        Grid.SetRow(typeBox, 2);
        layout.Children.Add(typeBox);
        var filePanel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        filePanel.Children.Add(new TextBlock { Text = "File name" });
        var fileNameBox = new TextBox { Text = initial.FileName, Margin = new Thickness(0, 4, 0, 0) };
        filePanel.Children.Add(fileNameBox);
        Grid.SetRow(filePanel, 3);
        layout.Children.Add(filePanel);
        typeBox.SelectionChanged += (_, _) =>
        {
            if (typeBox.SelectedItem is ProjectFileChoice selected) fileNameBox.Text = selected.FileName;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 78 };
        var create = new Button { Content = "Create", IsDefault = true, MinWidth = 78, Margin = new Thickness(7, 0, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(create);
        Grid.SetRow(buttons, 5);
        layout.Children.Add(buttons);
        dialog.Content = layout;
        create.Click += (_, _) => dialog.DialogResult = true;
        return dialog.ShowDialog() == true && typeBox.SelectedItem is ProjectFileChoice selected && !string.IsNullOrWhiteSpace(fileNameBox.Text)
            ? selected with { FileName = fileNameBox.Text.Trim() }
            : null;
    }

    private void PackageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshDetails();
        UpdateButtons();
    }

    private void RefreshDetails()
    {
        var package = PackageList.SelectedItem as MpmPackage;
        if (package is null)
        {
            DetailNameText.Text = "Select a package";
            DetailVersionText.Text = DetailSourceText.Text = DetailCompatibilityText.Text = string.Empty;
            DetailDescriptionText.Text = "Search a package or select an installed package to view its details.";
            return;
        }
        DetailNameText.Text = package.Name;
        DetailVersionText.Text = $"{package.Status} • {package.VersionLabel}";
        DetailSourceText.Text = "Source: " + package.Source;
        DetailDescriptionText.Text = string.IsNullOrWhiteSpace(package.Description) ? "No package description was returned by the provider." : package.Description;
        DetailCompatibilityText.Text = package.CompatibilityMessage;
    }

    private void UpdateButtons()
    {
        var hasContext = _context.IsAvailable && !_isBusy;
        var package = PackageList.SelectedItem as MpmPackage;
        InstallButton.IsEnabled = hasContext && !string.IsNullOrWhiteSpace(RequestedPackageName());
        UninstallButton.IsEnabled = hasContext && package?.IsInstalled == true;
        UpdateButton.IsEnabled = hasContext;
    }

    private string RequestedPackageName() => (PackageList.SelectedItem as MpmPackage)?.Name ?? SearchBox.Text.Trim();
    private void Mpm_ProgressChanged(object? sender, MpmProgressEventArgs e) => Dispatcher.BeginInvoke(() => AppendProgress(e.Message, e.IsError));
    private void AppendProgress(string message, bool isError)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        foreach (var line in message.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries)) ProgressBox.AppendText((isError ? "[error] " : "") + line + Environment.NewLine);
        ProgressBox.ScrollToEnd();
    }
    private void SetStatusError(string message) { StatusText.Text = message; AppendProgress(message, true); }
    private void MpmWindow_Closing(object? sender, CancelEventArgs e) { e.Cancel = true; Hide(); }
    private sealed record ProjectFileChoice(MpmProviderKind Provider, string FileName, string Label);
}
