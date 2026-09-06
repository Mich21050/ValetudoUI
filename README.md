# ValetudoUI

A small self-hosted web UI for a Valetudo robot (built for a Dreame X40 Ultra) with the one thing
Valetudo does not do: **persistent cleaning history with a coverage map for every run**.

- Live map with robot position and path, room selection, zone drawing, go-to
- Start / pause / stop / dock, suction / water / mode presets, locate
- Dock actions (empty dust bin, wash / dry mops), consumables, lifetime statistics
- History: every cleaning run is recorded with duration, area, rooms, presets and the map with the
  path the robot actually drove. Stored in SQLite next to the app.
- Works on phone and desktop; can be added to the home screen.

Settings pages are intentionally not replicated; use Valetudo's own UI for those.

## Running

Requires the .NET 10 SDK.

1. Put the robot's address into `ValetudoUI/appsettings.json` (or `appsettings.Development.json`):

   ```json
   "Valetudo": { "BaseUrl": "http://192.168.1.50", "Username": null, "Password": null }
   ```

   Fill in `Username`/`Password` if Valetudo's HTTP basic auth is enabled.

2. `dotnet run --project ValetudoUI` and open <http://localhost:5282>.
   Use the `lan` launch profile (`dotnet run --project ValetudoUI --launch-profile lan`) to listen on
   all interfaces so a phone on the Wi-Fi can reach it.

The SQLite database (`valetudo-ui.db`) is created next to the project on first start.

## How it talks to the robot

Only Valetudo's HTTP API is used: REST for commands and two Server-Sent Events streams
(`/api/v2/robot/state/attributes/sse` and `/api/v2/robot/state/map/sse`) for live updates.
Valetudo allows at most five SSE clients and kicks the oldest one, so avoid running several
instances of this app against the same robot while the stock UI is open.

The browser fetches the map JSON directly from this app (`/api/map/live`), never through the Blazor
circuit; a stored run's map comes from `/api/history/{id}/map`.

## How history is recorded

Valetudo has no cleaning history and no "finished" event, so runs are derived from status
transitions: `cleaning` opens a run, `docked`/`idle`/`error` without the `resumable` flag closes it
(a resumable robot keeps the run open for `History:ResumableGraceMinutes`). During a run the path
from every map update is accumulated (firmware path resets start a new path segment instead of
drawing a bogus line), and in-progress runs are checkpointed every `History:CheckpointSeconds` so a
restart of the app mid-run resumes recording. At the end the map is stored with the accumulated path
substituted in, plus time and area from `CurrentStatisticsCapability`.

Runs shorter than `History:MinRunSeconds` with almost no path are discarded.

## Testing without the vacuum

A fake robot can be hosted inside the app (Development environment only):

```json
"Valetudo": { "BaseUrl": "http://localhost:5282/fake", "UseFakeRobot": true }
```

It serves `ValetudoUI/Fixtures/map.json` if present (capture one with
`curl http://ROBOT/api/v2/robot/state/map > ValetudoUI/Fixtures/map.json`), otherwise a synthetic
four-room flat. Start a scripted run with

```
curl -X POST 'http://localhost:5282/fake/scenario?seconds=60&pathResetAt=20&segments=1,2'
curl -X POST 'http://localhost:5282/fake/scenario/error?message=Stuck'
```

## Useful checks against the real robot

```
curl -s http://ROBOT/api/v2/robot | jq
curl -s http://ROBOT/api/v2/robot/capabilities | jq
curl -s http://ROBOT/api/v2/robot/state/attributes | jq '.[].__class'
curl -sN http://ROBOT/api/v2/robot/state/attributes/sse | head -c 2000
curl -s http://ROBOT/api/v2/robot/capabilities/MapSegmentationCapability/properties
curl -s http://ROBOT/api/v2/robot/capabilities/ConsumableMonitoringCapability | jq
```

If the robot icon points the wrong way, set `angleOffset` in the options passed to the renderer in
`Components/Shared/MapView.razor` (Valetudo documents 0° as north, but this was not verified on the X40).
