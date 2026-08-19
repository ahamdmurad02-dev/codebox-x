using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CodeBoxX.Models;
using CodeBoxX.Services;

namespace CodeBoxX.Views;

public partial class AiAssistantWindow : Window
{
    private readonly GeminiService _gemini;
    private readonly Func<AiEditorAction, string, AiRequestContext> _contextFactory;
    private readonly Action<string> _insertCode;
    private readonly Action _openSettings;
    private readonly ObservableCollection<AiChatMessage> _messages = [];
    private string _lastResponse = string.Empty;
    private AiEditorAction _pendingAction = AiEditorAction.Chat;
    private bool _isSending;

    public AiAssistantWindow(GeminiService gemini, Func<AiEditorAction, string, AiRequestContext> contextFactory, Action<string> insertCode, Action openSettings)
    {
        InitializeComponent();
        _gemini = gemini;
        _contextFactory = contextFactory;
        _insertCode = insertCode;
        _openSettings = openSettings;
        ChatList.ItemsSource = _messages;
        UpdateResponseButtons();
    }

    public void ShowAssistant()
    {
        Owner ??= Application.Current.MainWindow;
        if (!IsVisible) Show();
        Activate();
        PromptBox.Focus();
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var prompt = PromptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            StatusText.Text = "Enter a question or choose an AI action.";
            PromptBox.Focus();
            return;
        }
        var action = _pendingAction;
        _pendingAction = AiEditorAction.Chat;
        await SendAsync(action, prompt);
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<AiEditorAction>(tag, out var action)) return;
        if (action is AiEditorAction.Generate or AiEditorAction.ProjectQuestion)
        {
            _pendingAction = action;
            PromptBox.Focus();
            StatusText.Text = action == AiEditorAction.Generate ? "Describe the code you want to generate, then select Send." : "Ask a question about the current project, then select Send.";
            return;
        }
        var prompt = action switch
        {
            AiEditorAction.Explain => "Explain this code.",
            AiEditorAction.Fix => "Find and fix errors in this code.",
            AiEditorAction.Refactor => "Refactor this code for clarity and maintainability.",
            AiEditorAction.AddComments => "Add useful comments to this code.",
            _ => "Help with this code."
        };
        await SendAsync(action, prompt);
    }

    private async Task SendAsync(AiEditorAction action, string prompt)
    {
        if (_isSending) return;
        _isSending = true;
        SetBusy(true);
        _messages.Add(new AiChatMessage { Role = "You", Text = prompt });
        ScrollToLatest();
        try
        {
            var result = await _gemini.GenerateAsync(_contextFactory(action, prompt));
            if (result.Success)
            {
                _lastResponse = result.Text;
                _messages.Add(new AiChatMessage { Role = "Assistant", Text = result.Text });
                PromptBox.Clear();
                StatusText.Text = "Gemini response ready.";
            }
            else
            {
                _messages.Add(new AiChatMessage { Role = "Assistant", Text = result.UserMessage });
                StatusText.Text = result.UserMessage;
            }
        }
        finally
        {
            _isSending = false;
            SetBusy(false);
            ScrollToLatest();
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => _openSettings();

    private void ClearChat_Click(object sender, RoutedEventArgs e)
    {
        _messages.Clear();
        _lastResponse = string.Empty;
        StatusText.Text = "Chat cleared.";
        UpdateResponseButtons();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastResponse)) return;
        Clipboard.SetText(_lastResponse);
        StatusText.Text = "Response copied to the clipboard.";
    }

    private void Insert_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastResponse)) return;
        var code = GeminiService.ExtractInsertableCode(_lastResponse);
        if (string.IsNullOrWhiteSpace(code))
        {
            StatusText.Text = "There is no insertable code in the latest response.";
            return;
        }
        _insertCode(code);
        StatusText.Text = "AI-generated code inserted into the editor.";
    }

    private void SetBusy(bool busy)
    {
        SendButton.IsEnabled = !busy;
        InsertButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(_lastResponse);
        CopyButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(_lastResponse);
        if (busy) StatusText.Text = "Gemini is thinking…";
    }

    private void UpdateResponseButtons()
    {
        InsertButton.IsEnabled = !string.IsNullOrWhiteSpace(_lastResponse);
        CopyButton.IsEnabled = !string.IsNullOrWhiteSpace(_lastResponse);
    }

    private void ScrollToLatest()
    {
        if (_messages.Count > 0) ChatList.ScrollIntoView(_messages[^1]);
    }

    private void AiAssistantWindow_Closing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
