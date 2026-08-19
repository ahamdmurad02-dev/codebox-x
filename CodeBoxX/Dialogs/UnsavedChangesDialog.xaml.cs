using System.Windows;

namespace CodeBoxX.Dialogs;

public enum UnsavedChangesChoice
{
    Cancel,
    Save,
    DontSave
}

public partial class UnsavedChangesDialog : Window
{
    public UnsavedChangesChoice Choice { get; private set; } = UnsavedChangesChoice.Cancel;

    public UnsavedChangesDialog(string documentName)
    {
        InitializeComponent();
        PromptText.Text = $"Do you want to save changes to {documentName}?";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Choice = UnsavedChangesChoice.Save;
        DialogResult = true;
    }

    private void DontSave_Click(object sender, RoutedEventArgs e)
    {
        Choice = UnsavedChangesChoice.DontSave;
        DialogResult = true;
    }
}
