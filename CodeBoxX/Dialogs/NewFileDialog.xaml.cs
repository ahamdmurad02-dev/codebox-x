using System.Windows;
using System.Windows.Controls;

namespace CodeBoxX.Dialogs;

public partial class NewFileDialog : Window
{
    public string FileName => FileNameBox.Text.Trim();
    public string SelectedExtension => (FileTypeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? ".txt";

    public NewFileDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            FileNameBox.Focus();
            FileNameBox.SelectAll();
        };
    }

    private void FileTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FileNameBox.Text) || Path.HasExtension(FileNameBox.Text)) return;
        FileNameBox.Text = $"untitled{SelectedExtension}";
        FileNameBox.Select(FileNameBox.Text.Length, 0);
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FileName))
        {
            MessageBox.Show("Enter a file name.", "New File", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || FileName.Contains(Path.DirectorySeparatorChar) || FileName.Contains(Path.AltDirectorySeparatorChar))
        {
            MessageBox.Show("Enter a valid file name without a folder path.", "New File", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
