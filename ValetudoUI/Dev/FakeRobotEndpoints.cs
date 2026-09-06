using System.Text.Json;

namespace ValetudoUI.Dev;

public static class FakeRobotEndpoints
{
    public static IEndpointRouteBuilder MapFakeRobot(this IEndpointRouteBuilder app)
    {
        app.ServiceProvider.GetRequiredService<FakeRobot>().Initialise();

        var api = app.MapGroup("/fake/api/v2");

        api.MapGet("/robot", () => Results.Ok(new
        {
            manufacturer = "Dreame",
            modelName = "X40 Ultra (fake)",
            modelDetails = new { supportedAttachments = new[] { "mop" }, supportedDockComponents = new[] { "water_tank_clean", "water_tank_dirty", "detergent", "dustbag" } },
            implementation = "FakeValetudoRobot"
        }));

        api.MapGet("/robot/capabilities", () => Results.Ok(FakeRobot.CapabilityList));
        api.MapGet("/robot/state/attributes", (FakeRobot r) => Results.Text(r.AttributesJson(), "application/json"));
        api.MapGet("/robot/state/map", (FakeRobot r) => Results.Text(r.MapJson(), "application/json"));
        api.MapGet("/robot/state/attributes/sse", (HttpContext ctx, FakeRobot r, CancellationToken ct) => StreamAsync(ctx, r, false, ct));
        api.MapGet("/robot/state/map/sse", (HttpContext ctx, FakeRobot r, CancellationToken ct) => StreamAsync(ctx, r, true, ct));

        api.MapGet("/robot/capabilities/{cap}", (string cap, FakeRobot r) => cap switch
        {
            "MapSegmentationCapability" => Results.Text(r.SegmentsJson(), "application/json"),
            "ConsumableMonitoringCapability" => Results.Ok(r.ConsumablesJson()),
            "CurrentStatisticsCapability" => Results.Ok(r.CurrentStatistics()),
            "TotalStatisticsCapability" => Results.Ok(r.TotalStatistics()),
            _ => Results.BadRequest()
        });

        api.MapGet("/robot/capabilities/{cap}/properties", (string cap) => cap switch
        {
            "MapSegmentationCapability" => Results.Ok(new { iterationCount = new { min = 1, max = 4 }, customOrderSupport = true }),
            "ZoneCleaningCapability" => Results.Ok(new { zoneCount = new { min = 1, max = 4 }, iterationCount = new { min = 1, max = 4 } }),
            "CurrentStatisticsCapability" => Results.Ok(new { availableStatistics = new[] { "time", "area" } }),
            "TotalStatisticsCapability" => Results.Ok(new { availableStatistics = new[] { "time", "area", "count" } }),
            _ => Results.Ok(new { })
        });

        api.MapGet("/robot/capabilities/{cap}/presets", (string cap) => cap switch
        {
            "FanSpeedControlCapability" => Results.Ok(new[] { "low", "medium", "high", "max" }),
            "WaterUsageControlCapability" => Results.Ok(new[] { "min", "low", "medium", "high", "max" }),
            "OperationModeControlCapability" => Results.Ok(new[] { "vacuum_and_mop", "mop", "vacuum", "vacuum_then_mop" }),
            _ => Results.BadRequest()
        });

        api.MapPut("/robot/capabilities/{cap}/preset", async (string cap, HttpContext ctx, FakeRobot r) =>
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var name = body.GetProperty("name").GetString() ?? "";
            var type = cap switch
            {
                "FanSpeedControlCapability" => "fan_speed",
                "WaterUsageControlCapability" => "water_grade",
                "OperationModeControlCapability" => "operation_mode",
                _ => null
            };
            if (type is null) return Results.BadRequest();
            r.SetPreset(type, name);
            return Results.Ok();
        });

        api.MapPut("/robot/capabilities/ConsumableMonitoringCapability/{type}/{subType}", (string type, string subType, FakeRobot r) =>
        {
            r.ResetConsumable(type, subType);
            return Results.Ok();
        });

        api.MapPut("/robot/capabilities/{cap}", async (string cap, HttpContext ctx, FakeRobot r) =>
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var action = body.TryGetProperty("action", out var a) ? a.GetString() : null;
            switch (cap, action)
            {
                case ("BasicControlCapability", "start"):
                    if (r.Status == "paused") r.Resume();
                    else r.StartScenario(90, null, null, "none");
                    return Results.Ok();
                case ("BasicControlCapability", "pause"): r.Pause(); return Results.Ok();
                case ("BasicControlCapability", "stop"): r.Stop(); return Results.Ok();
                case ("BasicControlCapability", "home"): r.Home(); return Results.Ok();
                case ("MapSegmentationCapability", "start_segment_action"):
                    var ids = body.GetProperty("segment_ids").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                    if (ids.Count == 0 || ids.Any(id => !"1234".Contains(id)))
                        return Results.Json("Invalid segment id", statusCode: 500);
                    r.StartScenario(60, null, ids, "segment");
                    return Results.Ok();
                case ("ZoneCleaningCapability", "clean"): r.StartScenario(40, null, null, "zone"); return Results.Ok();
                case ("GoToLocationCapability", "goto"): r.GoTo(); return Results.Ok();
                case ("LocateCapability", "locate"): return Results.Ok();
                case ("AutoEmptyDockManualTriggerCapability", "trigger"): r.SetDockStatus("emptying"); return Results.Ok();
                case ("MopDockCleanManualTriggerCapability", "start"): r.SetDockStatus("cleaning"); return Results.Ok();
                case ("MopDockCleanManualTriggerCapability", "stop"): r.SetDockStatus("idle"); return Results.Ok();
                case ("MopDockDryManualTriggerCapability", "start"): r.SetDockStatus("drying"); return Results.Ok();
                case ("MopDockDryManualTriggerCapability", "stop"): r.SetDockStatus("idle"); return Results.Ok();
                default:
                    return Results.BadRequest();
            }
        });

        // Test hooks (not part of Valetudo's API).
        app.MapPost("/fake/scenario", (int? seconds, int? pathResetAt, string? segments, FakeRobot r) =>
        {
            var ids = string.IsNullOrWhiteSpace(segments) ? null : segments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            r.StartScenario(seconds ?? 60, pathResetAt, ids, ids is null ? "none" : "segment");
            return Results.Ok(new { started = true, seconds = seconds ?? 60, pathResetAt, segments = ids });
        });
        app.MapPost("/fake/scenario/error", (string? message, FakeRobot r) =>
        {
            r.RaiseError(message ?? "Robot is stuck (fake)");
            return Results.Ok();
        });

        return app;
    }

    private static async Task StreamAsync(HttpContext ctx, FakeRobot robot, bool map, CancellationToken ct)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        await ctx.Response.StartAsync(ct);

        var channel = robot.Subscribe(map);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool hasData;
                try
                {
                    hasData = await channel.Reader.WaitToReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
                }
                catch (TimeoutException)
                {
                    hasData = false;
                }

                if (hasData && channel.Reader.TryRead(out var item))
                    await ctx.Response.WriteAsync($"event: {item.Event}\ndata: {item.Data}\n\n", ct);
                else
                    await ctx.Response.WriteAsync(": sse-keep-alive\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            robot.Unsubscribe(channel);
        }
    }
}
