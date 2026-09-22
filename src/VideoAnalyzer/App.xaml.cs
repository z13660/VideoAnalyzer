using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace VideoAnalyzer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandledException;

        // Allow "VideoAnalyzer.exe <path>" to open a file straight away.
        if (e.Args.Length > 0 && File.Exists(e.Args[0]))
        {
            StartupFile = Path.GetFullPath(e.Args[0]);
        }
    }

    /// <summary>File passed on the command line, if any.</summary>
    public static string? StartupFile { get; private set; }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.ToString(),
            "VideoAnalyzer — unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
