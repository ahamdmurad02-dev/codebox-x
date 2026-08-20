using System.Reflection;
using System.Windows;
using CodeBoxX.Services;

namespace CodeBoxX.Views;

public partial class AboutWindow : Window
{
    private readonly UpdateService _updateService;

    public AboutWindow(UpdateService updateService)
    {
        _updateService = updateService;
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 2, 1);
        VersionText.Text = $"Version {version.ToString(3)}";
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        var updateWindow = new UpdateWindow(_updateService) { Owner = Owner ?? this };
        updateWindow.ShowDialog();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
