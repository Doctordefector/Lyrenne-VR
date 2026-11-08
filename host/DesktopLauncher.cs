using System.Diagnostics;

namespace LyrenneVR;

/// <summary>
/// Starts Lyrenne desktop when Lyrenne VR is set to follow only Lyrenne, so one launch brings up both.
/// Uses the saved <see cref="Settings.LyrennePath"/> first. It is filled once, from the first copy
/// seen running, and never overwritten after, so a dev build run later cannot take it over. Without
/// it: the Start Menu shortcut (which every Lyrenne launch repoints at itself), then Scoop.
/// </summary>
static class DesktopLauncher
{
    static int _busy;

    public static void StartIfWanted(Settings settings)
    {
        if (!settings.LyrenneOnly || !settings.LaunchLyrenne) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                if (RunningPath() is { } running)
                {
                    if (string.IsNullOrEmpty(settings.LyrennePath) && running.Length > 0) { settings.LyrennePath = running; settings.Save(); }
                    return;
                }
                string? exe = Find(settings);
                if (exe == null)
                {
                    Log.Write("Lyrenne desktop not found: no Start Menu shortcut, no remembered path, no Scoop install");
                    return;
                }
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
                Log.Write($"started Lyrenne desktop: {exe}");
            }
            catch (Exception e)
            {
                Log.Write($"could not start Lyrenne desktop: {e.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        });
    }

    /// <summary>The exe of a running Lyrenne desktop, or null. Its launcher and the JVM it starts are both Lyrenne.exe.</summary>
    static string? RunningPath()
    {
        foreach (var p in Process.GetProcessesByName("Lyrenne"))
        {
            using (p)
            {
                try { return p.MainModule?.FileName ?? ""; }
                catch { return ""; } // running, path unreadable (elevated): still running
            }
        }
        return null;
    }

    static string? Find(Settings settings)
    {
        if (!string.IsNullOrEmpty(settings.LyrennePath) && File.Exists(settings.LyrennePath)) return settings.LyrennePath;
        string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Lyrenne.lnk");
        if (File.Exists(lnk) && ShortcutTarget(lnk) is { } target && File.Exists(target)) return target;
        string scoop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"scoop\apps\lyrenne\current\Lyrenne.exe");
        return File.Exists(scoop) ? scoop : null;
    }

    static string? ShortcutTarget(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            try { return (string)shell.CreateShortcut(lnk).TargetPath; }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }
        catch
        {
            return null;
        }
    }
}
