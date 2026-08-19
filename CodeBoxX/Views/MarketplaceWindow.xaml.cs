using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodeBoxX.Models;
using CodeBoxX.Services;

namespace CodeBoxX.Views;

public partial class MarketplaceWindow : Window
{
    private readonly ExtensionMarketplaceService _marketplace;
    private readonly ObservableCollection<MarketplaceEntryView> _entries = [];
    private bool _isRefreshingFilters;
    private bool _isInitializing = true;
    private bool _installedOnly;

    public event Action<ThemeDefinition, string, bool>? ThemeRequested;
    public event Action? ResetThemeRequested;

    public MarketplaceWindow(ExtensionMarketplaceService marketplace)
    {
        _marketplace = marketplace;
        InitializeComponent();
        _marketplace.ExtensionsChanged += (_, _) => Dispatcher.Invoke(RefreshAll);
        ExtensionList.ItemsSource = _entries;
        Loaded += (_, _) =>
        {
            _isInitializing = false;
            RefreshAll();
        };
    }

    public void ShowMarketplace()
    {
        Owner ??= Application.Current.MainWindow;
        if (!IsVisible) Show();
        Activate();
        if (IsInitialized)
        {
            _isInitializing = false;
            RefreshAll();
        }
    }

    private void Installed_Click(object sender, RoutedEventArgs e)
    {
        _installedOnly = !_installedOnly;
        SectionTitleText.Text = _installedOnly ? "Installed Extensions" : "All Extensions";
        RefreshList();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitializing) RefreshList();
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isRefreshingFilters) return;
        _isRefreshingFilters = true;
        TypeList.SelectedIndex = -1;
        _isRefreshingFilters = false;
        _installedOnly = false;
        SectionTitleText.Text = (CategoryList.SelectedItem as ListBoxItem)?.Content?.ToString() ?? "All Extensions";
        RefreshList();
    }

    private void TypeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isRefreshingFilters || TypeList.SelectedItem is not ListBoxItem item) return;
        _isRefreshingFilters = true;
        CategoryList.SelectedIndex = -1;
        _isRefreshingFilters = false;
        _installedOnly = false;
        SectionTitleText.Text = item.Content?.ToString() ?? "Extensions";
        RefreshList();
    }

    private void RefreshAll()
    {
        RefreshList();
    }

    private void RefreshList()
    {
        if (!IsInitialized) return;
        var search = SearchBox.Text?.Trim() ?? string.Empty;
        var category = (CategoryList.SelectedItem as ListBoxItem)?.Content?.ToString();
        var type = (TypeList.SelectedItem as ListBoxItem)?.Content?.ToString();
        var catalog = _marketplace.Catalog.AsEnumerable();

        if (_installedOnly) catalog = catalog.Where(package => _marketplace.IsInstalled(package.Manifest.Id));
        if (!string.IsNullOrWhiteSpace(search))
        {
            catalog = catalog.Where(package => package.Manifest.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || package.Manifest.Description.Contains(search, StringComparison.OrdinalIgnoreCase)
                || package.Manifest.TypeLabel.Contains(search, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(category))
        {
            catalog = category switch
            {
                "Featured" => catalog.Where(package => package.Manifest.IsFeatured),
                "Popular" => catalog.Where(package => package.Manifest.IsPopular),
                "Recently Added" => catalog.OrderByDescending(package => package.Manifest.PublishedAt).Take(12),
                "All Extensions" => catalog,
                _ => catalog.Where(package => string.Equals(package.Manifest.Category, category, StringComparison.OrdinalIgnoreCase))
            };
        }
        if (!string.IsNullOrWhiteSpace(type)) catalog = catalog.Where(package => MatchesType(package.Manifest.Types, type));

        var selectedId = (ExtensionList.SelectedItem as MarketplaceEntryView)?.Package.Manifest.Id;
        _entries.Clear();
        foreach (var package in catalog)
        {
            var installed = _marketplace.InstalledState.FirstOrDefault(state => state.Id.Equals(package.Manifest.Id, StringComparison.OrdinalIgnoreCase));
            var update = installed is not null && VersionNeedsUpdate(installed.Version, package.Manifest.UpdateVersion ?? package.Manifest.Version);
            _entries.Add(new MarketplaceEntryView(package, installed, update));
        }
        ResultCountText.Text = $"{_entries.Count} extension(s)";
        var matchingIndex = _entries.ToList().FindIndex(entry => entry.Package.Manifest.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
        ExtensionList.SelectedIndex = -1;
        ExtensionList.SelectedIndex = matchingIndex >= 0 ? matchingIndex : _entries.Count > 0 ? 0 : -1;
        RefreshDetails();
    }

    private void ExtensionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitializing) RefreshDetails();
    }

    private void RefreshDetails()
    {
        var entry = ExtensionList.SelectedItem as MarketplaceEntryView;
        if (entry is null)
        {
            DetailNameText.Text = "Select an extension";
            DetailMetaText.Text = DetailDescriptionText.Text = PermissionsText.Text = UpdateText.Text = string.Empty;
            SetButtons(false, false, false, false, false);
            return;
        }

        var manifest = entry.Package.Manifest;
        DetailNameText.Text = manifest.Name;
        DetailMetaText.Text = $"{manifest.TypeLabel} • {manifest.Version} • {manifest.Publisher}";
        DetailDescriptionText.Text = manifest.Description;
        PermissionsText.Text = manifest.RequiredPermissions.Count == 0 ? "No permissions required." : string.Join(Environment.NewLine, manifest.RequiredPermissions.Select(permission => $"• {permission}"));
        UpdateText.Text = entry.UpdateAvailable ? $"Update available: {manifest.UpdateVersion}" : string.Empty;
        var isTheme = entry.Package.Theme is not null;
        SetButtons(true, entry.IsInstalled, entry.IsInstalled, entry.UpdateAvailable, isTheme);
        EnableButton.Content = entry.IsEnabled ? "Disable" : "Enable";
        if (isTheme && entry.Package.Theme is not null)
        {
            ThemePreviewSwatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(entry.Package.Theme.EditorBackground));
        }
        else ThemePreviewSwatch.Background = (Brush)Application.Current.Resources["SurfaceBrush"];
    }

    private void SetButtons(bool hasSelection, bool installed, bool enableable, bool updateAvailable, bool isTheme)
    {
        InstallButton.IsEnabled = hasSelection && !installed;
        UninstallButton.IsEnabled = hasSelection && installed;
        EnableButton.IsEnabled = hasSelection && enableable;
        UpdateButton.IsEnabled = hasSelection && updateAvailable;
        ThemeSeparator.Visibility = isTheme ? Visibility.Visible : Visibility.Collapsed;
        ThemeActionsTitle.Visibility = isTheme ? Visibility.Visible : Visibility.Collapsed;
        PreviewThemeButton.Visibility = isTheme ? Visibility.Visible : Visibility.Collapsed;
        ApplyThemeButton.Visibility = isTheme ? Visibility.Visible : Visibility.Collapsed;
        ResetThemeButton.Visibility = isTheme ? Visibility.Visible : Visibility.Collapsed;
        ThemePreviewSwatch.Visibility = isTheme ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEntry() is not { } entry) return;
        var permissions = entry.Package.Manifest.RequiredPermissions.Count == 0 ? "No permissions" : string.Join(", ", entry.Package.Manifest.RequiredPermissions);
        var decision = MessageBox.Show($"Install {entry.Package.Manifest.Name}?\n\nPermissions: {permissions}\n\nCodeBox X installs validated data-only .cbxext packages. Unknown executable or script packages are rejected and are never executed.", "Confirm Extension Installation", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (decision != MessageBoxResult.Yes) return;
        CompleteAction(_marketplace.TryInstall(entry.Package.Manifest.Id, out var message), message);
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEntry() is not { } entry) return;
        if (MessageBox.Show($"Uninstall {entry.Package.Manifest.Name}?", "Confirm Uninstall", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        CompleteAction(_marketplace.TryUninstall(entry.Package.Manifest.Id, out var message), message);
    }

    private void Enable_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEntry() is not { } entry) return;
        CompleteAction(_marketplace.TrySetEnabled(entry.Package.Manifest.Id, !entry.IsEnabled, out var message), message);
    }

    private void Update_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEntry() is not { } entry) return;
        CompleteAction(_marketplace.TryUpdate(entry.Package.Manifest.Id, out var message), message);
    }

    private void PreviewTheme_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEntry()?.Package.Theme is not { } theme) return;
        ThemeRequested?.Invoke(theme, SelectedEntry()?.Package.Manifest.Id ?? string.Empty, false);
        StatusText.Text = $"Previewing {theme.Name}. Choose Apply Theme to keep it.";
    }

    private void ApplyTheme_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEntry() is not { } entry || entry.Package.Theme is not { } theme) return;
        if (!entry.IsInstalled || !entry.IsEnabled)
        {
            MessageBox.Show("Install and enable the theme before applying it.", "Theme", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ThemeRequested?.Invoke(theme, entry.Package.Manifest.Id, true);
        StatusText.Text = $"Applied {theme.Name}.";
    }

    private void ResetTheme_Click(object sender, RoutedEventArgs e)
    {
        ResetThemeRequested?.Invoke();
        StatusText.Text = "Reset to the selected built-in theme.";
    }

    private void CompleteAction(bool success, string message)
    {
        StatusText.Text = message;
        if (!success) MessageBox.Show(message, "Marketplace", MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshAll();
    }

    private MarketplaceEntryView? SelectedEntry() => ExtensionList.SelectedItem as MarketplaceEntryView;
    private static bool MatchesType(IReadOnlyCollection<ExtensionType> types, string label) => label switch
    {
        "Syntax Themes" => types.Contains(ExtensionType.SyntaxTheme),
        "Linters" => types.Contains(ExtensionType.Linter),
        "Formatters" => types.Contains(ExtensionType.Formatter),
        "Language Support" => types.Contains(ExtensionType.LanguageSupport),
        "Editor Tools" => types.Contains(ExtensionType.EditorTool),
        "Productivity" => types.Contains(ExtensionType.Productivity),
        _ => true
    };
    private static bool VersionNeedsUpdate(string installed, string available) => Version.TryParse(installed, out var current) && Version.TryParse(available, out var latest) ? current < latest : !string.Equals(installed, available, StringComparison.OrdinalIgnoreCase);

    private void MarketplaceWindow_Closing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}

public sealed class MarketplaceEntryView
{
    public MarketplaceEntryView(ExtensionPackage package, InstalledExtension? installed, bool updateAvailable)
    {
        Package = package;
        IsInstalled = installed is not null;
        IsEnabled = installed?.IsEnabled == true;
        UpdateAvailable = updateAvailable;
    }

    public ExtensionPackage Package { get; }
    public string Name => Package.Manifest.Name;
    public string Version => IsInstalled ? Package.Manifest.Version : Package.Manifest.Version;
    public string TypeLabel => Package.Manifest.TypeLabel;
    public string Description => Package.Manifest.Description;
    public bool IsInstalled { get; }
    public bool IsEnabled { get; }
    public bool UpdateAvailable { get; }
    public string Status => !IsInstalled ? "Available" : UpdateAvailable ? "Update available" : IsEnabled ? "Installed" : "Disabled";
}
