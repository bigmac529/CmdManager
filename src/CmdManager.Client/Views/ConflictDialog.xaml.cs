using System.Windows;
using CmdManager.Core.Sync;

namespace CmdManager.Client.Views;

public partial class ConflictDialog : Window
{
    public ConflictDialog(SyncItem item, int index, int total)
    {
        InitializeComponent();
        Title = $"CmdManager - Conflict {index} of {total}";
        PathText.Text = item.RelativePath;
        ReasonText.Text = char.ToUpperInvariant(item.Reason[0]) + item.Reason[1..] + " since the last sync.";
        DetailText.Text = $"local:  {Short(item.LocalSha256)}\nlast sync: {Short(item.BaseSha256)}\nserver: {Short(item.ServerSha256)}";
        BothButton.IsEnabled = item.LocalExists && item.ServerExists;
        LocalButton.Content = item.LocalExists ? "Keep local" : "Keep local (delete from DB)";
        ServerButton.Content = item.ServerExists ? "Keep server" : "Keep server (delete local file)";
    }

    public ConflictResolution Resolution { get; private set; } = ConflictResolution.Skip;
    public bool ApplyToAll => ApplyToAllBox.IsChecked == true;

    private static string Short(string? sha) => sha is null ? "(none)" : sha[..Math.Min(12, sha.Length)] + "…";

    private void Local_Click(object sender, RoutedEventArgs e) => Close(ConflictResolution.KeepLocal);
    private void Server_Click(object sender, RoutedEventArgs e) => Close(ConflictResolution.KeepServer);
    private void Both_Click(object sender, RoutedEventArgs e) => Close(ConflictResolution.KeepBoth);
    private void Skip_Click(object sender, RoutedEventArgs e) => Close(ConflictResolution.Skip);

    private void Close(ConflictResolution r)
    {
        Resolution = r;
        DialogResult = r != ConflictResolution.Skip;
    }
}
