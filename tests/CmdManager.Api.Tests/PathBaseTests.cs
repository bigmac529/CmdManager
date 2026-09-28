using System.Net;
using System.Net.Http.Json;
using CmdManager.Api.Hosting;
using CmdManager.Core.Contracts;
using CmdManager.Core.Http;
using Microsoft.AspNetCore.Http;

namespace CmdManager.Api.Tests;

/// <summary>
/// Production: IIS application "/api" on cmdmanager.socha3.com; ANCM (in-process) hands the app PathBase=/api, Path=/health.
/// Local dev (Kestrel): the app applies Hosting:PathBase=/api itself. Both must give the same public URLs, and the
/// prefix must never be applied twice.
/// </summary>
public class PathBaseTests
{
    private static HttpClient RootClient(ApiFactory factory)
    {
        var http = factory.CreateClient();
        http.BaseAddress = new Uri("http://localhost/"); // absolute paths below are the full public paths
        return http;
    }

    private static async Task<string> AssertLoginWorks(HttpClient http, string registerPath, string loginPath)
    {
        var name = "pb" + Guid.NewGuid().ToString("N")[..10];
        var reg = await http.PostAsJsonAsync(registerPath, new RegisterRequest(name, "correct horse battery"), CmdManagerJson.Options);
        Assert.Equal(HttpStatusCode.Created, reg.StatusCode);
        Assert.Equal("/api/auth/me", reg.Headers.Location?.OriginalString);
        var login = await http.PostAsJsonAsync(loginPath, new LoginRequest(name, "correct horse battery"), CmdManagerJson.Options);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var auth = await login.Content.ReadFromJsonAsync<AuthResponse>(CmdManagerJson.Options);
        Assert.False(string.IsNullOrEmpty(auth?.AccessToken));
        return auth!.AccessToken;
    }

    /// <summary>Unmatched routes are 401 for anonymous callers (fallback policy), so probe with a valid token: 404 = no route.</summary>
    private static async Task<HttpStatusCode> GetWithToken(HttpClient http, string path, string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new("Bearer", token);
        return (await http.SendAsync(req)).StatusCode;
    }

    [Fact]
    public async Task Kestrel_mode_serves_the_app_under_api()
    {
        using var factory = new ApiFactory(); // TestServer without a path base = Kestrel; app applies /api itself
        var http = RootClient(factory);

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/health")).StatusCode);
        var token = await AssertLoginWorks(http, "/api/auth/register", "/api/auth/login");
        Assert.Equal("CmdManager API. See /api/health.", await http.GetStringAsync("/api/"));
        Assert.Equal(HttpStatusCode.OK, await GetWithToken(http, "/api/auth/me", token));
        Assert.Equal(HttpStatusCode.NotFound, await GetWithToken(http, "/api/api/health", token));
        Assert.Equal(HttpStatusCode.NotFound, await GetWithToken(http, "/api/api/auth/me", token));
    }

    [Fact]
    public async Task Iis_mode_with_host_supplied_path_base_is_not_applied_twice()
    {
        using var factory = new ApiFactory();
        // Like ANCM for IIS application "/api": TestServer splits "/api/health" into PathBase=/api + Path=/health
        // before the app sees it.
        factory.Server.BaseAddress = new Uri("http://localhost/api/");
        var http = RootClient(factory);

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/health")).StatusCode);
        var token = await AssertLoginWorks(http, "/api/auth/register", "/api/auth/login");
        Assert.Equal("CmdManager API. See /api/health.", await http.GetStringAsync("/api/"));
        Assert.Equal(HttpStatusCode.OK, await GetWithToken(http, "/api/auth/me", token));
        // With plain UsePathBase the second /api would be stripped as well and these would answer 200.
        Assert.Equal(HttpStatusCode.NotFound, await GetWithToken(http, "/api/api/health", token));
        Assert.Equal(HttpStatusCode.NotFound, await GetWithToken(http, "/api/api/auth/me", token));
    }

    [Fact]
    public async Task Host_supplied_path_base_alone_is_enough()
    {
        // Proves the IIS-mode emulation really hands the app PathBase=/api (the app's own prefix handling is off
        // here), which is what makes the /api/api 404 assertions above meaningful.
        using var factory = new ApiFactory();
        factory.Settings[PathBaseExtensions.ConfigKey] = "";
        factory.Server.BaseAddress = new Uri("http://localhost/api/");
        var http = RootClient(factory);

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/health")).StatusCode);
        await AssertLoginWorks(http, "/api/auth/register", "/api/auth/login");
    }

    [Fact]
    public async Task Iis_mode_full_client_round_trip()
    {
        using var factory = new ApiFactory();
        factory.Server.BaseAddress = new Uri("http://localhost/api/");
        var api = factory.NewClient(); // BaseAddress http://localhost/api/, relative request URIs
        await api.RegisterAsync(new RegisterRequest("pb" + Guid.NewGuid().ToString("N")[..10], "correct horse battery"));
        var cmd = await api.CreateCommandAsync(new CommandUpsertRequest("hello.cmd", "@echo hi"));
        Assert.Single((await api.GetManifestAsync()).Items, i => i.Id == cmd.Id);
        Assert.True(await api.TryRefreshAsync());
    }

    [Fact]
    public async Task Empty_path_base_setting_disables_the_prefix()
    {
        using var factory = new ApiFactory();
        factory.Settings[PathBaseExtensions.ConfigKey] = "";
        var http = RootClient(factory);

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
        var name = "pb" + Guid.NewGuid().ToString("N")[..10];
        await http.PostAsJsonAsync("/auth/register", new RegisterRequest(name, "correct horse battery"), CmdManagerJson.Options);
        var login = await http.PostAsJsonAsync("/auth/login", new LoginRequest(name, "correct horse battery"), CmdManagerJson.Options);
        var token = (await login.Content.ReadFromJsonAsync<AuthResponse>(CmdManagerJson.Options))!.AccessToken;
        Assert.Equal(HttpStatusCode.NotFound, await GetWithToken(http, "/api/health", token));
        Assert.Equal(HttpStatusCode.OK, await GetWithToken(http, "/auth/me", token));
    }

    [Theory]
    [InlineData("/api", "/api")]
    [InlineData("api", "/api")]
    [InlineData(" /api/ ", "/api")]
    [InlineData("/cmd/api", "/cmd/api")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData(null, "")]
    public void Normalize_path_base(string? input, string expected) =>
        Assert.Equal(new PathString(expected.Length == 0 ? null : expected), PathBaseExtensions.Normalize(input));
}
