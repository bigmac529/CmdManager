using System.Net;
using System.Text;
using CmdManager.Core.Contracts;
using CmdManager.Core.Http;

namespace CmdManager.Core.Tests;

/// <summary>The API lives at https://cmdmanager.socha3.com/api/: every client request must keep the /api/ prefix.</summary>
public class ApiClientUrlTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("null", Encoding.UTF8, "application/json")
            });
        }
    }

    private static AuthResponse FakeSession() => new("access", DateTime.UtcNow.AddHours(1), "refresh",
        DateTime.UtcNow.AddDays(1), new UserDto(1, "u", null, DateTime.UtcNow));

    [Fact]
    public async Task Every_client_call_stays_under_the_api_base_address()
    {
        var recorder = new RecordingHandler();
        var session = new AuthSession();
        session.Set(FakeSession());
        var baseAddress = new Uri(CmdManagerApiClient.DefaultServerUrl);
        var handler = new AuthTokenHandler(session, baseAddress) { InnerHandler = recorder };
        using var api = new CmdManagerApiClient(new HttpClient(handler) { BaseAddress = baseAddress }, session);

        await api.TryRefreshAsync();
        session.Set(FakeSession());
        await api.LogoutAsync();
        session.Set(FakeSession());
        await api.GetAuthConfigAsync();
        await api.RegisterAsync(new RegisterRequest("u", "p"));
        session.Set(FakeSession());
        await api.LoginAsync(new LoginRequest("u", "p"));
        session.Set(FakeSession());
        await api.MeAsync();
        await api.ListCommandsAsync("x");
        await api.GetCommandAsync(1);
        await api.CreateCommandAsync(new CommandUpsertRequest("a.cmd", "echo"));
        await api.UpdateCommandAsync(1, new CommandUpsertRequest("a.cmd", "echo"));
        await api.DeleteCommandAsync(1, "abc");
        await api.ReplaceCommandContentAsync(1, [1], "abc");
        await api.GetCommandContentAsync(1);
        await api.ListAssetsAsync();
        await api.GetAssetAsync(1);
        await api.UploadAssetAsync("a.bin", [1]);
        await api.ReplaceAssetContentAsync(1, [1]);
        await api.UpdateAssetAsync(1, new AssetUpdateRequest("b.bin"));
        await api.DeleteAssetAsync(1);
        await api.GetAssetContentAsync(1);
        await api.GetManifestAsync();
        await api.ImportAsync(new ImportRequest([]));

        Assert.Equal(22, recorder.Requests.Count); // one request per call above
        Assert.All(recorder.Requests, u => Assert.StartsWith("https://cmdmanager.socha3.com/api/", u.AbsoluteUri));
        Assert.DoesNotContain(recorder.Requests, u => u.AbsolutePath.Contains("/api/api/"));
        Assert.Contains(recorder.Requests, u => u.AbsoluteUri == "https://cmdmanager.socha3.com/api/auth/login");
        Assert.Contains(recorder.Requests, u => u.AbsoluteUri == "https://cmdmanager.socha3.com/api/library/manifest");
    }

    [Fact]
    public async Task Automatic_refresh_after_401_posts_to_api_auth_refresh()
    {
        var calls = new List<string>();
        var inner = new LambdaHandler(req =>
        {
            calls.Add(req.RequestUri!.AbsoluteUri);
            return req.RequestUri.AbsolutePath.EndsWith("/auth/refresh")
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        var session = new AuthSession();
        session.Set(FakeSession());
        var baseAddress = new Uri("https://cmdmanager.socha3.com/api/");
        using var http = new HttpClient(new AuthTokenHandler(session) { InnerHandler = inner }) { BaseAddress = baseAddress };

        await http.GetAsync("commands");

        Assert.Equal(["https://cmdmanager.socha3.com/api/commands", "https://cmdmanager.socha3.com/api/auth/refresh"], calls);
        Assert.Null(session.Current); // refresh rejected → session expired
    }

    private sealed class LambdaHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    [Theory]
    [InlineData("/auth/login")]
    [InlineData("https://evil.example/auth/login")]
    public void Leading_slash_or_absolute_request_uris_are_rejected(string uri)
    {
        Assert.Throws<InvalidOperationException>(() => CmdManagerApiClient.EnsureRelative(new Uri(uri, UriKind.RelativeOrAbsolute)));
    }

    [Fact]
    public void Relative_request_uri_is_accepted() =>
        CmdManagerApiClient.EnsureRelative(new Uri("commands/5/content", UriKind.Relative));

    [Theory]
    [InlineData("https://cmdmanager.socha3.com/api", "https://cmdmanager.socha3.com/api/")]
    [InlineData("https://cmdmanager.socha3.com/api/", "https://cmdmanager.socha3.com/api/")]
    [InlineData(" http://localhost:5066/api?x=1 ", "http://localhost:5066/api/")]
    public void NormalizeServerUrl_always_ends_with_slash(string input, string expected) =>
        Assert.Equal(expected, CmdManagerApiClient.NormalizeServerUrl(input).AbsoluteUri);

    [Fact]
    public void Default_server_url_is_the_api_application() =>
        Assert.Equal("https://cmdmanager.socha3.com/api/", CmdManagerApiClient.DefaultServerUrl);

    [Theory]
    [InlineData(null, "https://cmdmanager.socha3.com/api/")]
    [InlineData("", "https://cmdmanager.socha3.com/api/")]
    [InlineData("https://cmdmanager.socha3.com", "https://cmdmanager.socha3.com/api/")]
    [InlineData("https://CmdManager.socha3.com/", "https://cmdmanager.socha3.com/api/")]
    [InlineData("https://cmdmanager.socha3.com/api/", "https://cmdmanager.socha3.com/api/")]
    [InlineData("http://localhost:5066/api/", "http://localhost:5066/api/")]
    [InlineData("https://other.example/", "https://other.example/")]
    public void UpgradeServerUrl_moves_the_old_root_url_to_api(string? saved, string expected) =>
        Assert.Equal(expected, CmdManagerApiClient.UpgradeServerUrl(saved));

    [Theory]
    [InlineData("https://cmdmanager.socha3.com/api/commands/5", "https://cmdmanager.socha3.com/api/")]
    [InlineData("https://cmdmanager.socha3.com/api/auth/me", "https://cmdmanager.socha3.com/api/")]
    [InlineData("https://cmdmanager.socha3.com/api/library/manifest", "https://cmdmanager.socha3.com/api/")]
    [InlineData("http://localhost/commands", "http://localhost/")]
    [InlineData("https://host/a/b/assets/1/content", "https://host/a/b/")]
    [InlineData("https://host/unknown/path", "https://host/")]
    public void ApiBase_is_everything_before_the_first_route_segment(string request, string expected) =>
        Assert.Equal(expected, AuthTokenHandler.ApiBase(new Uri(request)).AbsoluteUri);
}
