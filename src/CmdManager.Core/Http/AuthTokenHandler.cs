using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CmdManager.Core.Contracts;

namespace CmdManager.Core.Http;

/// <summary>
/// Attaches "Authorization: Bearer &lt;access token&gt;" to every request. Refreshes the access token shortly before it
/// expires, and once after a 401; when refreshing is impossible it clears the session and raises
/// <see cref="AuthSession.Expired"/> (the app stops background sync and shows the Login window).
/// </summary>
/// <param name="session">Shared login state.</param>
/// <param name="baseAddress">API base URL (e.g. https://cmdmanager.socha3.com/api/), used for the refresh call. When null
/// it is derived from each request URI (everything before the first known route segment).</param>
public sealed class AuthTokenHandler(AuthSession session, Uri? baseAddress = null) : DelegatingHandler
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(60);
    private static readonly string[] AnonymousPaths = ["auth/login", "auth/register", "auth/refresh", "auth/config", "health"];
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (IsAnonymous(request.RequestUri))
            return await base.SendAsync(request, ct);

        var current = session.Current;
        if (current is not null && current.ExpiresUtc - RefreshSkew <= DateTime.UtcNow)
            current = await RefreshAsync(request.RequestUri!, current, ct) ?? current;

        if (request.Content is not null)
            await request.Content.LoadIntoBufferAsync(ct); // so the request can be re-sent after a refresh

        Attach(request, session.Current);
        var response = await base.SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        var sent = session.Current;
        if (sent is null)
            return response; // not logged in at all

        var refreshed = await RefreshAsync(request.RequestUri!, sent, ct);
        if (refreshed is null)
        {
            session.MarkExpired();
            return response;
        }

        response.Dispose();
        Attach(request, refreshed);
        response = await base.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            session.MarkExpired();
        return response;
    }

    private static void Attach(HttpRequestMessage request, AuthResponse? s) =>
        request.Headers.Authorization = s is null ? null : new AuthenticationHeaderValue("Bearer", s.AccessToken);

    private static bool IsAnonymous(Uri? uri)
    {
        if (uri is null)
            return false;
        var path = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString;
        path = path.TrimStart('/');
        return AnonymousPaths.Any(p => path.EndsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns the new session, or null when the refresh token was rejected (or there is none).</summary>
    private async Task<AuthResponse?> RefreshAsync(Uri requestUri, AuthResponse used, CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            var current = session.Current;
            if (current is null)
                return null;
            if (!ReferenceEquals(current, used))
                return current; // another request already refreshed

            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(baseAddress ?? ApiBase(requestUri), "auth/refresh"))
            {
                Content = JsonContent.Create(new RefreshRequest(current.RefreshToken), options: CmdManagerJson.Options)
            };
            using var resp = await base.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return null;
            var auth = await resp.Content.ReadFromJsonAsync<AuthResponse>(CmdManagerJson.Options, ct);
            session.Set(auth);
            return auth;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>First path segments of the API's own routes (the app maps /auth, /commands, ... under its path base).</summary>
    private static readonly string[] RouteRoots = ["auth", "commands", "assets", "library", "health"];

    /// <summary>
    /// "https://host/api/" from "https://host/api/commands/5" (everything before the first known route segment);
    /// "https://host/" when none matches.
    /// </summary>
    internal static Uri ApiBase(Uri requestUri)
    {
        var segments = requestUri.AbsolutePath.Split('/');
        var prefix = new List<string>();
        foreach (var seg in segments.Skip(1))
        {
            if (RouteRoots.Contains(Uri.UnescapeDataString(seg), StringComparer.OrdinalIgnoreCase))
                return new Uri(requestUri.GetLeftPart(UriPartial.Authority) + "/" + string.Concat(prefix.Select(p => p + "/")));
            prefix.Add(seg);
        }
        return new Uri(requestUri.GetLeftPart(UriPartial.Authority) + "/");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _refreshLock.Dispose();
        base.Dispose(disposing);
    }
}
