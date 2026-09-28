using System.Xml.Linq;

namespace CmdManager.Api.Tests;

/// <summary>
/// The API's web.config (deployed to the IIS application /api by deploy-api.yml). IIS-level problems never reach the
/// in-memory test server, so the config itself is pinned here.
/// </summary>
public class WebConfigTests
{
    private static XElement SystemWebServer()
    {
        var doc = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "api-web.config"));
        var location = doc.Root!.Elements("location").Single(l => (string?)l.Attribute("path") == ".");
        Assert.Equal("false", (string?)location.Attribute("inheritInChildApplications"));
        return location.Element("system.webServer")!;
    }

    [Fact]
    public void WebDav_module_and_handler_are_removed_so_PUT_and_DELETE_reach_the_app()
    {
        // With IIS "WebDAV Publishing" installed, PUT/DELETE got "405 - HTTP verb used to access this page is not
        // allowed" (IIS HTML, Allow: GET, HEAD, OPTIONS, TRACE): saving, renaming and deleting commands/assets failed.
        var ws = SystemWebServer();
        Assert.Contains(ws.Element("modules")!.Elements("remove"), e => (string?)e.Attribute("name") == "WebDAVModule");

        var handlers = ws.Element("handlers")!.Elements().ToList();
        var removeWebDav = handlers.FindIndex(e => e.Name == "remove" && (string?)e.Attribute("name") == "WebDAV");
        var aspNetCore = handlers.FindIndex(e => e.Name == "add" && (string?)e.Attribute("name") == "aspNetCore");
        Assert.True(removeWebDav >= 0, "handlers must <remove name=\"WebDAV\" />");
        Assert.True(aspNetCore > removeWebDav, "aspNetCore handler must be added after the WebDAV handler is removed");
        Assert.Equal("*", (string?)handlers[aspNetCore].Attribute("verb"));
    }
}
