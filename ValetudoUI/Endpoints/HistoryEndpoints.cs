using ValetudoUI.Services;

namespace ValetudoUI.Endpoints;

public static class HistoryEndpoints
{
    public static IEndpointRouteBuilder MapHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        // Stored gzip bytes are streamed as-is; the browser inflates them transparently.
        app.MapGet("/api/history/{id:int}/map", async (int id, HistoryQueries queries, HttpContext ctx, CancellationToken ct) =>
        {
            var gzip = await queries.GetMapGzipAsync(id, ct);
            if (gzip is null || gzip.Length == 0) return Results.NotFound();

            ctx.Response.Headers.ContentEncoding = "gzip";
            ctx.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            return Results.Bytes(gzip, "application/json");
        });

        return app;
    }
}
