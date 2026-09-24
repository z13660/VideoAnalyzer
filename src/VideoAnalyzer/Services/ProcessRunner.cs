using System.Diagnostics;
using System.IO;
using System.Text;

namespace VideoAnalyzer.Services;

/// <summary>Thin, allocation friendly wrapper around <see cref="Process"/> for ffmpeg tooling.</summary>
public static class ProcessRunner
{
    /// <summary>
    /// Groups for <see cref="KillAll"/>. A decode belongs to whoever asked for it, so cancelling
    /// the analysis can take its four decoders down without touching the one drawing the picture.
    /// </summary>
    public const string Analysis = "analysis";
    public const string Playback = "playback";

    private static readonly object Gate = new();
    private static readonly Dictionary<Process, string> Live = new();

    /// <summary>Runs a tool and returns everything it wrote to stdout.</summary>
    public static string RunCapture(string exe, string arguments, TimeSpan timeout, string owner = Playback)
    {
        using var p = Start(exe, arguments, owner);
        var stdout = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { p.Kill(true); } catch { }
            throw new TimeoutException($"{Path.GetFileName(exe)} timed out after {timeout.TotalSeconds:0}s");
        }
        return stdout;
    }

    public static Process Start(string exe, string arguments, string owner = Playback)
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

        // Registered so the application can take its decoders down explicitly. Relying on the
        // cancellation token alone leaves them behind: the token stops the read loop, and if the
        // window is closing — or a new file is being opened — nothing waits for that loop to
        // reach its kill.
        lock (Gate) Live[p] = owner;
        p.Exited += (_, _) => { lock (Gate) Live.Remove(p); };

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
        CancellationToken ct,
        string owner = Playback)
    {
        using var p = Start(exe, arguments, owner);
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
        try
        {
            if (p.HasExited) return;
            p.Kill(true);
        }
        catch { }
    }

    /// <summary>
    /// Kills every registered decoder, or just one group of them. Called when the window closes
    /// and whenever a file is opened, so no ffmpeg outlives the work it was started for.
    /// </summary>
    public static int KillAll(string? owner = null)
    {
        List<Process> targets;
        lock (Gate)
        {
            targets = Live.Where(kv => owner is null || kv.Value == owner).Select(kv => kv.Key).ToList();
            foreach (var p in targets) Live.Remove(p);
        }

        int killed = 0;
        foreach (var p in targets)
        {
            try
            {
                if (p.HasExited) continue;
                p.Kill(true);
                killed++;
            }
            catch { }
        }

        if (killed > 0) AppLog.Write($"proc: killed {killed} decoder(s) owner={owner ?? "all"}");
        return killed;
    }

    /// <summary>How many decoders are registered for a group — used by the status line.</summary>
    public static int LiveCount(string? owner = null)
    {
        lock (Gate) return Live.Count(kv => owner is null || kv.Value == owner);
    }
}
