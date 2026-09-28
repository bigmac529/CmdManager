using System.Net.Http;
using System.Windows;
using CmdManager.Core.Contracts;
using CmdManager.Core.Http;

namespace CmdManager.Client.Views;

public partial class LoginWindow : Window
{
    public LoginWindow(string? message)
    {
        InitializeComponent();
        ServerBox.Text = App.Settings.ServerUrl;
        UserBox.Text = App.Settings.LastUserName ?? "";
        if (!string.IsNullOrEmpty(message))
        {
            MessageText.Text = message;
            MessageText.Visibility = Visibility.Visible;
        }
        Loaded += (_, _) =>
        {
            if (string.IsNullOrEmpty(UserBox.Text)) UserBox.Focus();
            else PasswordBox.Focus();
        };
    }

    private bool ApplyServer()
    {
        try
        {
            App.ChangeServer(ServerBox.Text);
            return true;
        }
        catch (ArgumentException ex)
        {
            StatusText.Text = ex.Message;
            return false;
        }
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyServer())
            return;
        if (string.IsNullOrWhiteSpace(UserBox.Text) || PasswordBox.Password.Length == 0)
        {
            StatusText.Text = "Enter your user name and password.";
            return;
        }

        LoginButton.IsEnabled = false;
        StatusText.Text = "Logging in...";
        try
        {
            await App.Api.LoginAsync(new LoginRequest(UserBox.Text.Trim(), PasswordBox.Password));
            App.Settings.LastUserName = UserBox.Text.Trim();
            App.Settings.Save();
            DialogResult = true;
        }
        catch (ApiException ex)
        {
            StatusText.Text = ex.Message;
        }
        catch (HttpRequestException ex)
        {
            StatusText.Text = $"Cannot reach {App.Settings.ServerUrl}: {ex.Message}";
        }
        catch (TaskCanceledException)
        {
            StatusText.Text = "The server did not respond in time.";
        }
        finally
        {
            LoginButton.IsEnabled = true;
        }
    }

    private async void CreateAccount_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyServer())
            return;
        try
        {
            var config = await App.Api.GetAuthConfigAsync();
            if (!config.AllowRegistration)
            {
                StatusText.Text = "Registration is closed on this server. Ask the administrator for an account.";
                return;
            }
            var register = new RegisterWindow(config.MinPasswordLength) { Owner = this };
            if (register.ShowDialog() == true)
            {
                App.Settings.LastUserName = register.UserName;
                App.Settings.Save();
                DialogResult = true; // registering also logs in
            }
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            StatusText.Text = $"Cannot reach {App.Settings.ServerUrl}: {ex.Message}";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
