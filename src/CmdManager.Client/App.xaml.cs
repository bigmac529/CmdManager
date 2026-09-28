using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using CmdManager.Client.Services;
using CmdManager.Client.Views;
using CmdManager.Core.Http;
using CmdManager.Core.Sync;

namespace CmdManager.Client;

/// <summary>
/// Startup: restore the DPAPI-protected session; if it is still valid go straight to the main window, otherwise show
/// Login (with a "Create account" link to Register). One HttpClient + AuthTokenHandler serves every API call; when the
/// session can no longer be refreshed the main window stops syncing and the Login window comes back.
/// </summary>
public partial class App : Application
{
    public static ClientSettings Settings { get; private set; } = new();
    public static AuthSession Session { get; } = new();
    public static CmdManagerApiClient Api { get; private set; } = null!;
    public static LibrarySyncService Sync { get; private set; } = null!;

    private bool _loginVisible;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        SyntaxHighlighting.Register();

        Settings = ClientSettings.Load();
        Session.Restore(TokenStore.Load());
        Session.Changed += (_, s) => TokenStore.Save(s);
        CreateApi(Settings.ServerUrl);

        var goToMain = false;
        if (Session.IsLoggedIn)
        {
            try
            {
                await Api.MeAsync();
                goToMain = true;
            }
            catch (HttpRequestException ex)
            {
                // Server unreachable: still open the app; the local command folder keeps working.
                AppPaths.Log("Startup: server unreachable: " + ex.Message);
                goToMain = true;
            }
            catch (ApiException ex)
            {
                AppPaths.Log("Startup: stored session rejected: " + ex.Message);
            }
        }

        Session.Expired += (_, _) => Dispatcher.BeginInvoke(() => ReturnToLogin("Your session has expired. Please log in again."));

        if (goToMain)
            ShowMain();
        else
            ShowLogin(null);
    }

    public static void CreateApi(string serverUrl)
    {
        Api?.Dispose();
        Api = CmdManagerApiClient.Create(serverUrl, Session);
        Sync = new LibrarySyncService(Api, AppPaths.SyncStateFile);
    }

    /// <summary>Switches to another server URL (clears the session: tokens are per server).</summary>
    public static void ChangeServer(string serverUrl)
    {
        var normalized = CmdManagerApiClient.NormalizeServerUrl(CmdManagerApiClient.UpgradeServerUrl(serverUrl)).ToString(); // keeps the trailing "/" (…/api/)
        if (string.Equals(normalized, Settings.ServerUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return;
        Session.Set(null);
        Settings.ServerUrl = normalized;
        Settings.Save();
        CreateApi(normalized);
    }

    public void ShowLogin(string? message)
    {
        if (_loginVisible)
            return;
        _loginVisible = true;
        try
        {
            var login = new LoginWindow(message);
            if (login.ShowDialog() == true)
                ShowMain();
            else
                Shutdown();
        }
        finally
        {
            _loginVisible = false;
        }
    }

    public void ShowMain()
    {
        var main = new MainWindow();
        MainWindow = main;
        main.Closed += (_, _) =>
        {
            if (!main.IsSwitchingToLogin)
                Shutdown();
        };
        main.Show();
    }

    /// <summary>Logout or expired session: stop the main window (timers, watcher) and show Login.</summary>
    public void ReturnToLogin(string? message)
    {
        if (MainWindow is MainWindow main)
            main.CloseForLogin();
        ShowLogin(message);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppPaths.Log("Unhandled: " + e.Exception);
        MessageBox.Show(e.Exception.Message, "CmdManager", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
