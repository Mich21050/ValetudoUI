using ValetudoUI.Services;

namespace ValetudoUI.Endpoints;

public static class MapEndpoints
{
    public static IEndpointRouteBuilder MapMapEndpoints(this IEndpointRouteBuilder app)
    {
        // The live map is fetched by the browser directly (never pushed through the Blazor circuit).
        app.MapGet("/api/map/live", (HttpContext ctx, RobotMonitorService monitor) =>
        {
            var map = monitor.Map;
            if (map is null) return Results.NotFound();

            var etag = $"\"v{map.Version}\"";
            ctx.Response.Headers.ETag = etag;
            ctx.Response.Headers.CacheControl = "no-cache";
            if (ctx.Request.Headers.IfNoneMatch.Any(v => v == etag))
                return Results.StatusCode(StatusCodes.Status304NotModified);

            return Results.Text(map.Json, "application/json");
        });

        app.MapGet("/api/map/live/meta", (RobotMonitorService monitor) =>
            monitor.Map is { } map
                ? Results.Ok(new { version = map.Version, nonce = map.Nonce, updatedUtc = map.UpdatedUtc })
                : Results.NotFound());

        return app;
    }
}
