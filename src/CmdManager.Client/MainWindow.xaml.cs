using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using CmdManager.Client.Services;
using CmdManager.Client.Views;
using CmdManager.Core;
using CmdManager.Core.Contracts;
using CmdManager.Core.Http;
using CmdManager.Core.Sync;
using Microsoft.Win32;

namespace CmdManager.Client;

/// <summary>
/// Library list + preview, command actions, and the two sync directions:
/// DB → folder ("Sync now", on start, every few minutes, after each save) and folder → DB ("Sync local changes to DB").
/// A FileSystemWatcher on the command folder only counts pending local changes; it never uploads by itself.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<LibraryItem> _items = [];
    private readonly ICollectionView _view;
    private readonly DispatcherTimer _syncTimer = new();
    private readonly DispatcherTimer _watchDebounce = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private FileSystemWatcher? _watcher;
    private bool _busy;
    private bool _closingForLogin;

    public MainWindow()
    {
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = o => o is LibraryItem item && FilterItem(item);
        ItemsList.ItemsSource = _view;
        _syncTimer.Tick += async (_, _) => await PullAsync(background: true);
        _watchDebounce.Tick += (_, _) =>
        {
            _watchDebounce.Stop();
            UpdatePendingCount();
        };
        Loaded += async (_, _) => await StartupAsync();
        Closing += MainWindow_Closing;
    }

    public bool IsSwitchingToLogin => _closingForLogin;

    private static ClientSettings Settings => App.Settings;
    private static string Folder => Settings.CommandFolder;
    private LibraryItem? Selected => ItemsList.SelectedItem as LibraryItem;

    // ------------------------------------------------------------ lifecycle

    private async Task StartupAsync()
    {
        UserText.Text = App.Session.Current?.User.UserName is { } u ? $"Signed in as {u}" : "";
        EnsureEnvironment();                 // first-run "installation": folder, PATH, CMDS, .csx integration
        await RefreshListAsync();
        await PullAsync(background: false);  // startup sync (DB → folder)
        StartWatcher();
        ConfigureTimer();
    }

    /// <summary>Stops background work and closes without shutting the app down (logout / expired session).</summary>
    public void CloseForLogin()
    {
        _closingForLogin = true;
        StopBackgroundWork();
        foreach (var w in OwnedWindows.OfType<Window>().ToList())
            w.Close();
        Close();
    }

    private void StopBackgroundWork()
    {
        _syncTimer.Stop();
        _watchDebounce.Stop();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e) => StopBackgroundWork();

    private void ConfigureTimer()
    {
        _syncTimer.Stop();
        if (Settings.BackgroundSyncMinutes > 0)
        {
            _syncTimer.Interval = TimeSpan.FromMinutes(Settings.BackgroundSyncMinutes);
            _syncTimer.Start();
        }
    }

    // ------------------------------------------------------------ environment (PATH, CMDS, .csx)

    private void EnsureEnvironment()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            if (Settings.ManagePath)
            {
                UserEnvironment.EnsureOnPath(Folder);
                UserEnvironment.SetCmds(Folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppPaths.Log("Environment setup failed: " + ex.Message);
            SyncStatus.Text = "Could not update PATH: " + ex.Message;
        }

        if (Settings.ManageCsxIntegration && !CsxIntegration.GetStatus().IsWorking)
        {
            try
            {
                CsxIntegration.Repair(Settings);
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                AppPaths.Log(".csx integration not set up: " + ex.Message);
            }
        }

        UpdateEnvironmentStatus();
    }

    public void UpdateEnvironmentStatus()
    {
        var onPath = UserEnvironment.IsOnUserPath(Folder);
        FolderStatus.Text = $"Command folder: {Folder}" + (onPath ? " (on PATH)" : " (not on PATH)");
        FolderStatus.Foreground = onPath ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.DarkRed;
        CsxStatus.Text = ".csx executable: " + CsxIntegration.GetStatus().Summary;
    }

    // ------------------------------------------------------------ list

    private async Task RefreshListAsync()
    {
        try
        {
            var commandsTask = App.Api.ListCommandsAsync();
            var assetsTask = App.Api.ListAssetsAsync();
            var items = (await commandsTask).Select(LibraryItem.From).Concat((await assetsTask).Select(LibraryItem.From))
                .OrderBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
            var selectedPath = Selected?.RelativePath;
            _items.Clear();
            foreach (var i in items)
                _items.Add(i);
            if (selectedPath is not null)
                ItemsList.SelectedItem = _items.FirstOrDefault(i => i.RelativePath.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
            Title = $"CmdManager - {_items.Count(i => i.IsCommand)} commands, {_items.Count(i => !i.IsCommand)} assets";
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            SyncStatus.Text = "Could not load the library: " + ex.Message;
        }
    }

    private bool FilterItem(LibraryItem item)
    {
        var type = TypeFilter?.SelectedIndex ?? 0;
        if (type == 1 && !item.IsCommand || type == 2 && item.IsCommand)
            return false;
        var q = SearchBox?.Text?.Trim();
        return string.IsNullOrEmpty(q) || item.Matches(q);
    }

    private void Search_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => _view?.Refresh();
    private void Filter_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => _view?.Refresh();

    private async void ItemsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var item = Selected;
        Preview.Text = "";
        Preview.SyntaxHighlighting = null;
        if (item is null)
        {
            PreviewTitle.Text = PreviewInfo.Text = "";
            return;
        }

        PreviewTitle.Text = item.RelativePath;
        PreviewInfo.Text = $"{item.KindText} · {item.SizeText} · updated {item.UpdatedText}"
                           + (string.IsNullOrEmpty(item.Description) ? "" : $"\n{item.Description}")
                           + (item.Tags.Length == 0 ? "" : $"\nTags: {item.TagsText}");
        try
        {
            string? text = null;
            if (item.IsEditable)
                text = (await App.Api.GetCommandAsync(item.Id)).TextContent;
            else if (item.Size <= 256 * 1024)
            {
                var bytes = await App.Api.GetContentAsync(item.ToManifestEntry());
                if (TextContent.TryDecode(bytes, out var decoded))
                    text = decoded;
            }
            if (Selected != item)
                return;
            Preview.SyntaxHighlighting = SyntaxHighlighting.ForFile(item.Name);
            Preview.Text = text ?? $"(binary file, {item.SizeText})";
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Preview.Text = "Could not load: " + ex.Message;
        }
    }

    private void ItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Edit_Click(sender, e);

    private void ItemsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Edit_Click(sender, e);
        else if (e.Key == Key.Delete) Delete_Click(sender, e);
        else if (e.Key == Key.F2) Rename_Click(sender, e);
    }

    // ------------------------------------------------------------ command actions

    private void New_Executed(object sender, ExecutedRoutedEventArgs e) => New_Click(sender, e);

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NewCommandWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Created.Count == 0)
            return;
        foreach (var c in dialog.Created)
            await WriteLocalAsync(new ManifestEntry(LibraryItemType.Command, c.Id, c.RelativePath, c.Sha256, c.Size, c.UpdatedUtc));
        await RefreshListAsync();
        var first = dialog.Created.FirstOrDefault(c => c.Kind is CommandKind.Csx or CommandKind.Ps1) ?? dialog.Created[0];
        ItemsList.SelectedItem = _items.FirstOrDefault(i => i.IsCommand && i.Id == first.Id);
        OpenEditor(first.Id);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item is null)
            return;
        if (!item.IsEditable)
        {
            MessageBox.Show(this, $"{item.RelativePath} is a binary {(item.IsCommand ? "command" : "asset")} and cannot be edited as text.\n"
                + "Replace it by editing the file in the command folder and pressing 'Sync local changes to DB'.",
                "CmdManager", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        OpenEditor(item.Id);
    }

    private async void OpenEditor(int commandId)
    {
        try
        {
            var cmd = await App.Api.GetCommandAsync(commandId);
            var editor = new EditorWindow(cmd) { Owner = this };
            editor.Saved += async (_, saved) =>
            {
                await WriteLocalAsync(new ManifestEntry(LibraryItemType.Command, saved.Id, saved.RelativePath, saved.Sha256, saved.Size, saved.UpdatedUtc));
                await RefreshListAsync();
            };
            editor.Show();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            ShowError(ex);
        }
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item is null)
            return;
        var newPath = InputDialog.Ask(this, "Rename", "New path (folder/name.ext):", item.RelativePath);
        if (newPath is null || newPath.Trim() == item.RelativePath)
            return;
        if (!LibraryPath.TryParse(newPath, out var lp, out var error))
        {
            MessageBox.Show(this, error, "Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            ManifestEntry stored;
            if (item.IsCommand)
            {
                var c = await App.Api.GetCommandAsync(item.Id);
                // Content omitted = kept; kind is re-derived from the new extension.
                var updated = await App.Api.UpdateCommandAsync(item.Id, new CommandUpsertRequest(lp.Value, Description: c.Description, Tags: c.Tags, ExpectedSha256: c.Sha256));
                stored = new ManifestEntry(LibraryItemType.Command, updated.Id, updated.RelativePath, updated.Sha256, updated.Size, updated.UpdatedUtc);
            }
            else
            {
                var a = await App.Api.UpdateAssetAsync(item.Id, new AssetUpdateRequest(lp.Value, item.Description));
                stored = new ManifestEntry(LibraryItemType.Asset, a.Id, a.RelativePath, a.Sha256, a.Size, a.UpdatedUtc);
            }
            App.Sync.RemoveLocal(Folder, item.RelativePath);
            await WriteLocalAsync(stored);
            await RefreshListAsync();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            ShowError(ex);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item is null)
            return;
        if (MessageBox.Show(this, $"Delete {item.RelativePath} from your library?\nThe copy in the command folder is removed too.",
                "Delete", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;
        try
        {
            if (item.IsCommand)
                await App.Api.DeleteCommandAsync(item.Id, item.Sha256);
            else
                await App.Api.DeleteAssetAsync(item.Id, item.Sha256);
            App.Sync.RemoveLocal(Folder, item.RelativePath);
            await RefreshListAsync();
            UpdatePendingCount();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            ShowError(ex);
        }
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item is null)
            return;
        var args = InputDialog.Ask(this, "Run", $"Arguments for {item.Name}:");
        if (args is null)
            return;
        try
        {
            var path = await App.Sync.WriteLocalAsync(Folder, item.ToManifestEntry());
            CommandRunner.Run(path, args, Settings, Folder);
        }
        catch (Exception ex) when (IsExpected(ex) || ex is System.ComponentModel.Win32Exception)
        {
            ShowError(ex);
        }
    }

    // ------------------------------------------------------------ sync

    private void Refresh_Executed(object sender, ExecutedRoutedEventArgs e) => SyncNow_Click(sender, e);

    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        await PullAsync(background: false);
        await RefreshListAsync();
    }

    /// <summary>DB → folder: write new/changed files, delete files removed from the DB (only ones this app wrote).</summary>
    private async Task PullAsync(bool background)
    {
        if (_busy || !App.Session.IsLoggedIn)
            return;
        _busy = true;
        try
        {
            SyncStatus.Text = "Syncing...";
            var progress = new Progress<string>(s => SyncStatus.Text = s);
            var r = await App.Sync.PullAsync(Folder, progress);
            var parts = new List<string>();
            if (r.Downloaded > 0) parts.Add($"{r.Downloaded} updated");
            if (r.DeletedLocal > 0) parts.Add($"{r.DeletedLocal} removed");
            if (r.Errors.Count > 0) parts.Add($"{r.Errors.Count} error(s)");
            SyncStatus.Text = $"Synced {DateTime.Now:HH:mm}" + (parts.Count > 0 ? ": " + string.Join(", ", parts) : "");
            SetPending(r.PendingLocal);
            if (r.Errors.Count > 0)
            {
                AppPaths.Log("Sync errors:\n  " + string.Join("\n  ", r.Errors));
                if (!background)
                    ConfirmListDialog.Show(this, "Sync", "Some files could not be written (in use?):", r.Errors);
            }
            if (background && r.Downloaded + r.DeletedLocal > 0)
                await RefreshListAsync();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            SyncStatus.Text = $"Sync failed {DateTime.Now:HH:mm}: {ex.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Folder → DB, after confirmation; conflicts are resolved one by one.</summary>
    private async void Push_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        _busy = true;
        PushButton.IsEnabled = false;
        try
        {
            SyncStatus.Text = "Comparing the command folder with the library...";
            var plan = await App.Sync.PlanAsync(Folder);
            var uploads = plan.Uploads.ToList();
            var deletes = plan.RemoteDeletes.ToList();
            var conflicts = plan.Conflicts.ToList();
            if (uploads.Count + deletes.Count + conflicts.Count == 0)
            {
                SyncStatus.Text = "No local changes to upload.";
                SetPending([]);
                return;
            }

            if (uploads.Count + deletes.Count > 0)
            {
                var lines = uploads.Select(u => $"UPLOAD  {u.RelativePath}   ({u.Reason})")
                    .Concat(deletes.Select(d => $"DELETE  {d.RelativePath}   (deleted locally → delete from the DB)"));
                var header = $"Send these local changes to the database?\n{uploads.Count} upload(s), {deletes.Count} deletion(s)"
                             + (conflicts.Count > 0 ? $"; {conflicts.Count} conflict(s) will be asked about next." : ".");
                if (!ConfirmListDialog.Confirm(this, "Sync local changes to DB", header, lines, "Sync to DB"))
                {
                    SyncStatus.Text = "Cancelled.";
                    return;
                }
            }

            var resolutions = new Dictionary<string, ConflictResolution>(StringComparer.OrdinalIgnoreCase);
            ConflictResolution? forAll = null;
            for (var i = 0; i < conflicts.Count; i++)
            {
                if (forAll is { } all)
                {
                    resolutions[conflicts[i].RelativePath] = all == ConflictResolution.KeepBoth && !(conflicts[i].LocalExists && conflicts[i].ServerExists)
                        ? ConflictResolution.Skip : all;
                    continue;
                }
                var d = new ConflictDialog(conflicts[i], i + 1, conflicts.Count) { Owner = this };
                d.ShowDialog();
                resolutions[conflicts[i].RelativePath] = d.Resolution;
                if (d.ApplyToAll)
                    forAll = d.Resolution;
            }

            var progress = new Progress<string>(s => SyncStatus.Text = s);
            var result = await App.Sync.PushAsync(plan, resolutions, progress);
            SyncStatus.Text = $"Sent {DateTime.Now:HH:mm}: {result.Uploaded} uploaded, {result.DeletedRemote} deleted"
                              + (result.Downloaded > 0 ? $", {result.Downloaded} downloaded" : "");
            var problems = result.NewConflicts.Select(p => $"CHANGED ON SERVER  {p}   (not overwritten; press the button again)")
                .Concat(result.Errors).ToList();
            if (problems.Count > 0)
                ConfirmListDialog.Show(this, "Sync local changes to DB", "Some files were not sent:", problems);
            await RefreshListAsync();
            UpdatePendingCount();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            ShowError(ex);
        }
        finally
        {
            _busy = false;
            PushButton.IsEnabled = true;
        }
    }

    private async Task WriteLocalAsync(ManifestEntry entry)
    {
        try
        {
            await App.Sync.WriteLocalAsync(Folder, entry);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            SyncStatus.Text = $"Could not write {entry.RelativePath} locally: {ex.Message}";
        }
    }

    // ------------------------------------------------------------ folder watcher

    private void StartWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (!Directory.Exists(Folder))
            return;
        _watcher = new FileSystemWatcher(Folder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        FileSystemEventHandler onChange = (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _watchDebounce.Stop();
            _watchDebounce.Start();
        });
        _watcher.Changed += onChange;
        _watcher.Created += onChange;
        _watcher.Deleted += onChange;
        _watcher.Renamed += (s, e) => onChange(s, e);
        _watcher.EnableRaisingEvents = true;
    }

    private async void UpdatePendingCount()
    {
        try
        {
            var folder = Folder;
            var changes = await Task.Run(() => App.Sync.DetectLocalChanges(folder));
            SetPending(changes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppPaths.Log("Local change scan failed: " + ex.Message);
        }
    }

    private void SetPending(IReadOnlyCollection<SyncItem> pending)
    {
        PendingStatus.Text = pending.Count == 0 ? "" : $"{pending.Count} local change(s) pending";
        PendingStatus.ToolTip = pending.Count == 0 ? null
            : string.Join("\n", pending.Take(30).Select(p => $"{p.RelativePath}: {(p.IsLocalOnly ? "local only" : p.Reason)}"))
              + (pending.Count > 30 ? "\n..." : "") + "\n\nPress 'Sync local changes to DB' to send them.";
    }

    // ------------------------------------------------------------ import / settings / logout

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to import (e.g. your local clone of CMDs)" };
        if (dialog.ShowDialog(this) != true)
            return;
        var answer = MessageBox.Show(this,
            $"Import everything in\n{dialog.FolderName}\n(skipping bin, obj, .git, .vs and desktop.ini)?\n\n"
            + "Yes = overwrite library files that already exist with different content\nNo = keep existing library files",
            "Import folder", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Cancel)
            return;

        _busy = true;
        try
        {
            var progress = new Progress<string>(s => SyncStatus.Text = s);
            var importer = new FolderImporter(App.Api);
            var r = await importer.ImportAsync(dialog.FolderName, overwrite: answer == MessageBoxResult.Yes, progress);
            SyncStatus.Text = $"Imported: {r.Created} new, {r.Updated} updated, {r.Unchanged} unchanged, {r.Skipped} skipped, {r.Errors.Count} error(s)";
            if (r.Errors.Count > 0)
                ConfirmListDialog.Show(this, "Import folder", "Some files were not imported:", r.Errors.Select(x => $"{x.RelativePath}: {x.Error}"));
        }
        catch (Exception ex) when (IsExpected(ex) || ex is DirectoryNotFoundException)
        {
            ShowError(ex);
        }
        finally
        {
            _busy = false;
        }
        await PullAsync(background: false);
        await RefreshListAsync();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Folder);
        CommandRunner.OpenFolder(Folder);
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var oldFolder = Folder;
        new SettingsWindow { Owner = this }.ShowDialog();
        ConfigureTimer();
        UpdateEnvironmentStatus();
        if (!string.Equals(oldFolder, Folder, StringComparison.OrdinalIgnoreCase))
        {
            StartWatcher();
            await PullAsync(background: false);
        }
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        StopBackgroundWork();
        try
        {
            await App.Api.LogoutAsync();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            AppPaths.Log("Logout: " + ex.Message);
        }
        ((App)Application.Current).ReturnToLogin(null);
    }

    // ------------------------------------------------------------ helpers

    private static bool IsExpected(Exception ex) =>
        ex is ApiException or HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException;

    private void ShowError(Exception ex)
    {
        if (ex is ApiException { IsUnauthorized: true })
            return; // session expired: App returns to the Login window
        MessageBox.Show(this, ex.Message, "CmdManager", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
