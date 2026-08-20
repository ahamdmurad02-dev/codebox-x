using System.Reflection;
using System.Windows;
using CodeBoxX.Services;

namespace CodeBoxX.Views;

public partial class UpdateWindow : Window
{
    private readonly UpdateService _updateService;
    private readonly Version _installedVersion;
    private CancellationTokenSource? _operationCancellation;
    private UpdateCheckResult? _availableUpdate;
    private bool _operationInProgress;

    public UpdateWindow(UpdateService updateService)
    {
        _updateService = updateService;
        _installedVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 2, 1);
        InitializeComponent();
        CurrentVersionText.Text = FormatVersion(_installedVersion);
        LatestVersionText.Text = "Checking…";
        Loaded += async (_, _) => await CheckForUpdateAsync();
        Closed += (_, _) => _operationCancellation?.Cancel();
    }

    private async Task CheckForUpdateAsync()
    {
        if (_operationInProgress) return;
        _operationInProgress = true;
        _operationCancellation = new CancellationTokenSource();
        DownloadButton.IsEnabled = false;
        CancelButton.Content = "Cancel";
        StatusText.Text = "Checking the official CodeBox X GitHub Releases…";
        ReleaseNotesBox.Text = string.Empty;
        LatestVersionText.Text = "Checking…";

        try
        {
            var result = await _updateService.CheckForUpdateAsync(_installedVersion, _operationCancellation.Token);
            _availableUpdate = result.IsUpdateAvailable ? result : null;
            LatestVersionText.Text = result.LatestVersion is null ? "Unavailable" : FormatVersion(result.LatestVersion);
            StatusText.Text = result.Message;
            ReleaseNotesBox.Text = string.IsNullOrWhiteSpace(result.ReleaseNotes) ? "No release notes were supplied." : result.ReleaseNotes;
            DownloadButton.IsEnabled = result.IsUpdateAvailable;
        }
        finally
        {
            _operationInProgress = false;
            _operationCancellation.Dispose();
            _operationCancellation = null;
        }
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null || _operationInProgress) return;

        _operationInProgress = true;
        _operationCancellation = new CancellationTokenSource();
        DownloadButton.IsEnabled = false;
        CancelButton.Content = "Cancel Download";
        DownloadProgressBar.Value = 0;
        DownloadProgressBar.Visibility = Visibility.Visible;
        ProgressText.Visibility = Visibility.Visible;
        ProgressText.Text = "Starting verified download…";
        StatusText.Text = "Downloading the official CodeBox X installer…";

        var progress = new Progress<UpdateDownloadProgress>(value =>
        {
            DownloadProgressBar.IsIndeterminate = value.TotalBytes is null or 0;
            DownloadProgressBar.Value = value.Percentage;
            ProgressText.Text = value.TotalBytes is > 0
                ? $"Downloaded {FormatSize(value.ReceivedBytes)} of {FormatSize(value.TotalBytes.Value)} ({value.Percentage:0}%)"
                : $"Downloaded {FormatSize(value.ReceivedBytes)}";
        });

        try
        {
            var result = await _updateService.DownloadInstallerAsync(_availableUpdate, progress, _operationCancellation.Token);
            if (!result.Success || string.IsNullOrWhiteSpace(result.InstallerPath))
            {
                StatusText.Text = result.Message;
                MessageBox.Show(result.Message, "Update CodeBox X", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DownloadProgressBar.Value = 100;
            ProgressText.Text = "Download verified with GitHub SHA-256 digest.";
            StatusText.Text = "The verified update installer is ready.";
            var launch = MessageBox.Show(
                "The update installer was downloaded and verified. Start it now?\n\nSave your work before closing CodeBox X. The installer will provide the final installation and launch option.",
                "Update CodeBox X", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (launch != MessageBoxResult.Yes) return;

            var launchResult = _updateService.StartInstaller(result.InstallerPath);
            if (!launchResult.Success)
            {
                StatusText.Text = launchResult.Message;
                MessageBox.Show(launchResult.Message, "Update CodeBox X", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            StatusText.Text = "The update installer has started. Save your work and close CodeBox X to continue.";
            MessageBox.Show("The verified installer has started. Save your work and close CodeBox X when you are ready; the installer will complete the update safely.", "Update CodeBox X", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            _operationInProgress = false;
            if (_operationCancellation is not null)
            {
                _operationCancellation.Dispose();
                _operationCancellation = null;
            }
            CancelButton.Content = "Cancel";
            DownloadButton.IsEnabled = _availableUpdate is not null;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress)
        {
            _operationCancellation?.Cancel();
            StatusText.Text = "Cancelling…";
            return;
        }

        Close();
    }

    private static string FormatVersion(Version version) => version.Build >= 0 ? version.ToString(3) : version.ToString(2);

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024d / 1024d / 1024d:0.0} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / 1024d / 1024d:0.0} MB";
        if (bytes >= 1024) return $"{bytes / 1024d:0.0} KB";
        return $"{bytes} bytes";
    }
}
