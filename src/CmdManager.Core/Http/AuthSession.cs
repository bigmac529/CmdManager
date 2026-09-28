using CmdManager.Core.Contracts;

namespace CmdManager.Core.Http;

/// <summary>
/// The current login (access + refresh token), shared by <see cref="AuthTokenHandler"/> and <see cref="CmdManagerApiClient"/>.
/// The desktop app persists it (DPAPI) on <see cref="Changed"/> and returns to the Login window on <see cref="Expired"/>.
/// </summary>
public sealed class AuthSession
{
    private readonly object _gate = new();
    private AuthResponse? _current;

    public AuthResponse? Current
    {
        get { lock (_gate) return _current; }
    }

    public bool IsLoggedIn => Current is not null;

    /// <summary>New tokens (login, register, refresh) or null (logout, expiry).</summary>
    public event EventHandler<AuthResponse?>? Changed;

    /// <summary>The server rejected the token and it could not be refreshed: the user must log in again.</summary>
    public event EventHandler? Expired;

    public void Set(AuthResponse? session)
    {
        lock (_gate) _current = session;
        Changed?.Invoke(this, session);
    }

    /// <summary>Restores persisted tokens without raising <see cref="Changed"/>.</summary>
    public void Restore(AuthResponse? session)
    {
        lock (_gate) _current = session;
    }

    internal void MarkExpired()
    {
        Set(null);
        Expired?.Invoke(this, EventArgs.Empty);
    }
}
