using System.Diagnostics;
using System.IO;
using System.Text;

namespace VideoAnalyzer.Services;

/// <summary>Thin, allocation friendly wrapper around <see cref="Process"/> for ffmpeg tooling.</summary>
public static class ProcessRunner
{
    /// <summary>Runs a tool and returns everything it wrote to stdout.</summary>
    public static string RunCapture(string exe, string arguments, TimeSpan timeout)
    {
        using var p = Start(exe, arguments);
        var stdout = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { p.Kill(true); } catch { }
            throw new TimeoutException($"{Path.GetFileName(exe)} timed out after {timeout.TotalSeconds:0}s");
        }
        return stdout;
    }

    public static Process Start(string exe, string arguments)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Start();
        return p;
    }

    /// <summary>
    /// Streams stderr line by line (ffmpeg writes its progress/report there) while
    /// swallowing stdout. Cancellation kills the process tree.
    /// </summary>
    public static async Task<int> RunStreamingAsync(
        string exe,
        string arguments,
        Action<string>? onStderrLine,
        CancellationToken ct)
    {
        using var p = Start(exe, arguments);
        var drain = p.StandardOutput.ReadToEndAsync(ct);

        try
        {
            string? line;
            while ((line = await p.StandardError.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                onStderrLine?.Invoke(line);
        }
        catch (OperationCanceledException)
        {
            TryKill(p);
            throw;
        }

        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        await drain.ConfigureAwait(false);
        return p.ExitCode;
    }

    public static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(true); } catch { }
    }
}
