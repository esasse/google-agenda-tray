using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AgendaTray;

/// <summary>Where the app keeps its state: %LOCALAPPDATA%\google-agenda-tray.</summary>
internal static class AppPaths
{
    /// <summary>GOOGLE_AGENDA_TRAY_HOME points the app at another folder: a throwaway
    /// one for trying things, so test data never lands next to the real accounts. Made
    /// absolute, so a relative value does not follow the folder the app started in.</summary>
    public static readonly string Root =
        Environment.GetEnvironmentVariable("GOOGLE_AGENDA_TRAY_HOME") is { Length: > 0 } home
            ? Path.GetFullPath(home)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "google-agenda-tray");

    /// <summary>Names the single-instance mutex and the tray window class. A copy
    /// running on another home is a separate app: with the same names, launching it
    /// next to the real one would only open the real one's popup.</summary>
    public static readonly string InstanceName =
        Environment.GetEnvironmentVariable("GOOGLE_AGENDA_TRAY_HOME") is { Length: > 0 }
            ? "GoogleAgendaTray-" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Root.ToLowerInvariant())))[..12]
            : "GoogleAgendaTray";

    public static string ClientFile => Path.Combine(Root, "client.json");
    public static string AccountsDir => Path.Combine(Root, "accounts");
    public static string CacheDir => Path.Combine(Root, "cache");
    public static string ConfigFile => Path.Combine(Root, "config.json");
    public static string LogFile => Path.Combine(Root, "agenda.log");

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(AccountsDir);
        Directory.CreateDirectory(CacheDir);
    }
}

internal static partial class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Root);
                var file = new FileInfo(AppPaths.LogFile);
                if (file.Exists && file.Length > 256 * 1024) file.Delete();
                File.AppendAllText(AppPaths.LogFile,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {OneLine(message)}\r\n");
            }
        }
        catch
        {
            // a log that throws would take down whatever was trying to report a problem
        }
    }

    /// <summary>Collapses whitespace: Google's error bodies are multi-line JSON.</summary>
    public static string OneLine(string? text) => Whitespace().Replace(text ?? "", " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

internal static class Shell
{
    public static void Open(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"could not open {target}: {ex.Message}");
        }
    }

    public static void Edit(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"could not open {path} in Notepad: {ex.Message}");
        }
    }
}
