using System.Net;
using System.Net.Http.Json;
using CmdManager.Core;
using CmdManager.Core.Contracts;
using CmdManager.Core.Http;

namespace CmdManager.Api.Tests;

public class LibraryTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Command_crud_roundtrip()
    {
        var api = await factory.RegisteredClientAsync();
        var text = "start \"\" \"https://www.google.com/search?q=%*\"\r\nexit\r\n";

        var created = await api.CreateCommandAsync(new CommandUpsertRequest("g.cmd", text, Description: "Google it", Tags: ["web", "search", "WEB"]));
        Assert.Equal("g.cmd", created.RelativePath);
        Assert.Equal("", created.Folder);
        Assert.Equal(CommandKind.Cmd, created.Kind);
        Assert.False(created.IsBinary);
        Assert.Equal(ContentHash.Sha256Hex(text), created.Sha256);
        Assert.Equal(["web", "search"], created.Tags);

        var fetched = await api.GetCommandAsync(created.Id);
        Assert.Equal(text, fetched.TextContent);

        var list = await api.ListCommandsAsync();
        Assert.Single(list);
        Assert.Single(await api.ListCommandsAsync("google"));
        Assert.Empty(await api.ListCommandsAsync("nomatch"));

        var bytes = await api.GetCommandContentAsync(created.Id);
        Assert.Equal(TextContent.ToBytes(text), bytes);

        // update content + rename into a folder
        var updated = await api.UpdateCommandAsync(created.Id, new CommandUpsertRequest("web/google.cmd", "@echo hi\r\n", Description: "renamed", ExpectedSha256: created.Sha256));
        Assert.Equal("web/google.cmd", updated.RelativePath);
        Assert.Equal("web", updated.Folder);
        Assert.Equal("google.cmd", updated.Name);
        Assert.True(updated.UpdatedUtc >= created.UpdatedUtc);

        await api.DeleteCommandAsync(created.Id);
        var ex = await Assert.ThrowsAsync<ApiException>(() => api.GetCommandAsync(created.Id));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task Command_names_are_unique_per_user_case_insensitively()
    {
        var api = await factory.RegisteredClientAsync();
        await api.CreateCommandAsync(new CommandUpsertRequest("Work.cmd", "exit"));
        var ex = await Assert.ThrowsAsync<ApiException>(() => api.CreateCommandAsync(new CommandUpsertRequest("WORK.CMD", "exit")));
        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);

        // same base name with another extension is fine (MkNewCmd creates x.cmd + x.csx)
        await api.CreateCommandAsync(new CommandUpsertRequest("work.csx", "Console.WriteLine(1);"));

        // a path used by an asset is taken too
        await api.UploadAssetAsync("tool.exe", [1, 2, 3]);
        var ex2 = await Assert.ThrowsAsync<ApiException>(() => api.CreateCommandAsync(new CommandUpsertRequest("TOOL.exe", BinaryContent: [9])));
        Assert.Equal(HttpStatusCode.Conflict, ex2.StatusCode);
    }

    [Theory]
    [InlineData("../evil.cmd")]
    [InlineData("C:/Windows/evil.cmd")]
    [InlineData("con.cmd")]
    [InlineData("bad|name.cmd")]
    [InlineData("")]
    public async Task Invalid_paths_are_rejected(string path)
    {
        var api = await factory.RegisteredClientAsync();
        var ex = await Assert.ThrowsAsync<ApiException>(() => api.CreateCommandAsync(new CommandUpsertRequest(path, "exit")));
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
    }

    [Fact]
    public async Task Update_with_stale_hash_returns_conflict()
    {
        var api = await factory.RegisteredClientAsync();
        var c = await api.CreateCommandAsync(new CommandUpsertRequest("x.cmd", "v1"));
        await api.UpdateCommandAsync(c.Id, new CommandUpsertRequest("x.cmd", "v2", ExpectedSha256: c.Sha256));

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.UpdateCommandAsync(c.Id, new CommandUpsertRequest("x.cmd", "v3", ExpectedSha256: c.Sha256)));
        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);

        var ex2 = await Assert.ThrowsAsync<ApiException>(() => api.ReplaceCommandContentAsync(c.Id, TextContent.ToBytes("v4"), c.Sha256));
        Assert.Equal(HttpStatusCode.Conflict, ex2.StatusCode);

        var ex3 = await Assert.ThrowsAsync<ApiException>(() => api.DeleteCommandAsync(c.Id, c.Sha256));
        Assert.Equal(HttpStatusCode.Conflict, ex3.StatusCode);

        var current = await api.GetCommandAsync(c.Id);
        Assert.Equal("v2", current.TextContent);
        var replaced = await api.ReplaceCommandContentAsync(c.Id, TextContent.ToBytes("v5"), current.Sha256);
        Assert.Equal(ContentHash.Sha256Hex("v5"), replaced.Sha256);
        await api.DeleteCommandAsync(c.Id, replaced.Sha256);
    }

    [Fact]
    public async Task Binary_command_roundtrips_exact_bytes()
    {
        var api = await factory.RegisteredClientAsync();
        byte[] lnk = [0x4C, 0x00, 0x00, 0x00, 0x01, 0x14, 0x02, 0x00, 0xFF, 0xFE];
        var c = await api.CreateCommandAsync(new CommandUpsertRequest("7z.lnk", BinaryContent: lnk));
        Assert.True(c.IsBinary);
        Assert.Equal(CommandKind.Lnk, c.Kind);
        Assert.Null((await api.GetCommandAsync(c.Id)).TextContent);
        Assert.Equal(lnk, await api.GetCommandContentAsync(c.Id));
    }

    [Fact]
    public async Task Asset_upload_download_replace_rename_delete()
    {
        var api = await factory.RegisteredClientAsync();
        var bytes = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();
        var a = await api.UploadAssetAsync("CSXScripts/CSXScripts.dll", bytes, "library dll");
        Assert.Equal("CSXScripts", a.Folder);
        Assert.Equal("CSXScripts.dll", a.Name);
        Assert.Equal(5000, a.Size);
        Assert.Equal(ContentHash.Sha256Hex(bytes), a.Sha256);

        Assert.Equal(bytes, await api.GetAssetContentAsync(a.Id));
        Assert.Single(await api.ListAssetsAsync("csxscripts"));

        var v2 = new byte[] { 1, 2, 3 };
        var replaced = await api.ReplaceAssetContentAsync(a.Id, v2, a.Sha256);
        Assert.Equal(3, replaced.Size);
        var stale = await Assert.ThrowsAsync<ApiException>(() => api.ReplaceAssetContentAsync(a.Id, v2, a.Sha256));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var renamed = await api.UpdateAssetAsync(a.Id, new AssetUpdateRequest("bin2/CSXScripts.dll", "moved"));
        Assert.Equal("bin2/CSXScripts.dll", renamed.RelativePath);

        await api.DeleteAssetAsync(a.Id);
        Assert.Empty(await api.ListAssetsAsync());
    }

    [Fact]
    public async Task Upload_over_the_size_limit_is_rejected()
    {
        var api = await factory.RegisteredClientAsync();
        var big = new byte[1024 * 1024 + 1]; // factory sets Library:MaxFileBytes = 1 MB
        var ex = await Assert.ThrowsAsync<ApiException>(() => api.UploadAssetAsync("big.bin", big));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, ex.StatusCode);

        var ex2 = await Assert.ThrowsAsync<ApiException>(() => api.CreateCommandAsync(new CommandUpsertRequest("big.exe", BinaryContent: big)));
        Assert.Equal(HttpStatusCode.BadRequest, ex2.StatusCode);
    }

    [Fact]
    public async Task Manifest_lists_commands_and_assets_with_hashes()
    {
        var api = await factory.RegisteredClientAsync();
        var c = await api.CreateCommandAsync(new CommandUpsertRequest("RDPs/work.rdp", "full address:s:work"));
        var a = await api.UploadAssetAsync("SysTools/tool.dll", [1, 2]);

        var manifest = await api.GetManifestAsync();
        Assert.Equal(2, manifest.Items.Count);
        var mc = Assert.Single(manifest.Items, i => i.Type == LibraryItemType.Command);
        Assert.Equal("RDPs/work.rdp", mc.RelativePath);
        Assert.Equal(c.Sha256, mc.Sha256);
        Assert.Equal(c.Id, mc.Id);
        var ma = Assert.Single(manifest.Items, i => i.Type == LibraryItemType.Asset);
        Assert.Equal(a.Sha256, ma.Sha256);
        Assert.Equal(2, ma.Size);
    }

    [Fact]
    public async Task Bulk_import_creates_updates_and_reports_per_item()
    {
        var api = await factory.RegisteredClientAsync();
        var r1 = await api.ImportAsync(new ImportRequest(
        [
            new ImportItem("g.cmd", Text: "start \"\" \"x\"\r\nexit"),
            new ImportItem("RDPs/server.rdp", Content: [0xFF, 0xFE, 0x66, 0x00]),   // UTF-16 → binary command
            new ImportItem("CSXScripts/Settings.json", Text: "{ }"),                 // text → command (Other)
            new ImportItem("CSXScripts/Newtonsoft.Json.dll", Content: [0x4D, 0x5A, 0x00]), // binary → asset
            new ImportItem("../escape.cmd", Text: "x"),
            new ImportItem("G.CMD", Text: "dup")
        ]));
        Assert.Equal(4, r1.Created);
        Assert.Equal(2, r1.Errors.Count);
        Assert.Contains(r1.Items!, i => i.RelativePath == "CSXScripts/Newtonsoft.Json.dll" && i.Type == LibraryItemType.Asset && i.Id > 0);

        var commands = await api.ListCommandsAsync();
        Assert.Equal(3, commands.Count);
        Assert.True(commands.Single(c => c.Name == "server.rdp").IsBinary);
        Assert.Equal(CommandKind.Other, commands.Single(c => c.Name == "Settings.json").Kind);
        Assert.Single(await api.ListAssetsAsync());

        var r2 = await api.ImportAsync(new ImportRequest(
        [
            new ImportItem("g.cmd", Text: "start \"\" \"x\"\r\nexit"),  // unchanged
            new ImportItem("CSXScripts/Settings.json", Text: "{ \"a\": 1 }") // changed
        ], Overwrite: false));
        Assert.Equal(1, r2.Unchanged);
        Assert.Equal(1, r2.Skipped);

        var r3 = await api.ImportAsync(new ImportRequest([new ImportItem("CSXScripts/Settings.json", Text: "{ \"a\": 1 }")]));
        Assert.Equal(1, r3.Updated);
    }

    [Fact]
    public async Task Export_returns_zip()
    {
        var api = await factory.RegisteredClientAsync();
        await api.CreateCommandAsync(new CommandUpsertRequest("a.cmd", "exit"));
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", api.Session!.AccessToken);
        var resp = await http.GetAsync("/api/library/export");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/zip", resp.Content.Headers.ContentType!.MediaType);
        using var zip = new System.IO.Compression.ZipArchive(await resp.Content.ReadAsStreamAsync());
        Assert.Contains(zip.Entries, e => e.FullName == "a.cmd");
    }
}
