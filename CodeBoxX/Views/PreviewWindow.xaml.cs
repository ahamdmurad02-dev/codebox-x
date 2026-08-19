using System.ComponentModel;
using System.Text;
using System.Windows;
using CodeBoxX.Models;
using CodeBoxX.Services;
using Microsoft.Web.WebView2.Core;

namespace CodeBoxX.Views;

public partial class PreviewWindow : Window
{
    private readonly string _previewCacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeBoxX", "PreviewCache");
    private readonly string _previewFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeBoxX", "PreviewCache", $"preview-{Guid.NewGuid():N}.html");
    private EditorDocument? _document;
    private Task? _browserInitialization;
    private int _previewVersion;

    public PreviewWindow()
    {
        InitializeComponent();
    }

    public bool AutoRefreshEnabled => AutoRefreshBox.IsChecked == true;
    public EditorDocument? CurrentDocument => _document;

    public void ShowDocument(EditorDocument document)
    {
        _document = document;
        Owner ??= Application.Current.MainWindow;
        if (!IsVisible) Show();
        Activate();
        RefreshPreview();
    }

    public void RefreshIfShowing(EditorDocument document)
    {
        if (!IsVisible || !AutoRefreshEnabled || !ReferenceEquals(_document, document)) return;
        RefreshPreview();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshPreview();

    private async void RefreshPreview()
    {
        if (_document is null)
        {
            ShowError("Select a document to preview.");
            return;
        }

        DocumentNameText.Text = _document.FilePath ?? _document.FileNameHint;
        if (!PreviewRenderer.TryRender(_document, out var html, out var title, out var error))
        {
            ShowError(error);
            return;
        }

        var version = ++_previewVersion;
        try
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Collapsed;
            PreviewBrowser.Visibility = Visibility.Visible;
            Title = $"CodeBox X — Live Preview — {title}";
            await EnsureBrowserReadyAsync();
            if (version != _previewVersion) return;

            var previewUri = await WritePreviewFileAsync(html);
            if (version != _previewVersion) return;

            // Navigate to a real file instead of NavigateToString. This gives the
            // Chromium-based engine a normal document lifecycle for local HTML,
            // which avoids the legacy script issues seen with in-memory preview
            // documents and allows adjacent CSS/JS assets to execute correctly.
            PreviewBrowser.CoreWebView2.Settings.IsScriptEnabled = true;
            PreviewBrowser.CoreWebView2.Navigate(previewUri);
            PreviewStatusText.Text = $"Preview updated {DateTime.Now:T}";
        }
        catch (WebView2RuntimeNotFoundException)
        {
            _browserInitialization = null;
            ShowError("Microsoft Edge WebView2 Runtime is required for Live Preview. Install or update Microsoft Edge/WebView2 Runtime, then restart CodeBox X.");
        }
        catch (Exception ex)
        {
            _browserInitialization = null;
            ShowError($"The embedded modern preview could not be displayed: {ex.Message}");
        }
    }

    private async Task EnsureBrowserReadyAsync()
    {
        _browserInitialization ??= PreviewBrowser.EnsureCoreWebView2Async();
        await _browserInitialization;
    }

    private async Task<string> WritePreviewFileAsync(string html)
    {
        Directory.CreateDirectory(_previewCacheDirectory);
        await File.WriteAllTextAsync(_previewFilePath, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new Uri(_previewFilePath).AbsoluteUri;
    }

    private void ShowError(string message)
    {
        _previewVersion++;
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
        PreviewBrowser.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Visible;
        PreviewStatusText.Text = "Preview unavailable";
    }

    private void PreviewWindow_Closing(object? sender, CancelEventArgs e)
    {
        // Keep the native window ready for the next preview request.
        e.Cancel = true;
        Hide();
    }
}
