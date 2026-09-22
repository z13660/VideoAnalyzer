using System.IO;

namespace VideoAnalyzer.Services;

/// <summary>
/// Locates the ffmpeg / ffprobe executables. Search order:
/// explicit override -> FFMPEG_HOME env var -> tools folder next to the app
/// (and up to 4 parent directories) -> PATH -> well known install locations.
/// </summary>
public static class FfmpegLocator
{
    private static string? _ffmpeg;
    private static string? _ffprobe;

    public static string? FfmpegPath => _ffmpeg ??= Find("ffmpeg.exe", "ffmpeg");
    public static string? FfprobePath => _ffprobe ??= Find("ffprobe.exe", "ffprobe");

    public static bool IsAvailable => FfmpegPath is not null && FfprobePath is not null;

    /// <summary>Version banner of the detected ffmpeg, e.g. "ffmpeg version 9.0.1".</summary>
    public static string VersionText { get; private set; } = "not found";

    /// <summary>Force a specific ffmpeg binary (also probes next to it for ffprobe).</summary>
    public static void Override(string ffmpegExe)
    {
        _ffmpeg = ffmpegExe;
        var dir = Path.GetDirectoryName(ffmpegExe);
        if (dir is not null)
        {
            var probe = Path.Combine(dir, "ffprobe.exe");
            if (!File.Exists(probe)) probe = Path.Combine(dir, "ffprobe");
            if (File.Exists(probe)) _ffprobe = probe;
        }
    }

    public static void RefreshVersion()
    {
        if (FfmpegPath is null) { VersionText = "not found"; return; }
        try
        {
            var first = ProcessRunner.RunCapture(FfmpegPath, "-version", TimeSpan.FromSeconds(10))
                                     .Split('\n').FirstOrDefault()?.Trim();
            VersionText = string.IsNullOrWhiteSpace(first) ? "unknown" : first;
        }
        catch { VersionText = "unknown"; }
    }

    private static string? Find(string windowsName, string unixName)
    {
        foreach (var candidate in EnumerateCandidates(windowsName, unixName))
        {
            try { if (File.Exists(candidate)) return Path.GetFullPath(candidate); }
            catch { /* ignore malformed paths */ }
        }
        return null;
    }

    private static IEnumerable<string> EnumerateCandidates(string windowsName, string unixName)
    {
        // 1. environment override
        var home = Environment.GetEnvironmentVariable("FFMPEG_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, windowsName);
            yield return Path.Combine(home, "bin", windowsName);
            yield return Path.Combine(home, unixName);
        }

        var exeDir = AppContext.BaseDirectory;

        // 2. tools folder beside the app, and beside up to 4 parent folders
        var dir = new DirectoryInfo(exeDir);
        for (int depth = 0; depth < 9 && dir is not null; depth++, dir = dir.Parent)
        {
            yield return Path.Combine(dir.FullName, "tools", "ffmpeg", "bin", windowsName);
            yield return Path.Combine(dir.FullName, "tools", "ffmpeg", windowsName);
            yield return Path.Combine(dir.FullName, "tools", windowsName);
            yield return Path.Combine(dir.FullName, "ffmpeg", "bin", windowsName);
            yield return Path.Combine(dir.FullName, windowsName);
        }

        // 3. PATH
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var p in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return Path.Combine(p.Trim(), windowsName);
            yield return Path.Combine(p.Trim(), unixName);
        }

        // 4. common install locations
        yield return @"C:\ffmpeg\bin\" + windowsName;
        yield return @"C:\Program Files\ffmpeg\bin\" + windowsName;
        yield return @"C:\ProgramData\chocolatey\bin\" + windowsName;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "Microsoft", "WinGet", "Links", windowsName);
    }
}
