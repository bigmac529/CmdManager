using System.Net;
using System.Text;
using CmdManager.Core.Contracts;
using CmdManager.Core.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.DependencyInjection;

namespace CmdManager.Api.Tests;

/// <summary>
/// Pins every HTTP method + relative URL the desktop client sends (CmdManagerApiClient) to an endpoint the API
/// actually maps for that method, so a client/API route or verb mismatch (405/404) fails here instead of in the app.
/// </summary>
public class ClientRouteContractTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Method, string Path)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null", Encoding.UTF8, "application/json") });
        }
    }

    private static AuthResponse FakeSession() => new("access", DateTime.UtcNow.AddHours(1), "refresh",
        DateTime.UtcNow.AddDays(1), new UserDto(1, "u", null, DateTime.UtcNow));

    private static async Task<List<(string Method, string Path)>> RecordClientCallsAsync()
    {
        var recorder = new RecordingHandler();
        var session = new AuthSession();
        session.Set(FakeSession());
        var handler = new AuthTokenHandler(session, ApiFactory.ApiBase) { InnerHandler = recorder };
        using var api = new CmdManagerApiClient(new HttpClient(handler) { BaseAddress = ApiFactory.ApiBase }, session);

        await api.TryRefreshAsync();
        session.Set(FakeSession());
        await api.LogoutAsync();
        session.Set(FakeSession());
        await api.GetAuthConfigAsync();
        await api.RegisterAsync(new RegisterRequest("u", "p"));
        await api.LoginAsync(new LoginRequest("u", "p"));
        session.Set(FakeSession());
        await api.MeAsync();
        await api.ListCommandsAsync("x");
        await api.GetCommandAsync(1);
        await api.CreateCommandAsync(new CommandUpsertRequest("new.csx", "Console.WriteLine(1);")); // New… (.csx + .cmd)
        await api.UpdateCommandAsync(1, new CommandUpsertRequest("new.csx", "Console.WriteLine(2);")); // editor Ctrl+S / rename
        await api.DeleteCommandAsync(1, "abc");
        await api.ReplaceCommandContentAsync(1, [1], "abc"); // push local edits
        await api.GetCommandContentAsync(1);
        await api.ListAssetsAsync();
        await api.GetAssetAsync(1);
        await api.UploadAssetAsync("CSXScripts/CSXScripts.dll", [1]);
        await api.ReplaceAssetContentAsync(1, [1], "abc"); // push a changed dll
        await api.UpdateAssetAsync(1, new AssetUpdateRequest("CSXScripts/Other.dll"));
        await api.DeleteAssetAsync(1, "abc");
        await api.GetAssetContentAsync(1);
        await api.GetManifestAsync();
        await api.ImportAsync(new ImportRequest([]));
        return recorder.Requests;
    }

    [Fact]
    public async Task Every_client_request_matches_an_api_endpoint_for_its_method()
    {
        var requests = await RecordClientCallsAsync();
        Assert.Equal(22, requests.Count);
        Assert.Contains(("PUT", "/api/commands/1"), requests);
        Assert.Contains(("PUT", "/api/assets/1/content"), requests);
        Assert.Contains(("POST", "/api/assets"), requests);

        using var factory = new ApiFactory();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => (Matcher: new TemplateMatcher(new RouteTemplate(e.RoutePattern), new RouteValueDictionary()),
                          Methods: e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []))
            .ToList();

        var unmatched = new List<string>();
        foreach (var (method, path) in requests)
        {
            Assert.StartsWith("/api/", path);
            var appPath = new PathString(path["/api".Length..].TrimEnd('/') is { Length: > 0 } p ? p : "/");
            var byPath = endpoints.Where(e => e.Matcher.TryMatch(appPath, new RouteValueDictionary())).ToList();
            if (byPath.Count == 0)
                unmatched.Add($"{method} {path}: no route (404)");
            else if (!byPath.Any(e => e.Methods.Count == 0 || e.Methods.Contains(method)))
                unmatched.Add($"{method} {path}: route exists but not for {method} (405; mapped: {string.Join(",", byPath.SelectMany(e => e.Methods).Distinct())})");
        }
        Assert.True(unmatched.Count == 0, string.Join(Environment.NewLine, unmatched));
    }
}
