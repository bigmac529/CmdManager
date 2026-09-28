using System.Windows;

namespace CmdManager.Client.Views;

public partial class ConfirmListDialog : Window
{
    public ConfirmListDialog(string title, string header, IEnumerable<string> lines, string okText = "OK", bool showCancel = true)
    {
        InitializeComponent();
        Title = title;
        HeaderText.Text = header;
        Lines.ItemsSource = lines.ToList();
        OkButton.Content = okText;
        if (!showCancel)
            CancelButton.Visibility = Visibility.Collapsed;
    }

    public static bool Confirm(Window owner, string title, string header, IEnumerable<string> lines, string okText = "OK") =>
        new ConfirmListDialog(title, header, lines, okText) { Owner = owner }.ShowDialog() == true;

    public static void Show(Window owner, string title, string header, IEnumerable<string> lines) =>
        new ConfirmListDialog(title, header, lines, "Close", showCancel: false) { Owner = owner }.ShowDialog();

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
