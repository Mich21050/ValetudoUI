using ValetudoUI.Services;

namespace ValetudoUI.Endpoints;

public static class TelegramEndpoints
{
    public static IEndpointRouteBuilder MapTelegramEndpoints(this IEndpointRouteBuilder app)
    {
        // curl -X POST http://localhost:5282/api/telegram/test
        app.MapPost("/api/telegram/test", async (TelegramNotifier notifier, CancellationToken ct) =>
        {
            var error = await notifier.SendTestAsync(ct);
            return error is null
                ? Results.Ok(new { sent = true })
                : Results.Json(new { sent = false, error }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }).DisableAntiforgery();
        return app;
    }
}
