using System.Windows;

namespace CodeBoxX.Dialogs;

public partial class InputDialog : Window
{
    public string Value => ValueBox.Text.Trim();

    public InputDialog(string prompt, string title = "CodeBox X", string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Value))
        {
            MessageBox.Show("Please enter a value.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
