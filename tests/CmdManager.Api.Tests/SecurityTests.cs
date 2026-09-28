using System.Net;
using System.Net.Http.Json;
using System.Text;
using CmdManager.Core.Contracts;
using CmdManager.Core.Http;

namespace CmdManager.Api.Tests;

/// <summary>Every library endpoint needs a bearer token, and users never see each other's files.</summary>
public class SecurityTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    public static TheoryData<string, string> LibraryEndpoints => new()
    {
        { "GET", "/api/auth/me" },
        { "POST", "/api/auth/logout" },
        { "GET", "/api/commands" },
        { "GET", "/api/commands/1" },
        { "GET", "/api/commands/1/content" },
        { "POST", "/api/commands" },
        { "PUT", "/api/commands/1" },
        { "PUT", "/api/commands/1/content" },
        { "DELETE", "/api/commands/1" },
        { "GET", "/api/assets" },
        { "GET", "/api/assets/1" },
        { "GET", "/api/assets/1/content" },
        { "POST", "/api/assets" },
        { "PUT", "/api/assets/1" },
        { "PUT", "/api/assets/1/content" },
        { "DELETE", "/api/assets/1" },
        { "GET", "/api/library/manifest" },
        { "POST", "/api/library/import" },
        { "GET", "/api/library/export" },
        { "GET", "/api/some/unmapped/route" }
    };

    [Theory]
    [MemberData(nameof(LibraryEndpoints))]
    public async Task Endpoint_returns_401_without_token(string method, string url)
    {
        var http = factory.CreateClient();
        using var req = new HttpRequestMessage(new HttpMethod(method), url);
        if (method is "POST" or "PUT")
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/api/auth/config")]
    public async Task Anonymous_endpoints_are_reachable(string url)
    {
        var resp = await factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Token_signed_with_another_key_is_rejected()
    {
        // header.payload.signature with a bogus HMAC
        const string forged = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxIiwiaXNzIjoiQ21kTWFuYWdlciIsImF1ZCI6IkNtZE1hbmFnZXIuQ2xpZW50IiwiZXhwIjo0MTAyNDQ0ODAwfQ.c2lnbmF0dXJlLW5vdC12YWxpZA";
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", forged);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/commands")).StatusCode);
    }

    [Fact]
    public async Task User_A_cannot_read_or_modify_user_B_files()
    {
        var alice = await factory.RegisteredClientAsync();
        var bob = await factory.RegisteredClientAsync();

        var bobCmd = await bob.CreateCommandAsync(new CommandUpsertRequest("secret.cmd", "echo bob"));
        var bobAsset = await bob.UploadAssetAsync("SysTools/bob.exe", [1, 2, 3]);

        // Alice sees nothing of Bob's
        Assert.Empty(await alice.ListCommandsAsync());
        Assert.Empty(await alice.ListAssetsAsync());
        Assert.Empty((await alice.GetManifestAsync()).Items);

        async Task Is404(Func<Task> call) => Assert.Equal(HttpStatusCode.NotFound, (await Assert.ThrowsAsync<ApiException>(call)).StatusCode);

        await Is404(() => alice.GetCommandAsync(bobCmd.Id));
        await Is404(() => alice.GetCommandContentAsync(bobCmd.Id));
        await Is404(() => alice.UpdateCommandAsync(bobCmd.Id, new CommandUpsertRequest("secret.cmd", "echo pwned")));
        await Is404(() => alice.ReplaceCommandContentAsync(bobCmd.Id, [1]));
        await Is404(() => alice.DeleteCommandAsync(bobCmd.Id));
        await Is404(() => alice.GetAssetAsync(bobAsset.Id));
        await Is404(() => alice.GetAssetContentAsync(bobAsset.Id));
        await Is404(() => alice.ReplaceAssetContentAsync(bobAsset.Id, [9]));
        await Is404(() => alice.UpdateAssetAsync(bobAsset.Id, new AssetUpdateRequest("x.exe")));
        await Is404(() => alice.DeleteAssetAsync(bobAsset.Id));

        // Alice can use the same paths in her own library; import never touches Bob's rows
        await alice.CreateCommandAsync(new CommandUpsertRequest("secret.cmd", "echo alice"));
        var r = await alice.ImportAsync(new ImportRequest([new ImportItem("SysTools/bob.exe", Content: [7, 7])]));
        Assert.Equal(1, r.Created);

        // Bob's data is untouched
        Assert.Equal("echo bob", (await bob.GetCommandAsync(bobCmd.Id)).TextContent);
        Assert.Equal(new byte[] { 1, 2, 3 }, await bob.GetAssetContentAsync(bobAsset.Id));
        Assert.Equal(2, (await bob.GetManifestAsync()).Items.Count);
    }

    [Fact]
    public async Task Client_supplied_user_id_is_ignored()
    {
        var alice = await factory.RegisteredClientAsync();
        var bob = await factory.RegisteredClientAsync();
        await bob.CreateCommandAsync(new CommandUpsertRequest("bob.cmd", "x"));

        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", alice.Session!.AccessToken);
        var list = await http.GetFromJsonAsync<List<CommandSummaryDto>>($"/api/commands?userId={bob.Session!.User.Id}", CmdManagerJson.Options);
        Assert.Empty(list!);
    }

    [Fact]
    public async Task Expired_session_raises_Expired_and_clears_tokens()
    {
        var api = await factory.RegisteredClientAsync();
        var expired = false;
        api.AuthSession.Expired += (_, _) => expired = true;
        // Break both tokens: the handler's refresh attempt fails → session cleared.
        var s = api.Session!;
        api.AuthSession.Restore(s with { AccessToken = "bogus", RefreshToken = "also-bogus" });

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.ListCommandsAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.True(expired);
        Assert.Null(api.Session);
    }

    [Fact]
    public async Task Handler_refreshes_a_rejected_access_token_transparently()
    {
        var api = await factory.RegisteredClientAsync();
        var s = api.Session!;
        api.AuthSession.Restore(s with { AccessToken = "bogus" }); // refresh token still valid

        var list = await api.ListCommandsAsync();
        Assert.Empty(list);
        Assert.NotEqual("bogus", api.Session!.AccessToken);
        Assert.NotEqual(s.RefreshToken, api.Session.RefreshToken);
    }
}
