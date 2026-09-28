using System.Net.Http;
using System.Windows;
using CmdManager.Core.Contracts;
using CmdManager.Core.Http;

namespace CmdManager.Client.Views;

public partial class RegisterWindow : Window
{
    private readonly int _minPasswordLength;

    public RegisterWindow(int minPasswordLength)
    {
        InitializeComponent();
        _minPasswordLength = minPasswordLength;
        ServerText.Text = "Server: " + App.Settings.ServerUrl;
        PasswordLabel.Text = $"Password (at least {minPasswordLength} characters)";
        Loaded += (_, _) => UserBox.Focus();
    }

    public string UserName => UserBox.Text.Trim();

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (PasswordBox.Password != ConfirmBox.Password)
        {
            StatusText.Text = "The passwords do not match.";
            return;
        }
        if (PasswordBox.Password.Length < _minPasswordLength)
        {
            StatusText.Text = $"The password must be at least {_minPasswordLength} characters.";
            return;
        }

        CreateButton.IsEnabled = false;
        StatusText.Text = "";
        try
        {
            await App.Api.RegisterAsync(new RegisterRequest(UserName, PasswordBox.Password,
                string.IsNullOrWhiteSpace(EmailBox.Text) ? null : EmailBox.Text.Trim()));
            DialogResult = true;
        }
        catch (ApiException ex)
        {
            StatusText.Text = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            StatusText.Text = "Cannot reach the server: " + ex.Message;
        }
        finally
        {
            CreateButton.IsEnabled = true;
        }
    }
}
