using System.IO;

namespace CmdManager.Client.Services;

/// <summary>
/// Everything the app keeps on disk lives under %LOCALAPPDATA%\CmdManager — deliberately NOT in the ClickOnce install
/// folder, which is a hashed per-version directory under %LOCALAPPDATA%\Apps\2.0 that changes on every update (a PATH
/// entry pointing there would break after the next update).
/// </summary>
public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CmdManager");

    public static string DefaultCommandFolder => Path.Combine(DataDir, "cmds");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string SessionFile => Path.Combine(DataDir, "session.bin");
    public static string SyncStateFile => Path.Combine(DataDir, "sync-state.json");
    public static string EditDir => Path.Combine(DataDir, "edit");
    public static string LogFile => Path.Combine(DataDir, "cmdmanager.log");

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
