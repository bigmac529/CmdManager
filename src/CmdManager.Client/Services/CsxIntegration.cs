using CmdManager.Core;
using Microsoft.Win32;

namespace CmdManager.Client.Services;

public sealed record CsxIntegrationStatus(bool PathExtOk, bool AssociationOk, string? AssociationProgId, string? OpenCommand, string? UserChoiceProgId)
{
    /// <summary>Typing "name" at the prompt runs name.csx.</summary>
    public bool IsWorking => PathExtOk && AssociationOk && (UserChoiceProgId is null || UserChoiceProgId == CsxIntegration.ProgId);

    public string Summary => IsWorking
        ? "yes"
        : !PathExtOk ? "no (.CSX missing from PATHEXT)"
        : !AssociationOk ? "no (no .csx open command)"
        : $"no (Explorer 'Open with' choice '{UserChoiceProgId}' overrides it)";
}

/// <summary>
/// Per-user (no admin) integration that makes "name" run name.csx from cmd:
/// (1) user PATHEXT = machine PATHEXT + ".CSX" (a user PATHEXT replaces the machine one, see <see cref="PathExt"/>);
/// (2) HKCU\Software\Classes\.csx → CmdManager.csx, shell\open\command = "dotnet.exe" script "%1" -- %*.
/// Previous values are backed up in settings.json so <see cref="Remove"/> can restore them.
/// No machine-wide (elevated) variant is provided.
/// </summary>
public static class CsxIntegration
{
    public const string ProgId = "CmdManager.csx";
    private const string ClassesPath = @"Software\Classes";
    private const string MachineEnvPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    private const string UserChoicePath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.csx\UserChoice";

    public static string? MachinePathExt()
    {
        using var key = Registry.LocalMachine.OpenSubKey(MachineEnvPath, writable: false);
        return key?.GetValue("PATHEXT", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public static string? UserPathExt() => UserEnvironment.ReadRaw("PATHEXT").Value;

    public static CsxIntegrationStatus GetStatus()
    {
        var pathExtOk = PathExt.IsEffective(MachinePathExt(), UserPathExt(), PathExt.Csx);

        // HKEY_CLASSES_ROOT is the merged HKCU+HKLM view, i.e. what ShellExecute sees.
        string? progId, command = null;
        using (var ext = Registry.ClassesRoot.OpenSubKey(".csx"))
            progId = ext?.GetValue(null) as string;
        if (!string.IsNullOrEmpty(progId))
        {
            using var cmd = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            command = cmd?.GetValue(null) as string;
        }

        string? userChoice;
        using (var uc = Registry.CurrentUser.OpenSubKey(UserChoicePath))
            userChoice = uc?.GetValue("ProgId") as string;

        return new CsxIntegrationStatus(pathExtOk, !string.IsNullOrWhiteSpace(command), progId, command, userChoice);
    }

    /// <summary>Command line for the association, e.g. "C:\Program Files\dotnet\dotnet.exe" script "%1" -- %*.</summary>
    public static string BuildOpenCommand(string runner)
    {
        runner = string.IsNullOrWhiteSpace(runner) ? CommandTemplates.DefaultCsxRunner : runner.Trim();
        var parts = runner.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var exe = parts[0].Trim('"');
        var rest = parts.Length > 1 ? " " + parts[1] : "";
        string? full = exe.Equals("dotnet", StringComparison.OrdinalIgnoreCase) || exe.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
            ? DotnetTools.FindDotnet()
            : System.IO.Path.IsPathRooted(exe) ? exe : DotnetTools.FindOnPath(exe);
        if (full is null)
            throw new InvalidOperationException($"'{exe}' was not found on PATH. Install it first (see Settings).");
        return $"\"{full}\"{rest} \"%1\" -- %*";
    }

    /// <summary>Creates/repairs the integration. Returns log lines describing what changed.</summary>
    public static IReadOnlyList<string> Repair(ClientSettings settings)
    {
        var log = new List<string>();
        var command = BuildOpenCommand(settings.CsxRunner);
        using var classes = Registry.CurrentUser.CreateSubKey(ClassesPath, writable: true);

        // Back up previous values once (the first time we touch anything).
        if (!settings.CsxBackup.Captured)
        {
            using var existing = classes.OpenSubKey(".csx");
            settings.CsxBackup = new CsxIntegrationBackup
            {
                Captured = true,
                UserPathExt = UserPathExt(),
                CsxKeyExisted = existing is not null,
                CsxDefault = existing?.GetValue(null) as string
            };
            settings.Save();
        }

        // (1) PATHEXT
        var newPathExt = PathExt.WithExtension(MachinePathExt(), UserPathExt(), PathExt.Csx);
        if (newPathExt is not null)
        {
            UserEnvironment.WriteUserVariable("PATHEXT", newPathExt); // REG_SZ via SetEnvironmentVariable(User) → broadcasts
            Environment.SetEnvironmentVariable("PATHEXT", newPathExt, EnvironmentVariableTarget.Process);
            log.Add($"User PATHEXT set to {newPathExt}");
        }

        // (2) association in HKCU\Software\Classes
        using (var ext = classes.CreateSubKey(".csx", writable: true))
        {
            var current = ext.GetValue(null) as string;
            if (!string.IsNullOrEmpty(current) && current != ProgId)
                log.Add($"Replaced existing per-user .csx association '{current}' (backed up; 'Remove integration' restores it).");
            if (current != ProgId)
                ext.SetValue(null, ProgId);
        }

        using (var prog = classes.CreateSubKey(ProgId, writable: true))
        {
            prog.SetValue(null, "C# script (CmdManager)");
            using (var icon = prog.CreateSubKey("DefaultIcon", writable: true))
            {
                var exe = command.Split('"', StringSplitOptions.RemoveEmptyEntries)[0];
                icon.SetValue(null, $"\"{exe}\",0");
            }
            using var cmd = prog.CreateSubKey(@"shell\open\command", writable: true);
            if (cmd.GetValue(null) as string != command)
            {
                cmd.SetValue(null, command);
                log.Add($".csx open command: {command}");
            }
        }

        NativeMethods.NotifyAssociationsChanged();
        settings.ManageCsxIntegration = true;
        settings.Save();
        foreach (var line in log)
            AppPaths.Log(line);
        return log;
    }

    /// <summary>Removes the integration and restores the backed-up PATHEXT / .csx association.</summary>
    public static IReadOnlyList<string> Remove(ClientSettings settings)
    {
        var log = new List<string>();
        var backup = settings.CsxBackup;

        // PATHEXT
        var currentUser = UserPathExt();
        string? restored = backup.Captured && backup.UserPathExt is not null
            ? backup.UserPathExt
            : PathExt.WithoutExtension(MachinePathExt(), currentUser, PathExt.Csx);
        if (!string.Equals(restored, currentUser, StringComparison.Ordinal))
        {
            UserEnvironment.WriteUserVariable("PATHEXT", restored);
            Environment.SetEnvironmentVariable("PATHEXT", PathExt.Effective(MachinePathExt(), restored), EnvironmentVariableTarget.Process);
            log.Add(restored is null ? "User PATHEXT removed (machine value applies)." : $"User PATHEXT restored to {restored}");
        }

        using var classes = Registry.CurrentUser.CreateSubKey(ClassesPath, writable: true);
        using (var ext = classes.OpenSubKey(".csx", writable: true))
        {
            if (ext is not null && ext.GetValue(null) as string == ProgId)
            {
                if (backup.Captured && backup.CsxDefault is not null)
                {
                    ext.SetValue(null, backup.CsxDefault);
                    log.Add($".csx association restored to '{backup.CsxDefault}'.");
                }
                else
                {
                    ext.DeleteValue("", throwOnMissingValue: false);
                    log.Add(".csx association removed.");
                }
            }
        }
        if (!(backup.Captured && backup.CsxKeyExisted))
        {
            using var ext = classes.OpenSubKey(".csx");
            var empty = ext is not null && ext.SubKeyCount == 0 && ext.ValueCount == 0;
            ext?.Dispose();
            if (empty)
                classes.DeleteSubKey(".csx", throwOnMissingSubKey: false);
        }
        classes.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);

        NativeMethods.NotifyAssociationsChanged();
        settings.ManageCsxIntegration = false;
        settings.CsxBackup = new CsxIntegrationBackup();
        settings.Save();
        foreach (var line in log)
            AppPaths.Log(line);
        return log;
    }
}
