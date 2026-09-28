using System.IO;
using System.Text.Json;
using CmdManager.Core.Http;

namespace CmdManager.Client.Services;

/// <summary>Previous values replaced by the .csx integration, restored by "Remove integration".</summary>
public sealed class CsxIntegrationBackup
{
    public bool Captured { get; set; }
    /// <summary>User-level PATHEXT before we changed it (null = there was none).</summary>
    public string? UserPathExt { get; set; }
    /// <summary>Default value of HKCU\Software\Classes\.csx before we changed it (null = key/value absent).</summary>
    public string? CsxDefault { get; set; }
    /// <summary>HKCU\Software\Classes\.csx existed before we touched it.</summary>
    public bool CsxKeyExisted { get; set; }
}

/// <summary>%LOCALAPPDATA%\CmdManager\settings.json</summary>
public sealed class ClientSettings
{
    /// <summary>API base URL.</summary>
    public string ServerUrl { get; set; } = CmdManagerApiClient.DefaultServerUrl;
    public string? LastUserName { get; set; }

    /// <summary>The command folder (synced from the DB, on the user PATH, CMDS points here).</summary>
    public string CommandFolder { get; set; } = AppPaths.DefaultCommandFolder;

    /// <summary>Add the command folder to the user PATH and set CMDS (turned off by "Remove from PATH").</summary>
    public bool ManagePath { get; set; } = true;

    /// <summary>Make .csx directly executable (per-user PATHEXT + HKCU association); turned off by "Remove integration".</summary>
    public bool ManageCsxIntegration { get; set; } = true;
    public CsxIntegrationBackup CsxBackup { get; set; } = new();

    /// <summary>Runner for .csx scriptlets: "dotnet script" (default) or legacy "scriptcs".</summary>
    public string CsxRunner { get; set; } = "dotnet script";

    /// <summary>Also create name.cmd wrappers for new .csx scriptlets. null = automatic (only when the .csx association is not working).</summary>
    public bool? GenerateCmdWrappers { get; set; }

    public string ExternalEditor { get; set; } = "notepad.exe";

    /// <summary>Background DB → folder sync interval while the app runs; 0 = off.</summary>
    public int BackgroundSyncMinutes { get; set; } = 5;

    public bool KeepRunWindowOpen { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static ClientSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var settings = JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new ClientSettings();
                settings.ServerUrl = CmdManagerApiClient.UpgradeServerUrl(settings.ServerUrl); // old root URL → /api/
                return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            AppPaths.Log("settings.json unreadable, using defaults: " + ex.Message);
        }
        return new ClientSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        var tmp = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
    }
}
