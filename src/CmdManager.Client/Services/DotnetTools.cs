using System.Diagnostics;
using System.IO;

namespace CmdManager.Client.Services;

/// <summary>Finds dotnet.exe / scriptcs.exe and manages the dotnet-script global tool.</summary>
public static class DotnetTools
{
    /// <summary>Full path of an executable on PATH (user + machine, with PATHEXT) or null.</summary>
    public static string? FindOnPath(string exe)
    {
        var names = Path.HasExtension(exe) ? [exe] : new[] { exe + ".exe", exe + ".cmd", exe + ".bat" };
        var path = string.Join(';',
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim('"')), name);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (ArgumentException)
                {
                }
            }
        }
        return null;
    }

    public static string? FindDotnet()
    {
        var onPath = FindOnPath("dotnet.exe");
        if (onPath is not null)
            return onPath;
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var candidate = Path.Combine(Environment.GetFolderPath(root), "dotnet", "dotnet.exe");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>Runs a process hidden and returns (exit code, combined output).</summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(string fileName, string arguments, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (-1, "Timed out.");
        }
        return (p.ExitCode, (await stdout) + (await stderr));
    }

    /// <summary>True/false when known; null when dotnet itself is missing.</summary>
    public static async Task<bool?> IsDotnetScriptInstalledAsync()
    {
        var dotnet = FindDotnet();
        if (dotnet is null)
            return null;
        try
        {
            var (code, output) = await RunAsync(dotnet, "tool list -g", TimeSpan.FromSeconds(30));
            return code == 0 && output.Split('\n').Any(l => l.TrimStart().StartsWith("dotnet-script", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public static async Task<(bool Ok, string Output)> InstallDotnetScriptAsync()
    {
        var dotnet = FindDotnet() ?? throw new InvalidOperationException("dotnet.exe was not found. Install the .NET SDK first (dotnet-script needs the SDK, not just the runtime).");
        var (code, output) = await RunAsync(dotnet, "tool install -g dotnet-script", TimeSpan.FromMinutes(5));
        return (code == 0, output);
    }
}
