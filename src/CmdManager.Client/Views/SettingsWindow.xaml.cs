using System.IO;
using System.Windows;
using CmdManager.Client.Services;
using Microsoft.Win32;

namespace CmdManager.Client.Views;

public partial class SettingsWindow : Window
{
    private static ClientSettings Settings => App.Settings;

    public SettingsWindow()
    {
        InitializeComponent();
        FolderBox.Text = Settings.CommandFolder;
        RunnerBox.Text = Settings.CsxRunner;
        WrapperBox.IsChecked = Settings.GenerateCmdWrappers;
        EditorBox.Text = Settings.ExternalEditor;
        SyncMinutesBox.Text = Settings.BackgroundSyncMinutes.ToString();
        KeepOpenBox.IsChecked = Settings.KeepRunWindowOpen;
        ServerText.Text = $"Server: {Settings.ServerUrl}  (change it on the login window)  ·  Data: {AppPaths.DataDir}";
        RefreshStatus();
        Loaded += async (_, _) => await CheckDotnetScriptAsync();
    }

    private void RefreshStatus()
    {
        var onPath = UserEnvironment.IsOnUserPath(Settings.CommandFolder);
        PathStatus.Text = onPath ? "On your user PATH: yes" : "On your user PATH: no" + (Settings.ManagePath ? "" : " (PATH management turned off)");
        var cmds = UserEnvironment.GetCmds();
        CmdsStatus.Text = $"CMDS = {cmds ?? "(not set)"}";

        var status = CsxIntegration.GetStatus();
        CsxStatusText.Text = ".csx executable from command line: " + status.Summary;
        CsxDetailText.Text = $"PATHEXT has .CSX: {(status.PathExtOk ? "yes" : "no")}\n"
                             + $".csx → {status.AssociationProgId ?? "(none)"}\n"
                             + $"open: {status.OpenCommand ?? "(none)"}"
                             + (status.UserChoiceProgId is null ? "" : $"\nExplorer 'Open with' choice: {status.UserChoiceProgId}");
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Command folder", InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : AppPaths.DataDir };
        if (d.ShowDialog(this) == true)
            FolderBox.Text = d.FolderName;
    }

    private void ApplyFolder_Click(object sender, RoutedEventArgs e)
    {
        string newFolder;
        try
        {
            newFolder = Path.GetFullPath(FolderBox.Text.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            StatusText.Text = ex.Message;
            return;
        }

        var old = Settings.CommandFolder;
        if (string.Equals(old.TrimEnd('\\'), newFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            Directory.CreateDirectory(newFolder);
            Settings.CommandFolder = newFolder;
            Settings.ManagePath = true;
            Settings.Save();
            UserEnvironment.EnsureOnPath(newFolder, old);
            UserEnvironment.SetCmds(newFolder);
            StatusText.Text = $"Command folder moved to {newFolder}. The library is synced into it when you close Settings. The old folder {old} was left in place.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = ex.Message;
        }
        RefreshStatus();
    }

    private void AddToPath_Click(object sender, RoutedEventArgs e)
    {
        Settings.ManagePath = true;
        Settings.Save();
        UserEnvironment.EnsureOnPath(Settings.CommandFolder);
        UserEnvironment.SetCmds(Settings.CommandFolder);
        StatusText.Text = "Added. Open a new cmd window to pick up the change.";
        RefreshStatus();
    }

    private void RemoveFromPath_Click(object sender, RoutedEventArgs e)
    {
        Settings.ManagePath = false; // don't re-add on next start
        Settings.Save();
        UserEnvironment.RemoveFromPath(Settings.CommandFolder);
        if (string.Equals(UserEnvironment.GetCmds(), Settings.CommandFolder, StringComparison.OrdinalIgnoreCase))
            UserEnvironment.SetCmds(null);
        StatusText.Text = "Removed from PATH (and CMDS cleared). It will not be re-added until you press 'Add to PATH'.";
        RefreshStatus();
    }

    private void RepairCsx_Click(object sender, RoutedEventArgs e)
    {
        SaveRunner();
        try
        {
            var log = CsxIntegration.Repair(Settings);
            StatusText.Text = log.Count == 0 ? "Already set up." : string.Join("\n", log);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            StatusText.Text = ex.Message;
        }
        RefreshStatus();
    }

    private void RemoveCsx_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var log = CsxIntegration.Remove(Settings);
            StatusText.Text = log.Count == 0 ? "Nothing to remove." : string.Join("\n", log);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            StatusText.Text = ex.Message;
        }
        RefreshStatus();
    }

    private async Task CheckDotnetScriptAsync()
    {
        DotnetScriptStatus.Text = "Checking for dotnet-script...";
        var installed = await DotnetTools.IsDotnetScriptInstalledAsync();
        DotnetScriptStatus.Text = installed switch
        {
            true => "dotnet-script: installed",
            false => "dotnet-script: not installed",
            null => "dotnet-script: the .NET SDK (dotnet.exe) was not found"
        };
        InstallButton.Visibility = installed == false ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        DotnetScriptStatus.Text = "Installing dotnet-script (dotnet tool install -g dotnet-script)...";
        try
        {
            var (ok, output) = await DotnetTools.InstallDotnetScriptAsync();
            StatusText.Text = ok ? "dotnet-script installed. Open a new cmd window so %USERPROFILE%\\.dotnet\\tools is on PATH." : output;
        }
        catch (InvalidOperationException ex)
        {
            StatusText.Text = ex.Message;
        }
        InstallButton.IsEnabled = true;
        await CheckDotnetScriptAsync();
    }

    private void BrowseEditor_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*", Title = "External editor" };
        if (d.ShowDialog(this) == true)
            EditorBox.Text = d.FileName;
    }

    private void SaveRunner()
    {
        var runner = string.IsNullOrWhiteSpace(RunnerBox.Text) ? "dotnet script" : RunnerBox.Text.Trim();
        var changed = !string.Equals(runner, Settings.CsxRunner, StringComparison.Ordinal);
        Settings.CsxRunner = runner;
        Settings.Save();
        if (changed && Settings.ManageCsxIntegration)
        {
            try
            {
                CsxIntegration.Repair(Settings); // point the association at the new runner
            }
            catch (InvalidOperationException ex)
            {
                StatusText.Text = ex.Message;
            }
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveRunner();
        Settings.GenerateCmdWrappers = WrapperBox.IsChecked;
        Settings.ExternalEditor = string.IsNullOrWhiteSpace(EditorBox.Text) ? "notepad.exe" : EditorBox.Text.Trim();
        Settings.BackgroundSyncMinutes = int.TryParse(SyncMinutesBox.Text, out var m) && m >= 0 ? Math.Min(m, 1440) : 5;
        Settings.KeepRunWindowOpen = KeepOpenBox.IsChecked == true;
        Settings.Save();
        DialogResult = true;
    }
}
