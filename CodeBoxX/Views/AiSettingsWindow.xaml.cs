using System.Windows;
using CodeBoxX.Services;

namespace CodeBoxX.Views;

public partial class AiSettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly GeminiService _gemini;

    public AiSettingsWindow(AppSettings settings, GeminiService gemini)
    {
        InitializeComponent();
        _settings = settings;
        _gemini = gemini;
        StatusText.Text = string.IsNullOrWhiteSpace(_settings.GetGeminiApiKey()) ? "No API key saved. Add a Gemini API key to use the AI Assistant." : "An API key is securely saved for this Windows user.";
    }

    private string CurrentKey => ShowKeyBox.IsChecked == true ? ApiKeyTextBox.Text.Trim() : ApiKeyPasswordBox.Password.Trim();

    private void ShowKeyBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (ShowKeyBox.IsChecked == true)
        {
            ApiKeyTextBox.Text = ApiKeyPasswordBox.Password;
            ApiKeyTextBox.Visibility = Visibility.Visible;
            ApiKeyPasswordBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            ApiKeyPasswordBox.Password = ApiKeyTextBox.Text;
            ApiKeyPasswordBox.Visibility = Visibility.Visible;
            ApiKeyTextBox.Visibility = Visibility.Collapsed;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var key = CurrentKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            StatusText.Text = "Enter an API key before saving, or use Clear API Key to remove the saved key.";
            return;
        }
        try
        {
            _settings.SetGeminiApiKey(key);
            _settings.Save();
            StatusText.Text = "API key saved securely with Windows user-level data protection.";
            ClearEditorInput();
        }
        catch
        {
            StatusText.Text = "The API key could not be saved securely. Please try again.";
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Remove the saved Gemini API key for this Windows user?", "Clear Gemini API Key", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            _settings.ClearGeminiApiKey();
            _settings.Save();
            ClearEditorInput();
            StatusText.Text = "Saved API key removed.";
        }
        catch
        {
            StatusText.Text = "The saved API key could not be removed.";
        }
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        var temporaryKey = CurrentKey;
        var hasTemporaryKey = !string.IsNullOrWhiteSpace(temporaryKey);
        if (!hasTemporaryKey && string.IsNullOrWhiteSpace(_settings.GetGeminiApiKey()))
        {
            StatusText.Text = "Save or enter an API key before testing the connection.";
            return;
        }
        StatusText.Text = "Testing the Gemini connection…";
        try
        {
            var result = await _gemini.TestConnectionAsync(hasTemporaryKey ? temporaryKey : null);
            StatusText.Text = result.Success ? "Connection successful. Gemini 3.1 Flash-Lite is ready." : result.UserMessage;
        }
        catch
        {
            StatusText.Text = "The connection test could not be completed. Check your network connection and try again.";
        }
    }

    private void ClearEditorInput()
    {
        ApiKeyPasswordBox.Password = string.Empty;
        ApiKeyTextBox.Text = string.Empty;
    }
}
