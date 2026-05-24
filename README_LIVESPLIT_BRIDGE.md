# TournamentTimer LiveSplit Bridge

This guide is for setups where Start/Split events should come from LiveSplit or a LiveSplit autosplitter.

If runners press Start/Split manually in Runner UI, this bridge is not required.

LiveSplit bridge is useful as a fallback for games where the autosplitter works only through LiveSplit, including cases where headless ASL tooling is not enough.

## How it works

Flow:

```text
LiveSplit / autosplitter
-> TournamentTimer Bridge plugin in LiveSplit
-> Runner UI on the runner PC
-> TournamentTimer Server
-> Admin panel and overlay
```

LiveSplit does not send events directly to the server. It sends events to Runner UI through the local bridge. Runner UI then logs and syncs them to the server.

Runner UI must be open and connected.

## Plugin file

The runner package and full package include:

```text
livesplit-plugin/LiveSplit.TournamentTimerBridge.dll
```

The `.pdb` file is not required for LiveSplit.

## Copy the plugin

Copy the DLL to the LiveSplit Components folder.

Example:

```text
C:\LiveSplit\Components\LiveSplit.TournamentTimerBridge.dll
```

Restart LiveSplit after copying.

If Windows blocked the DLL:

```text
Right click DLL
-> Properties
-> Unblock
```

## Add the plugin to a LiveSplit layout

Copying the DLL is not enough. Add the component to the layout:

1. Restart LiveSplit.
2. Right-click LiveSplit.
3. Open Edit Layout.
4. Click the + button.
5. Find TournamentTimer Bridge.
6. Add it to the layout.
7. Click OK.
8. Save the layout.

After that, LiveSplit can send Start/Split events to Runner UI.

If the component is missing:

- check that the DLL is in `LiveSplit/Components`;
- restart LiveSplit;
- check whether Windows blocked the DLL;
- make sure you copied the DLL, not only the `.pdb`;
- make sure you are editing the LiveSplit installation that actually starts.

This is not a layout file. Do not open it through Open Layout. It is a LiveSplit component that must be added through Edit Layout.

## Required running apps

For bridge mode, start:

```text
TournamentTimer Server
Runner UI
LiveSplit with TournamentTimer Bridge added to the layout
```

Correct order:

```text
Server
-> Runner UI
-> Runner UI Connect
-> LiveSplit
-> game / autosplitter
```

## One Runner UI per PC

One PC should normally have one active Runner UI for LiveSplit bridge.

If two Runner UI windows are open, the second one may show:

```text
Local bridge already in use on this PC.
```

This is expected. Manual Start/Split can still work. The LiveSplit bridge works through the first Runner UI that owns the local bridge port.

## Autosplitters

If LiveSplit has an autosplitter, it continues to work as usual.

Autosplitter starts or splits in LiveSplit. The TournamentTimer Bridge plugin sends those events to Runner UI. Runner UI sends them to the server.

For load-removed timing, the plugin sends both LiveSplit `RealTime` and `GameTime`. Runner UI uses `GameTime` as the official elapsed time for LiveSplit split/final events and keeps `RealTime` as audit metadata.

`GameTime` must be configured and working in LiveSplit. For load-removal runs, set LiveSplit to use **Game Time** and enable the relevant autosplitter/load remover settings. If LiveSplit sends a bad GameTime value, Runner UI rejects the event, locks LiveSplit input for the current attempt, and the admin panel highlights the affected runner.

For WASM / AutoSplittingRuntime autosplitters, LiveSplit bridge can be the fallback path:

```text
WASM autosplitter in LiveSplit
-> TournamentTimer Bridge
-> Runner UI
```

ASL Host Launcher is not required for this path.

## Reset / undo behavior

A Reset in LiveSplit does not reset the official server attempt.

For a clean race start, the admin uses New attempt in the admin panel. If LiveSplit reset/undo/skip creates a desync during a run, Runner UI disables LiveSplit input for that attempt and the admin decides what to do next outside the automatic timing path.

This prevents a runner from accidentally changing the official server timer.

## Camera

Runner camera does not depend on LiveSplit bridge.

If the admin asks for camera video, enable camera in Runner UI. LiveSplit and autosplitters continue separately.

## Quick bridge test without LiveSplit

If Runner UI is open and connected, test the local bridge from PowerShell.

Start:

```powershell
Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:52991/api/local/livesplit/events" -ContentType "application/json" -Body '{"eventType":"start","sourceEventId":"manual-start-test","liveSplitRealTimeMs":0,"liveSplitGameTimeMs":0,"timerPhase":"Running"}'
```

Split with valid load-removed time:

```powershell
Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:52991/api/local/livesplit/events" -ContentType "application/json" -Body '{"eventType":"split","splitIndex":0,"sourceEventId":"manual-split-test","liveSplitRealTimeMs":15000,"liveSplitGameTimeMs":12345,"timerPhase":"Running"}'
```

Expected split result:

```text
accepted: true
status: Running
message: synced
```

The server log should show:

```text
TimingSource: LiveSplitGameTime
ClientElapsedMs: 12345
LiveSplitRealTimeMs: 15000
LiveSplitGameTimeMs: 12345
```

Bad GameTime guard test:

```powershell
Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:52991/api/local/livesplit/events" -ContentType "application/json" -Body '{"eventType":"split","splitIndex":0,"sourceEventId":"bad-zero-gametime-test","liveSplitRealTimeMs":5000,"liveSplitGameTimeMs":0,"timerPhase":"Running"}'
```

Expected bad split result:

```text
accepted: false
message: livesplit_input_disabled_invalid_livesplit_game_time_0_with_real_time_5000
```

The bad split must not appear as an accepted official server event. The admin panel should highlight the affected runner with a red LiveSplit input lock warning.

If the manual bridge test works but LiveSplit does not trigger Runner UI, the problem is probably plugin placement, layout setup, or LiveSplit configuration.

## Troubleshooting

Check:

- plugin DLL is in `LiveSplit/Components`;
- LiveSplit was restarted after copying the DLL;
- plugin was added through Edit Layout;
- layout was saved after adding the plugin;
- Runner UI is open;
- Runner UI is connected to the server;
- Run ID is correct;
- Runner ID is correct;
- Run key is correct;
- only one Runner UI is active for bridge on this PC;
- Runner UI does not show Local bridge already in use;
- plugin endpoint points to `http://127.0.0.1:52991/api/local/livesplit/events` if configurable;
- LiveSplit is displaying/using Game Time for load-removed runs;
- autosplitter/load remover is active and actually updates GameTime;
- admin panel does not show `LIVE SPLIT INPUT LOCKED` for the runner.
