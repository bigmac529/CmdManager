using Microsoft.AspNetCore.Http;

namespace CmdManager.Api.Hosting;

public static class PathBaseExtensions
{
    public const string ConfigKey = "Hosting:PathBase";
    public const string DefaultPathBase = "/api";

    /// <summary>
    /// Serves the app under <c>Hosting:PathBase</c> (default <c>/api</c>) without ever applying it twice.
    /// <list type="bullet">
    /// <item>IIS in-process (ANCM, IIS application "/api"): the request already arrives with PathBase=/api and
    /// Path=/health, so nothing is changed. A request for /api/api/health stays unmatched (404).</item>
    /// <item>Kestrel (local dev, tests): Path=/api/health is split into PathBase=/api + Path=/health, which gives the
    /// same public URLs as production. Paths without the prefix (e.g. /health) still work, like UsePathBase.</item>
    /// </list>
    /// Call before <c>UseRouting</c>.
    /// </summary>
    public static IApplicationBuilder UseConfiguredPathBase(this WebApplication app)
    {
        var pathBase = Normalize(app.Configuration[ConfigKey] ?? DefaultPathBase);
        if (!pathBase.HasValue)
            return app;

        return app.Use(async (context, next) =>
        {
            var request = context.Request;
            // A non-empty PathBase means the host (ANCM / IIS application) has already applied it.
            if (!request.PathBase.HasValue && request.Path.StartsWithSegments(pathBase, out var matched, out var remaining))
            {
                request.PathBase = matched;
                request.Path = remaining;
                try
                {
                    await next(context);
                }
                finally
                {
                    request.PathBase = PathString.Empty;
                    request.Path = matched.Add(remaining);
                }
                return;
            }

            await next(context);
        });
    }

    /// <summary>"api", "/api/", " /api " → "/api"; "", "/" → empty (disabled).</summary>
    public static PathString Normalize(string? value)
    {
        var v = (value ?? "").Trim().Trim('/');
        return v.Length == 0 ? PathString.Empty : new PathString("/" + v);
    }
}
