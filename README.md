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

### Phases (vacuum then mop)

A run is split into phases at every dock visit the robot makes while its task is still resumable:
the mop wash and pad change between the vacuum and mop passes of a "vacuum then mop" cycle, the
periodic mop washes during a mop clean, or a mid-task charge. Each phase records its start/end,
duration, the area gained (from `CurrentStatisticsCapability`) and a kind:

| Situation | Kind |
|---|---|
| operation mode `vacuum` / `mop` / `vacuum_and_mop` | that mode |
| anything else (sequential mode or unknown) and the mop is reported detached | `vacuum` |
| anything else, first phase | `vacuum` |
| anything else, later phases | `mop` |

The stored map tags every path entity with `metaData.phase` and `metaData.phaseKind`; the map
renderer colours paths by kind (`--phase-*` variables in `app.css`) and the run detail page lets you
show or hide each phase.

## Telegram notifications

The app can post to a Telegram chat when a run finishes and when the robot reports an error. It calls
the Bot API directly (`sendMessage`), no library involved.

1. Create a bot with [@BotFather](https://t.me/BotFather) and copy the token.
2. Send the bot any message (or add it to a group), then open
   `https://api.telegram.org/bot<TOKEN>/getUpdates` and read `message.chat.id` from the response.
3. Put both into `appsettings.Development.json` (or `dotnet user-secrets`), not into `appsettings.json`:

```json
"Telegram": { "BotToken": "123456:ABC...", "ChatId": "123456789", "NotifyRunCompleted": true, "NotifyErrors": true, "Silent": false }
```

`curl -X POST http://localhost:5282/api/telegram/test` sends a test message (503 with Telegram's error
text if it fails). Errors are reported once per distinct error, not on every state update. Without a
token and chat id the feature stays off.

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
# vacuum-then-mop: docks (resumable) at 20 s, mops attached afterwards -> two phases
curl -X POST 'http://localhost:5282/fake/scenario?seconds=60&washAt=20&thenMop=true&segments=1,2'
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
