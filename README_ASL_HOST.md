# TournamentTimer ASL Host Guide

This guide is for optional ASL Host tooling.

ASL Host is experimental helper tooling for running some ASL autosplitters without the full LiveSplit UI. It is not required for normal TournamentTimer use.

If a specific autosplitter does not work headlessly, use manual Runner UI input, LiveSplit bridge, or admin correction.

## Main idea

ASL Host flow:

```text
Game
-> ASL Host
-> Runner UI local bridge
-> TournamentTimer Server
-> Admin panel and overlay
```

ASL Host does not send official events directly to the server. Runner UI remains the bridge to the official server state.

Runner UI must be open and connected before ASL Host events can reach the server.

## Component roles

Do not mix these roles:

```text
ASL Catalog
  Admin/server-admin tool.
  Finds autosplitters, installs assets, opens settings, saves asl-settings.json, prepares splits.lss.

ASL Host
  Runner runtime.
  Runs autosplitter.asl and sends detected events to Runner UI local bridge.

ASL Host Launcher
  Runner tool.
  Starts, stops, and restarts ASL Host and shows logs.
```

ASL Catalog should not be bundled as a runner runtime dependency. ASL Host Launcher should not become the catalog.

## What to prepare

Before ASL testing, prepare:

```text
Runner UI connected to the server
Run ID
autosplitter.asl
splits.lss
asl-settings.json       optional
correct x86/x64 choice
```

Typical assets folder:

```text
server-runs/assets/<runId>/
  autosplitter.asl
  splits.lss
  asl-settings.json
  Components/
```

For a real tournament, the ASL Host Run ID should normally match the server Run ID.

## Run ID

There are two related meanings:

```text
Server Run ID
  ID of the tournament config in configs/<runId>.json.

ASL Host Run ID
  Name of the assets folder in server-runs/assets/<runId>.
```

For normal use, keep them the same.

Example:

```text
Server Run ID: re9
ASL Host Run ID: re9
Config: configs/re9.json
Assets: server-runs/assets/re9
```

## Assets folder

Current default:

```text
server-runs/assets/<runId>/
```

Expected files:

```text
autosplitter.asl
splits.lss
asl-settings.json
```

`autosplitter.asl` defines the ASL logic.

`splits.lss` defines the split list.

`asl-settings.json` stores selected ASL settings for the run.

## ASL Catalog

ASL Catalog is used to find and install autosplitters from a catalog.

Typical workflow:

1. Open ASL Catalog from the server-admin or full package.
2. Click Load catalog.
3. Search for the game.
4. Set RunId.
5. Check Runs root.
6. Select an autosplitter.
7. Click Install + Configure.

After installation, files are placed under:

```text
server-runs/assets/<runId>/
```

If the selected entry is unsupported, for example WASM / AutoSplittingRuntime, the catalog should show it as unsupported and should not install it as a normal ASL.

## ASL settings

Some ASL scripts expose settings.

Important: `autosplitter.asl` declares settings, but selected values are stored separately in `asl-settings.json`.

Open settings through:

```text
Install + Configure
Open settings for RunId
ASL Host Launcher settings button if available
```

Workflow:

1. Open ASL Settings.
2. Check Settings file path.
3. Select the required settings.
4. Save `asl-settings.json`.
5. If needed, preview or generate `splits.lss`.

Runner UI should not be the place where ASL settings are chosen. The admin should prepare assets before the event.

## Generate splits.lss from ASL settings

Some autosplitters have settings that can be used as a draft split list.

This is best-effort only. It is not guaranteed to produce a perfect route.

Safe workflow:

1. Open ASL Settings.
2. Select the required settings.
3. Click Preview / Generate splits.lss.
4. Remove unwanted service/debug/options entries.
5. Rename splits if needed.
6. Reorder rows if needed.
7. Save `splits.lss`.

Do not blindly save generated splits without review.

If the generator cannot create a good split list, use a real LiveSplit `.lss` file or placeholder splits for testing.

## Start ASL Host

Use ASL Host Launcher from the runner or full package.

Typical workflow:

1. Select Run ID.
2. Check that `autosplitter.asl` exists.
3. Check that `splits.lss` exists.
4. Check that `asl-settings.json` exists if settings are needed.
5. Choose x64 or x86.
6. Start ASL Host.
7. Start the game.
8. Watch ASL Host log.
9. Check Runner UI and admin panel.

For x64 games, use x64 ASL Host.

For old x86 games, use x86 ASL Host.

If you are unsure, try x64 first. If the ASL cannot see the process or memory reads fail, check architecture.

## Local bridge

ASL Host sends events to Runner UI through:

```text
http://127.0.0.1:52991/api/local/livesplit/events
```

Runner UI then:

- validates local state;
- writes local log;
- sends the event to the server;
- writes server sync log.

If Runner UI is not connected, ASL Host may see game events but TournamentTimer Server will not receive them.

## New attempt behavior

When the admin creates a New attempt, old Runner UI sessions should not keep sending events for the previous attempt.

Expected behavior:

```text
server creates New attempt
old Runner UI detects wrong attempt
Runner UI disconnects for safety
runner reconnects to the new attempt
```

Before the real start, the admin should create a clean attempt after testing.

## x86 and x64

Architecture matters because ASL scripts often read game process memory.

```text
x64 ASL Host
  Use for x64 games.

x86 ASL Host
  Use for old x86 games.
```

Do not run x86 and x64 host at the same time for the same run unless you are deliberately debugging. It can create duplicate Start/Split events.

## Compatibility position

Supported or intended:

```text
simple self-contained ASL
ASL with settings
some ASL helper dependencies after testing
```

Warning / not guaranteed:

```text
ASL with external DLLs
ASL with complex helpers
architecture-specific helper dependencies
```

Currently unsupported as normal ASL Host input:

```text
WASM
AutoSplittingRuntime
ASR
.NET component autosplitters
```

These need separate future runtime work if required.

## Minimum test checklist

1. Start TournamentTimer Server.
2. Open Admin panel.
3. Open Runner UI.
4. Connect Runner UI.
5. Prepare assets in `server-runs/assets/<runId>`.
6. Start ASL Host Launcher.
7. Start ASL Host.
8. Start the game.
9. Check ASL Host log for START/SPLIT.
10. Check Runner UI log.
11. Check admin panel split table.
12. Press New attempt before the real race.

## Common problems

### ASL Catalog finds nothing

Check:

```text
Load catalog was clicked
Catalog URL is correct
game name search uses the catalog name
```

### Settings are visible but ASL Host uses defaults

Check:

```text
asl-settings.json exists
settings file is in the current run assets folder
Run ID matches
live mode uses the same Run ID
```

### Generated splits look wrong

This is expected sometimes. The generator is best-effort.

Remove unwanted entries, rename rows, reorder them, or use a real `.lss` file.

### ASL Host does not see the game

Check:

```text
game is running
correct x86/x64 host
correct autosplitter.asl
process name expected by ASL
ASL Host log
```

### ASL Host sees events but Runner UI does not change

Check:

```text
Runner UI is open
Runner UI is connected
Runner UI and ASL Host are on the same machine
local bridge is not already occupied by another Runner UI
```

### x86 host crashes on dependency

A helper DLL may be built for x64 or have architecture-specific dependencies. Try x64 if the game is x64.

### WASM / AutoSplittingRuntime is blocked

This is intentional. WASM / ASR is not normal ASL Host support.
