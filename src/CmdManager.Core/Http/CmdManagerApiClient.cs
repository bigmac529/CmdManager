using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CmdManager.Core.Contracts;

namespace CmdManager.Core.Http;

/// <summary>
/// Typed client for the CmdManager API. Uses one <see cref="HttpClient"/> whose <see cref="AuthTokenHandler"/> attaches
/// the bearer token to every request and refreshes it; the tokens live in the shared <see cref="AuthSession"/>.
/// </summary>
public sealed class CmdManagerApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _disposeHttp;

    /// <param name="http">An HttpClient whose pipeline contains an <see cref="AuthTokenHandler"/> for <paramref name="session"/>, with BaseAddress set.</param>
    public CmdManagerApiClient(HttpClient http, AuthSession session, bool disposeHttp = false)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        AuthSession = session ?? throw new ArgumentNullException(nameof(session));
        _disposeHttp = disposeHttp;
        if (_http.BaseAddress is null)
            throw new ArgumentException("HttpClient.BaseAddress must be set to the server URL.", nameof(http));
    }

    /// <summary>Creates the single HttpClient (with <see cref="AuthTokenHandler"/>) for <paramref name="serverUrl"/>, e.g. https://cmdmanager.socha3.com.</summary>
    public static CmdManagerApiClient Create(string serverUrl, AuthSession session, TimeSpan? timeout = null)
    {
        var handler = new AuthTokenHandler(session) { InnerHandler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) } };
        var http = new HttpClient(handler) { BaseAddress = NormalizeServerUrl(serverUrl), Timeout = timeout ?? TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CmdManager.Client/1.0");
        return new CmdManagerApiClient(http, session, disposeHttp: true);
    }

    public static Uri NormalizeServerUrl(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("Server URL must be an absolute http(s) URL, e.g. https://cmdmanager.socha3.com", nameof(serverUrl));
        var s = uri.GetLeftPart(UriPartial.Path);
        return new Uri(s.EndsWith('/') ? s : s + "/");
    }

    public Uri ServerUrl => _http.BaseAddress!;

    public AuthSession AuthSession { get; }

    public AuthResponse? Session => AuthSession.Current;

    public bool IsLoggedIn => AuthSession.IsLoggedIn;

    // ---------------- auth ----------------

    public Task<AuthConfigDto> GetAuthConfigAsync(CancellationToken ct = default) =>
        SendAsync<AuthConfigDto>(() => new HttpRequestMessage(HttpMethod.Get, "api/auth/config"), ct);

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var auth = await SendAsync<AuthResponse>(() => Json(HttpMethod.Post, "api/auth/register", request), ct);
        AuthSession.Set(auth);
        return auth;
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var auth = await SendAsync<AuthResponse>(() => Json(HttpMethod.Post, "api/auth/login", request), ct);
        AuthSession.Set(auth);
        return auth;
    }

    public Task<UserDto> MeAsync(CancellationToken ct = default) =>
        SendAsync<UserDto>(() => new HttpRequestMessage(HttpMethod.Get, "api/auth/me"), ct);

    /// <summary>Forces a token refresh (normally automatic). False when the session is gone.</summary>
    public async Task<bool> TryRefreshAsync(CancellationToken ct = default)
    {
        var current = AuthSession.Current;
        if (current is null)
            return false;
        using var req = Json(HttpMethod.Post, "api/auth/refresh", new RefreshRequest(current.RefreshToken));
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            return false;
        AuthSession.Set(await resp.Content.ReadFromJsonAsync<AuthResponse>(CmdManagerJson.Options, ct));
        return true;
    }

    /// <summary>Revokes the refresh token on the server (best effort) and clears the session.</summary>
    public async Task LogoutAsync(CancellationToken ct = default)
    {
        var current = AuthSession.Current;
        if (current is not null)
        {
            try
            {
                using var req = Json(HttpMethod.Post, "api/auth/logout", new RefreshRequest(current.RefreshToken));
                using var resp = await _http.SendAsync(req, ct);
            }
            catch (HttpRequestException)
            {
                // offline: the refresh token simply expires on its own
            }
        }

        AuthSession.Set(null);
    }

    // ---------------- commands ----------------

    public Task<List<CommandSummaryDto>> ListCommandsAsync(string? search = null, CancellationToken ct = default) =>
        SendAsync<List<CommandSummaryDto>>(() => new HttpRequestMessage(HttpMethod.Get, "api/commands" + Query(("search", search))), ct);

    public Task<CommandDto> GetCommandAsync(int id, CancellationToken ct = default) =>
        SendAsync<CommandDto>(() => new HttpRequestMessage(HttpMethod.Get, $"api/commands/{id}"), ct);

    public Task<CommandDto> CreateCommandAsync(CommandUpsertRequest request, CancellationToken ct = default) =>
        SendAsync<CommandDto>(() => Json(HttpMethod.Post, "api/commands", request), ct);

    public Task<CommandDto> UpdateCommandAsync(int id, CommandUpsertRequest request, CancellationToken ct = default) =>
        SendAsync<CommandDto>(() => Json(HttpMethod.Put, $"api/commands/{id}", request), ct);

    /// <param name="expectedSha256">When set, the server refuses (409) if the command's content changed meanwhile.</param>
    public Task DeleteCommandAsync(int id, string? expectedSha256 = null, CancellationToken ct = default) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"api/commands/{id}" + Query(("expectedSha256", expectedSha256))), ct);

    /// <summary>Replaces only the content (keeps name/description/tags). 409 when <paramref name="expectedSha256"/> no longer matches.</summary>
    public Task<CommandDto> ReplaceCommandContentAsync(int id, byte[] content, string? expectedSha256 = null, CancellationToken ct = default) =>
        SendAsync<CommandDto>(() => RawContent(HttpMethod.Put, $"api/commands/{id}/content", content, expectedSha256), ct);

    public Task<byte[]> GetCommandContentAsync(int id, CancellationToken ct = default) =>
        GetBytesAsync($"api/commands/{id}/content", ct);

    // ---------------- assets ----------------

    public Task<List<AssetDto>> ListAssetsAsync(string? search = null, CancellationToken ct = default) =>
        SendAsync<List<AssetDto>>(() => new HttpRequestMessage(HttpMethod.Get, "api/assets" + Query(("search", search))), ct);

    public Task<AssetDto> GetAssetAsync(int id, CancellationToken ct = default) =>
        SendAsync<AssetDto>(() => new HttpRequestMessage(HttpMethod.Get, $"api/assets/{id}"), ct);

    /// <summary>Creates a new asset (multipart upload). 409 when the path is taken.</summary>
    public Task<AssetDto> UploadAssetAsync(string relativePath, byte[] content, string? description = null, CancellationToken ct = default) =>
        SendAsync<AssetDto>(() =>
        {
            var form = new MultipartFormDataContent
            {
                { new StringContent(relativePath), "path" },
                { new ByteArrayContent(content) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }, "file", Path.GetFileName(relativePath) }
            };
            if (description is not null)
                form.Add(new StringContent(description), "description");
            return new HttpRequestMessage(HttpMethod.Post, "api/assets") { Content = form };
        }, ct);

    public Task<AssetDto> ReplaceAssetContentAsync(int id, byte[] content, string? expectedSha256 = null, CancellationToken ct = default) =>
        SendAsync<AssetDto>(() => RawContent(HttpMethod.Put, $"api/assets/{id}/content", content, expectedSha256), ct);

    public Task<AssetDto> UpdateAssetAsync(int id, AssetUpdateRequest request, CancellationToken ct = default) =>
        SendAsync<AssetDto>(() => Json(HttpMethod.Put, $"api/assets/{id}", request), ct);

    public Task DeleteAssetAsync(int id, string? expectedSha256 = null, CancellationToken ct = default) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"api/assets/{id}" + Query(("expectedSha256", expectedSha256))), ct);

    public Task<byte[]> GetAssetContentAsync(int id, CancellationToken ct = default) =>
        GetBytesAsync($"api/assets/{id}/content", ct);

    // ---------------- library ----------------

    public Task<ManifestDto> GetManifestAsync(CancellationToken ct = default) =>
        SendAsync<ManifestDto>(() => new HttpRequestMessage(HttpMethod.Get, "api/library/manifest"), ct);

    public Task<ImportResult> ImportAsync(ImportRequest request, CancellationToken ct = default) =>
        SendAsync<ImportResult>(() => Json(HttpMethod.Post, "api/library/import", request), ct);

    public Task<byte[]> GetContentAsync(ManifestEntry entry, CancellationToken ct = default) =>
        entry.Type == LibraryItemType.Command ? GetCommandContentAsync(entry.Id, ct) : GetAssetContentAsync(entry.Id, ct);

    // ---------------- plumbing ----------------

    private static HttpRequestMessage Json<T>(HttpMethod method, string url, T body) =>
        new(method, url) { Content = JsonContent.Create(body, options: CmdManagerJson.Options) };

    private static HttpRequestMessage RawContent(HttpMethod method, string url, byte[] content, string? expectedSha256)
    {
        var req = new HttpRequestMessage(method, url)
        {
            Content = new ByteArrayContent(content) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }
        };
        if (!string.IsNullOrEmpty(expectedSha256))
            req.Headers.TryAddWithoutValidation("If-Match", "\"" + expectedSha256 + "\"");
        return req;
    }

    private static string Query(params (string Key, string? Value)[] parts)
    {
        var q = string.Join('&', parts.Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value!)}"));
        return q.Length == 0 ? string.Empty : "?" + q;
    }

    private async Task<byte[]> GetBytesAsync(string url, CancellationToken ct)
    {
        using var resp = await SendRawAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    private async Task SendAsync(Func<HttpRequestMessage> factory, CancellationToken ct)
    {
        using var _ = await SendRawAsync(factory, ct);
    }

    private async Task<T> SendAsync<T>(Func<HttpRequestMessage> factory, CancellationToken ct)
    {
        using var resp = await SendRawAsync(factory, ct);
        return (await resp.Content.ReadFromJsonAsync<T>(CmdManagerJson.Options, ct))!;
    }

    private async Task<HttpResponseMessage> SendRawAsync(Func<HttpRequestMessage> factory, CancellationToken ct)
    {
        using var req = factory();
        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        try
        {
            await EnsureSuccessAsync(resp, ct);
        }
        catch
        {
            resp.Dispose();
            throw;
        }
        return resp;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
            return;

        string message = $"{(int)resp.StatusCode} {resp.ReasonPhrase}";
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
                    {
                        var all = errors.EnumerateObject().SelectMany(p => p.Value.EnumerateArray().Select(e => e.GetString())).Where(s => s is not null);
                        message = string.Join(" ", all);
                    }
                    else if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                        message = detail.GetString()!;
                    else if (root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                        message = title.GetString()!;
                }
            }
        }
        catch (JsonException)
        {
            // not a problem-details body; keep the status line
        }

        throw new ApiException(resp.StatusCode, message);
    }

    public void Dispose()
    {
        if (_disposeHttp)
            _http.Dispose();
    }
}
