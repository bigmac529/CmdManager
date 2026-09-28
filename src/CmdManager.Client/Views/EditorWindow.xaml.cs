using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using CmdManager.Client.Services;
using CmdManager.Core;
using CmdManager.Core.Contracts;
using CmdManager.Core.Http;

namespace CmdManager.Client.Views;

/// <summary>
/// AvalonEdit code editor for text commands (C# highlighting for .csx, Batch for .cmd/.bat, PowerShell for .ps1, ...).
/// Ctrl+S saves to the server (optimistic concurrency on the content hash) and the main window then writes the file to
/// the command folder so it is usable at the prompt immediately. F5 runs it with the arguments box.
/// </summary>
public partial class EditorWindow : Window
{
    private CommandDto _command;
    private bool _dirty;
    private bool _loading;
    private FileSystemWatcher? _externalWatcher;
    private string? _externalPath;

    public EditorWindow(CommandDto command)
    {
        InitializeComponent();
        _command = command;
        _loading = true;
        Editor.SyntaxHighlighting = SyntaxHighlighting.ForFile(command.Name);
        Editor.Text = command.TextContent ?? "";
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 4;
        Editor.Options.HighlightCurrentLine = true;
        DescriptionBox.Text = command.Description ?? "";
        TagsBox.Text = string.Join(", ", command.Tags);
        _loading = false;
        Editor.TextChanged += (_, _) => MarkDirty();
        DescriptionBox.TextChanged += (_, _) => MarkDirty();
        TagsBox.TextChanged += (_, _) => MarkDirty();
        Editor.TextArea.Caret.PositionChanged += (_, _) => CaretText.Text = $"Ln {Editor.TextArea.Caret.Line}, Col {Editor.TextArea.Caret.Column}";
        UpdateTitle();
        StatusText.Text = command.Kind == CommandKind.Csx ? $"C# scriptlet · runner: {App.Settings.CsxRunner}" : command.Kind.ToString();
        Loaded += (_, _) => Editor.Focus();
        Closing += EditorWindow_Closing;
    }

    /// <summary>Raised after a successful save with the stored command.</summary>
    public event EventHandler<CommandDto>? Saved;

    private void MarkDirty()
    {
        if (_loading || _dirty)
            return;
        _dirty = true;
        UpdateTitle();
    }

    private void UpdateTitle() => Title = $"{(_dirty ? "*" : "")}{_command.RelativePath} - CmdManager";

    private void Save_Executed(object sender, ExecutedRoutedEventArgs e) => Save_Click(sender, e);

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    private async Task<bool> SaveAsync(bool force = false)
    {
        SaveButton.IsEnabled = false;
        try
        {
            var tags = TagsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var request = new CommandUpsertRequest(_command.RelativePath, Editor.Text,
                Description: string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim(),
                Tags: tags, Kind: _command.Kind, ExpectedSha256: force ? null : _command.Sha256);
            _command = await App.Api.UpdateCommandAsync(_command.Id, request);
            _dirty = false;
            UpdateTitle();
            StatusText.Text = $"Saved {DateTime.Now:HH:mm:ss} and written to the command folder.";
            Saved?.Invoke(this, _command);
            return true;
        }
        catch (ApiException ex) when (ex.IsConflict)
        {
            var answer = MessageBox.Show(this,
                "This command was changed elsewhere (another PC, or 'Sync local changes to DB') since you opened it.\n\n"
                + "Yes = overwrite the server version with yours\nNo = discard your changes and reload the server version\nCancel = keep editing",
                "Save conflict", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (answer == MessageBoxResult.Yes)
                return await SaveAsync(force: true);
            if (answer == MessageBoxResult.No)
                await ReloadAsync();
            return false;
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            StatusText.Text = "Save failed: " + ex.Message;
            return false;
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private async Task ReloadAsync()
    {
        _command = await App.Api.GetCommandAsync(_command.Id);
        _loading = true;
        Editor.Text = _command.TextContent ?? "";
        DescriptionBox.Text = _command.Description ?? "";
        TagsBox.Text = string.Join(", ", _command.Tags);
        _loading = false;
        _dirty = false;
        UpdateTitle();
        StatusText.Text = "Reloaded from the server.";
    }

    private void Run_Executed(object sender, ExecutedRoutedEventArgs e) => Run_Click(sender, e);

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_dirty && !await SaveAsync())
            return;
        try
        {
            var entry = new ManifestEntry(LibraryItemType.Command, _command.Id, _command.RelativePath, _command.Sha256, _command.Size, _command.UpdatedUtc);
            var path = await App.Sync.WriteLocalAsync(App.Settings.CommandFolder, entry);
            CommandRunner.Run(path, ArgsBox.Text, App.Settings, App.Settings.CommandFolder);
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or IOException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "Run failed: " + ex.Message;
        }
    }

    /// <summary>Opens a working copy in the external editor; changes saved there are pulled back into this window.</summary>
    private void External_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(AppPaths.EditDir, _command.Id.ToString());
            Directory.CreateDirectory(dir);
            _externalPath = Path.Combine(dir, _command.Name);
            File.WriteAllBytes(_externalPath, TextContent.ToBytes(Editor.Text));

            _externalWatcher?.Dispose();
            _externalWatcher = new FileSystemWatcher(dir, _command.Name) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            _externalWatcher.Changed += (_, _) => Dispatcher.BeginInvoke(ReloadFromExternal);
            _externalWatcher.Renamed += (_, _) => Dispatcher.BeginInvoke(ReloadFromExternal); // editors that save via rename
            _externalWatcher.EnableRaisingEvents = true;

            CommandRunner.OpenInExternalEditor(App.Settings.ExternalEditor, _externalPath);
            StatusText.Text = $"Editing in {App.Settings.ExternalEditor}. Changes you save there appear here; press Ctrl+S to save to the server.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "Could not open the external editor: " + ex.Message;
        }
    }

    private async void ReloadFromExternal()
    {
        if (_externalPath is null || !File.Exists(_externalPath))
            return;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(_externalPath);
                if (!TextContent.TryDecode(bytes, out var text))
                {
                    StatusText.Text = "The external editor saved a non-UTF-8 file; save it as UTF-8 to bring it back.";
                    return;
                }
                if (text != Editor.Text)
                {
                    var caret = Editor.CaretOffset;
                    Editor.Document.Text = text;
                    Editor.CaretOffset = Math.Min(caret, Editor.Document.TextLength);
                    StatusText.Text = $"Reloaded from external editor at {DateTime.Now:HH:mm:ss} - press Ctrl+S to save to the server.";
                }
                return;
            }
            catch (IOException)
            {
                await Task.Delay(200); // editor still writing
            }
        }
    }

    private async void EditorWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_dirty)
        {
            var answer = MessageBox.Show(this, $"Save changes to {_command.RelativePath}?", "CmdManager",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
            if (answer == MessageBoxResult.Yes)
            {
                e.Cancel = true;
                if (await SaveAsync())
                {
                    _dirty = false;
                    Close();
                }
                return;
            }
        }
        _externalWatcher?.Dispose();
    }
}
