using System.Windows;
using System.Windows.Threading;

namespace CodeBoxX;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeBoxX", "runtime-errors.log");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"{DateTimeOffset.Now:O}{Environment.NewLine}{args.Exception}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never mask the original UI exception.
        }

        MessageBox.Show($"CodeBox X encountered an error. Details were written to:{Environment.NewLine}{logPath}{Environment.NewLine}{Environment.NewLine}{args.Exception.Message}", "CodeBox X", MessageBoxButton.OK, MessageBoxImage.Error);
        args.Handled = true;
    }
}
