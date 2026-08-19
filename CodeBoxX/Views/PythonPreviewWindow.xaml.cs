using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using CodeBoxX.Models;
using CodeBoxX.Services;

namespace CodeBoxX.Views;

public partial class PythonPreviewWindow : Window
{
    private static readonly TimeSpan PreviewTimeout = TimeSpan.FromMinutes(15);
    private readonly PythonPreviewSession _session;
    private readonly Func<EditorDocument, bool> _saveDocument;
    private EditorDocument? _document;
    private long _visibleSessionId;
    private bool _isStarting;

    public PythonPreviewWindow(PythonPreviewSession session, Func<EditorDocument, bool> saveDocument)
    {
        _session = session;
        _saveDocument = saveDocument;
        InitializeComponent();
        _session.DataReceived += Session_DataReceived;
        _session.StatusChanged += Session_StatusChanged;
        SetStatus("Ready", running: false, isError: false);
    }

    public EditorDocument? CurrentDocument => _document;

    public void ShowDocument(EditorDocument document)
    {
        _document = document;
        Owner ??= Application.Current.MainWindow;
        DocumentNameText.Text = document.FilePath ?? document.FileNameHint;
        Title = $"CodeBox X — Python Live Preview — {document.FileNameHint}";
        if (!IsVisible) Show();
        Activate();
        _ = StartPreviewAsync(restart: false);
    }

    public void RestartIfShowing(EditorDocument document)
    {
        if (!IsVisible || !ReferenceEquals(_document, document) || !_session.IsRunning) return;
        _ = StartPreviewAsync(restart: true);
    }

    private async void Run_Click(object sender, RoutedEventArgs e) => await StartPreviewAsync(restart: false);

    private async void Restart_Click(object sender, RoutedEventArgs e) => await StartPreviewAsync(restart: true);

    private void Stop_Click(object sender, RoutedEventArgs e) => StopPreview();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        OutputBox.Clear();
        ErrorBox.Clear();
        FooterText.Text = "Output cleared.";
    }

    private async Task StartPreviewAsync(bool restart)
    {
        if (_isStarting) return;
        if (_document is null)
        {
            ReportUserError("Open a saved Python file before starting Live Preview.");
            return;
        }

        if (!string.Equals(Path.GetExtension(_document.FilePath ?? _document.FileNameHint), ".py", StringComparison.OrdinalIgnoreCase))
        {
            ReportUserError("Python Live Preview supports .py files only. Select a Python file, then run Live Preview.");
            return;
        }

        _isStarting = true;
        try
        {
            if (_document.IsDirty || string.IsNullOrWhiteSpace(_document.FilePath))
            {
                if (!_saveDocument(_document))
                {
                    ReportUserError("Save the Python file before running Live Preview.");
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(_document.FilePath))
            {
                ReportUserError("Save the Python file to a valid location before running Live Preview.");
                return;
            }

            DocumentNameText.Text = _document.FilePath;
            OutputBox.Clear();
            ErrorBox.Clear();
            FooterText.Text = restart ? "Restarting Python Live Preview..." : "Starting Python Live Preview...";
            SetStatus(restart ? "Restarting" : "Starting", running: true, isError: false);

            var startTask = _session.StartAsync(_document.FilePath, PreviewTimeout);
            _visibleSessionId = _session.CurrentSessionId;
            var result = await startTask;
            if (!result.Success)
            {
                _visibleSessionId = 0;
                ReportUserError(result.Message);
                return;
            }

            _visibleSessionId = result.SessionId;
            InterpreterText.Text = result.InterpreterPath;
            FooterText.Text = string.IsNullOrWhiteSpace(result.Guidance) ? result.Message : result.Guidance;
            if (!string.IsNullOrWhiteSpace(result.Guidance))
            {
                OutputBox.AppendText(result.Guidance + Environment.NewLine);
                OutputBox.ScrollToEnd();
            }
        }
        catch (OperationCanceledException)
        {
            ReportUserError("Python Live Preview startup was cancelled.");
        }
        catch (Exception ex)
        {
            ReportUserError($"Python Live Preview could not start: {ex.Message}");
        }
        finally
        {
            _isStarting = false;
        }
    }

    private void StopPreview()
    {
        var wasRunning = _session.IsRunning;
        _session.Stop();
        _visibleSessionId = 0;
        SetStatus("Stopped", running: false, isError: false);
        FooterText.Text = wasRunning ? "Python Live Preview stopped." : "No Python preview process is running.";
    }

    private void Session_DataReceived(object? sender, PythonPreviewData data)
    {
        if (data.SessionId != _visibleSessionId) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (data.SessionId != _visibleSessionId) return;
            var target = data.Stream == PythonPreviewStream.Error ? ErrorBox : OutputBox;
            target.AppendText(data.Text + Environment.NewLine);
            target.ScrollToEnd();
        });
    }

    private void Session_StatusChanged(object? sender, PythonPreviewStatus status)
    {
        if (status.SessionId != _visibleSessionId) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (status.SessionId != _visibleSessionId) return;
            SetStatus(status.Message, status.IsRunning, status.IsError);
            FooterText.Text = status.Message;
        });
    }

    private void ReportUserError(string message)
    {
        ErrorBox.AppendText(message + Environment.NewLine);
        ErrorBox.ScrollToEnd();
        SetStatus("Preview unavailable", running: false, isError: true);
        FooterText.Text = message;
    }

    private void SetStatus(string message, bool running, bool isError)
    {
        StatusText.Text = message;
        StatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isError ? "#E74C3C" : running ? "#34C759" : "#808890"));
        StatusPill.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isError ? "#FDE2E2" : running ? "#DCF7E5" : "#E8EBEE"));
    }

    private void PythonPreviewWindow_Closing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        StopPreview();
        Hide();
    }
}
