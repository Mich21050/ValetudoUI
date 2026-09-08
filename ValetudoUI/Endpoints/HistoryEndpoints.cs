using ValetudoUI.Data;
using ValetudoUI.Services;

namespace ValetudoUI.Endpoints;

public static class HistoryEndpoints
{
    public static IEndpointRouteBuilder MapHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        // Stored gzip bytes are streamed as-is; the browser inflates them transparently.
        // The map is rewritten at every checkpoint while a run is in progress and once more when it completes,
        // so only a finished run's map may be cached for good; otherwise the browser has to revalidate.
        app.MapGet("/api/history/{id:int}/map", async (int id, HistoryQueries queries, HttpContext ctx, CancellationToken ct) =>
        {
            var blob = await queries.GetMapGzipAsync(id, ct);
            if (blob is null || blob.MapJsonGzip.Length == 0) return Results.NotFound();

            var etag = $"\"{blob.Version}\"";
            ctx.Response.Headers.ETag = etag;
            ctx.Response.Headers.CacheControl = blob.State == RunState.InProgress
                ? "private, no-cache"
                : "private, max-age=31536000, immutable";
            if (ctx.Request.Headers.IfNoneMatch.Any(v => v == etag))
                return Results.StatusCode(StatusCodes.Status304NotModified);

            ctx.Response.Headers.ContentEncoding = "gzip";
            return Results.Bytes(blob.MapJsonGzip, "application/json");
        });

        return app;
    }
}
