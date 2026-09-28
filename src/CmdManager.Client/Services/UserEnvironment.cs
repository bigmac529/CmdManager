using CmdManager.Core;
using Microsoft.Win32;

namespace CmdManager.Client.Services;

/// <summary>
/// Per-user environment (HKCU\Environment): adds/removes the command folder on the user Path and sets CMDS.
/// No admin rights needed. The string logic lives in <see cref="PathList"/> (unit-tested in Core).
/// </summary>
public static class UserEnvironment
{
    private const string EnvKey = "Environment";

    /// <summary>Raw (unexpanded) user variable and its registry kind.</summary>
    public static (string? Value, RegistryValueKind Kind) ReadRaw(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(EnvKey, writable: false);
        if (key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string value)
            return (null, RegistryValueKind.ExpandString);
        return (value, key.GetValueKind(name));
    }

    public static bool IsOnUserPath(string folder) => PathList.Contains(ReadRaw("Path").Value, folder, Environment.ExpandEnvironmentVariables);

    /// <summary>
    /// Writes a user variable. Plain values go through Environment.SetEnvironmentVariable(User), which also broadcasts
    /// WM_SETTINGCHANGE. Values that are REG_EXPAND_SZ (the usual kind for Path, e.g. "%USERPROFILE%\bin") are written
    /// with the registry API so they stay expandable — SetEnvironmentVariable would store them as REG_SZ — and then
    /// broadcast explicitly.
    /// </summary>
    public static void WriteUserVariable(string name, string? value, RegistryValueKind kind = RegistryValueKind.String)
    {
        if (value is null || kind != RegistryValueKind.ExpandString)
        {
            Environment.SetEnvironmentVariable(name, string.IsNullOrEmpty(value) ? null : value, EnvironmentVariableTarget.User);
            return;
        }

        using (var key = Registry.CurrentUser.CreateSubKey(EnvKey, writable: true))
            key.SetValue(name, value, RegistryValueKind.ExpandString);
        NativeMethods.BroadcastEnvironmentChange();
    }

    /// <summary>Adds <paramref name="folder"/> to the user Path (only if missing) and removes <paramref name="previousFolder"/>. True if Path changed.</summary>
    public static bool EnsureOnPath(string folder, string? previousFolder = null)
    {
        var (raw, kind) = ReadRaw("Path");
        var updated = PathList.Move(raw, previousFolder, folder, Environment.ExpandEnvironmentVariables);
        if (string.Equals(updated, raw ?? "", StringComparison.Ordinal))
            return false;
        WriteUserVariable("Path", updated, kind == RegistryValueKind.ExpandString || updated.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String);
        AppendToProcessPath(folder);
        AppPaths.Log($"User Path: added {folder}" + (previousFolder is null ? "" : $", removed {previousFolder}"));
        return true;
    }

    public static bool RemoveFromPath(string folder)
    {
        var (raw, kind) = ReadRaw("Path");
        if (!PathList.Contains(raw, folder, Environment.ExpandEnvironmentVariables))
            return false;
        var updated = PathList.Remove(raw, folder, Environment.ExpandEnvironmentVariables);
        WriteUserVariable("Path", updated.Length == 0 ? null : updated, kind);
        AppPaths.Log($"User Path: removed {folder}");
        return true;
    }

    public static string? GetCmds() => ReadRaw("CMDS").Value;

    public static void SetCmds(string? folder)
    {
        if (string.Equals(GetCmds(), folder, StringComparison.OrdinalIgnoreCase))
            return;
        WriteUserVariable("CMDS", folder);
        Environment.SetEnvironmentVariable("CMDS", folder, EnvironmentVariableTarget.Process);
    }

    private static void AppendToProcessPath(string folder)
    {
        var current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("PATH", PathList.Add(current, folder), EnvironmentVariableTarget.Process);
    }
}
