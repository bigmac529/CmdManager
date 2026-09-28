using System.Net.Http;
using System.Windows;
using CmdManager.Client.Services;
using CmdManager.Core;
using CmdManager.Core.Contracts;
using CmdManager.Core.Http;

namespace CmdManager.Client.Views;

public partial class NewCommandWindow : Window
{
    private sealed record TemplateChoice(CommandTemplateKind Kind, string Title);

    private readonly bool _csxAssociationWorks;

    public NewCommandWindow()
    {
        InitializeComponent();
        TemplateBox.ItemsSource = CommandTemplates.All.Select(t => new TemplateChoice(t.Kind, t.Title)).ToList();
        _csxAssociationWorks = CsxIntegration.GetStatus().IsWorking;
        WrapperBox.IsChecked = App.Settings.GenerateCmdWrappers ?? !_csxAssociationWorks;
        WrapperBox.Checked += (_, _) => UpdatePreview();
        WrapperBox.Unchecked += (_, _) => UpdatePreview();
        CsxInfo.Text = _csxAssociationWorks
            ? "Not needed: .csx files run directly from the command line on this PC (PATHEXT + association). Keep it for other machines."
            : ".csx is not executable from the command line on this PC yet (see Settings), so a wrapper is recommended.";
        TemplateBox.SelectedIndex = 0;
        Loaded += async (_, _) =>
        {
            NameBox.Focus();
            await CheckDotnetScriptAsync();
        };
    }

    /// <summary>Commands created on the server.</summary>
    public List<CommandDto> Created { get; } = [];

    private CommandTemplateKind Kind => (TemplateBox.SelectedItem as TemplateChoice)?.Kind ?? CommandTemplateKind.CsxScriptlet;
    private bool IsCsx => Kind is CommandTemplateKind.CsxScriptlet or CommandTemplateKind.CsxLibrary;

    private void Template_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TargetPanel is null)
            return;
        TargetPanel.Visibility = Kind == CommandTemplateKind.StartCmd ? Visibility.Visible : Visibility.Collapsed;
        CsxPanel.Visibility = IsCsx ? Visibility.Visible : Visibility.Collapsed;
        UpdatePreview();
    }

    private void Name_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdatePreview();

    private IReadOnlyList<TemplateFile> BuildFiles(out string? note)
    {
        var result = CommandTemplates.Create(Kind, NameBox.Text.Trim(), TargetBox.Text, App.Settings.CsxRunner);
        note = result.Note;
        var files = result.Files.ToList();
        if (IsCsx && WrapperBox.IsChecked != true)
            files.RemoveAll(f => f.FileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase));
        return files;
    }

    private void UpdatePreview()
    {
        if (FilesText is null)
            return;
        if (!NameValidator.TryValidateCommandName(NameBox.Text, out var error))
        {
            FilesText.Text = "";
            NoteText.Text = string.IsNullOrWhiteSpace(NameBox.Text) ? "" : error;
            return;
        }
        var files = BuildFiles(out var note);
        var folder = FolderBox.Text.Trim().Trim('/', '\\');
        FilesText.Text = string.Join("\n", files.Select(f => string.IsNullOrEmpty(folder) ? f.FileName : $"{folder}/{f.FileName}"));
        NoteText.Text = note ?? "";
    }

    private async Task CheckDotnetScriptAsync()
    {
        if (CommandTemplates.IsScriptCs(App.Settings.CsxRunner))
        {
            DotnetScriptStatus.Text = $"Runner: {App.Settings.CsxRunner}";
            return;
        }
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
        DotnetScriptStatus.Text = "Installing dotnet-script...";
        try
        {
            var (ok, output) = await DotnetTools.InstallDotnetScriptAsync();
            if (!ok)
                MessageBox.Show(this, output, "Install dotnet-script", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "Install dotnet-script", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        InstallButton.IsEnabled = true;
        await CheckDotnetScriptAsync();
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "";
        if (!NameValidator.TryValidateCommandName(NameBox.Text, out var error))
        {
            StatusText.Text = error;
            return;
        }

        var folder = FolderBox.Text.Trim();
        IReadOnlyList<TemplateFile> files;
        try
        {
            files = BuildFiles(out _);
            foreach (var f in files)
                LibraryPath.Combine(folder, f.FileName); // validates the folder
        }
        catch (ArgumentException ex)
        {
            StatusText.Text = ex.Message;
            return;
        }

        CreateButton.IsEnabled = false;
        try
        {
            foreach (var f in files)
            {
                var path = LibraryPath.Combine(folder, f.FileName).Value;
                var dto = await App.Api.CreateCommandAsync(new CommandUpsertRequest(path, f.Content,
                    Description: string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim()));
                Created.Add(dto);
            }
            DialogResult = true;
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            StatusText.Text = ex.Message + (Created.Count > 0 ? $" ({Created.Count} file(s) were created before the error.)" : "");
            if (Created.Count > 0)
                DialogResult = true;
        }
        finally
        {
            CreateButton.IsEnabled = true;
        }
    }
}
