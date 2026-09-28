using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CmdManager.Core.Contracts;
using CmdManager.Core.Http;

namespace CmdManager.Api.Tests;

public class AuthTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Health_is_healthy_and_reports_database()
    {
        var resp = await factory.CreateClient().GetAsync("health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"Healthy\"", body);
        Assert.Contains("\"connected\":true", body);
        Assert.Contains("\"pendingMigrations\":0", body);
    }

    [Fact]
    public async Task Register_returns_tokens_and_me_works()
    {
        var client = factory.NewClient();
        var auth = await client.RegisterAsync(new RegisterRequest("Michael", "s3cret-password", "m@example.com"));

        Assert.False(string.IsNullOrEmpty(auth.AccessToken));
        Assert.False(string.IsNullOrEmpty(auth.RefreshToken));
        Assert.True(auth.ExpiresUtc > DateTime.UtcNow);
        Assert.Equal("Michael", auth.User.UserName);

        var me = await client.MeAsync();
        Assert.Equal(auth.User.Id, me.Id);
        Assert.Equal("m@example.com", me.Email);
    }

    [Fact]
    public async Task Register_rejects_duplicate_user_name_case_insensitively()
    {
        var name = "dup" + Guid.NewGuid().ToString("N")[..8];
        await factory.NewClient().RegisterAsync(new RegisterRequest(name, "password123"));
        var ex = await Assert.ThrowsAsync<ApiException>(() => factory.NewClient().RegisterAsync(new RegisterRequest(name.ToUpperInvariant(), "password123")));
        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);
    }

    [Theory]
    [InlineData("ab", "password123")]        // user name too short
    [InlineData("has space", "password123")]
    [InlineData("validname", "short")]       // password too short
    public async Task Register_validates_input(string user, string password)
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => factory.NewClient().RegisterAsync(new RegisterRequest(user, password)));
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
    }

    [Fact]
    public async Task Login_succeeds_with_correct_password_and_fails_otherwise()
    {
        var name = "login" + Guid.NewGuid().ToString("N")[..8];
        await factory.NewClient().RegisterAsync(new RegisterRequest(name, "right-password"));

        var ok = await factory.NewClient().LoginAsync(new LoginRequest(name.ToUpperInvariant(), "right-password"));
        Assert.Equal(name, ok.User.UserName);

        var bad = await Assert.ThrowsAsync<ApiException>(() => factory.NewClient().LoginAsync(new LoginRequest(name, "wrong-password")));
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        var unknown = await Assert.ThrowsAsync<ApiException>(() => factory.NewClient().LoginAsync(new LoginRequest("nobody-here", "whatever1")));
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
    }

    [Fact]
    public async Task Login_locks_out_after_repeated_failures()
    {
        var name = "lock" + Guid.NewGuid().ToString("N")[..8];
        await factory.NewClient().RegisterAsync(new RegisterRequest(name, "right-password"));
        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<ApiException>(() => factory.NewClient().LoginAsync(new LoginRequest(name, "nope-nope")));

        var locked = await Assert.ThrowsAsync<ApiException>(() => factory.NewClient().LoginAsync(new LoginRequest(name, "right-password")));
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
    }

    [Theory]
    [InlineData("auth/me")]
    [InlineData("commands")]
    [InlineData("assets")]
    [InlineData("library/manifest")]
    [InlineData("library/export")]
    public async Task Library_endpoints_require_authentication(string url)
    {
        var resp = await factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Garbage_token_is_rejected()
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not.a.jwt");
        var resp = await http.GetAsync("commands");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_tokens_and_old_refresh_token_stops_working()
    {
        var client = await factory.RegisteredClientAsync();
        var first = client.Session!;

        Assert.True(await client.TryRefreshAsync());
        var second = client.Session!;
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        await client.MeAsync();

        var http = factory.CreateClient();
        var reuse = await http.PostAsJsonAsync("auth/refresh", new RefreshRequest(first.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_refresh_token()
    {
        var client = await factory.RegisteredClientAsync();
        var refresh = client.Session!.RefreshToken;
        await client.LogoutAsync();
        Assert.Null(client.Session);

        var resp = await factory.CreateClient().PostAsJsonAsync("auth/refresh", new RefreshRequest(refresh));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Auth_config_reports_registration_open()
    {
        var cfg = await factory.NewClient().GetAuthConfigAsync();
        Assert.True(cfg.AllowRegistration);
        Assert.Equal(8, cfg.MinPasswordLength);
    }
}

public class RegistrationDisabledTests : IClassFixture<RegistrationDisabledTests.ClosedFactory>
{
    public sealed class ClosedFactory : ApiFactory
    {
        public ClosedFactory() => Settings["Auth:AllowRegistration"] = "false";
    }

    private readonly ClosedFactory _factory;
    public RegistrationDisabledTests(ClosedFactory factory) => _factory = factory;

    [Fact]
    public async Task Register_is_forbidden_when_disabled()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => _factory.NewClient().RegisterAsync(new RegisterRequest("someone", "password123")));
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.False((await _factory.NewClient().GetAuthConfigAsync()).AllowRegistration);
    }
}
