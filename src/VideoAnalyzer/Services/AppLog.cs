using System.IO;
using System.Text;

namespace VideoAnalyzer.Services;

/// <summary>
/// Minimal append-only diagnostic log written next to the executable
/// (<c>videoanalyzer.log</c>). WPF apps have no console, so this is the only way to
/// see what the analysis pipeline did when something silently produces nothing.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "videoanalyzer.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(Path,
                    $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // logging must never take the app down
        }
    }

    public static void Write(string stage, Exception ex) => Write($"{stage}: {ex}");
}
