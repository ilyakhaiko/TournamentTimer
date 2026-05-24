# TournamentTimer Runner Guide

This guide is for race participants.

You do not need to start the server or configure the admin panel. You need to open Runner UI, connect to the server, and send Start/Split events. If the admin asks for camera video, enable it in Runner UI.

## What the admin should give you

The admin should provide:

```text
Server URL
Run ID
Runner ID
Run key
```

Example:

```text
Server URL: http://123.123.123.123:5177
Run ID: re9
Runner ID: runner-1
Run key: long-random-run-key
```

Important:

- use only your own Runner ID;
- do not use another participant's Runner ID;
- `runKey` is needed to connect to the server;
- you do not need `adminKey`.

## Open Runner UI

Open the runner package and run:

```text
runner-ui/TournamentTimer.Runner.Wpf.exe
```

If the admin provided a shortcut or script, use that. The normal runner workflow is still simple: open the exe, fill the fields, and click Connect.

## Connect to the server

Fill in Runner UI:

```text
Server URL
Run ID
Runner ID
Run key
```

Click Connect.

After connecting, Runner UI shows:

- timer;
- current status;
- split list;
- local log path;
- server sync status;
- camera controls if available.

Runner UI saves the values for the next launch.

## Start and Split

The main runner action is Start / Split.

```text
Ready
  Start the timer.

Running
  Apply the next split.

Finished
  No normal runner action.
```

The runner normally does not have a dangerous Finish button. Finish should happen through the last split or LiveSplit bridge. If something goes wrong, the admin can correct or finish the run from the admin panel.

## Hotkey

Runner UI supports a hotkey.

The logic is the same:

```text
Ready -> Start
Running -> Split
Finished -> no action
```

To change the hotkey:

1. Click Change.
2. Press the desired key.
3. The new key is saved.

Typical supported keys:

```text
F1-F12
NumPad0-NumPad9
End
Home
PageUp
PageDown
Insert
Delete
Space
Slash
Backslash
Backtick
Minus
Equals
Comma
Period
Semicolon
Quote
LeftBracket
RightBracket
```

If a global hotkey cannot be registered, choose another key.

## Global hotkey

If Global hotkey is enabled, the hotkey can work while the game is focused.

If Global hotkey is disabled, the hotkey works only when Runner UI is focused.

If registration fails, Runner UI logs something like:

```text
Global hotkey registration failed. Key may be already used by another app.
```

## Camera

If the admin asks for camera video:

1. Click Connect first.
2. Click Start camera.
3. Allow camera access if Windows asks.
4. Keep Runner UI open.

If needed, restart camera:

1. Click Stop camera.
2. Wait a few seconds.
3. Click Start camera again.

Runner UI sends only camera video for OBS. Microphone and voice chat are handled separately.

## LiveSplit bridge

If the event uses LiveSplit or an autosplitter, Runner UI still needs to be open and connected.

Flow:

```text
LiveSplit / autosplitter
-> Runner UI on your PC
-> TournamentTimer Server
-> Admin panel and overlay
```

If two Runner UI windows are open on the same PC, one of them may show:

```text
Local bridge already in use on this PC.
```

This is expected. For a real LiveSplit bridge setup, keep one active Runner UI on the machine.

Manual Start/Split still works even if the bridge is unavailable.

For load-removed timing, LiveSplit must provide Game Time. If GameTime is invalid during a LiveSplit split/final event, Runner UI rejects the event, disables LiveSplit input for the current attempt, and the admin panel highlights the runner.

## ASL Host

If the event uses ASL Host, it sends Start/Split events to Runner UI through the local bridge.

The admin should provide prepared assets and instructions. Runner UI must be connected before ASL Host events can reach the server.

Basic flow:

```text
Game
-> ASL Host
-> Runner UI local bridge
-> TournamentTimer Server
```

If ASL Host sees game events but Runner UI does not change, check that Runner UI is open, connected, and running on the same machine.

## Admin corrections

If something goes wrong, the admin may correct the state from the admin panel.

After an admin correction, runner input may become locked or the runner may show Finished. This is expected. Follow the admin's instructions.

If the admin creates a New attempt, you may need to reconnect.

## Before the race

Checklist:

1. Open Runner UI.
2. Enter Server URL, Run ID, Runner ID, and Run key.
3. Click Connect.
4. Check that splits are loaded.
5. Check the hotkey.
6. Start camera if the admin asks for it.
7. If LiveSplit is used, check that events reach Runner UI.
8. Wait for the admin's start command.
9. If the admin presses New attempt after testing, reconnect if needed.

## Common problems

### 401 while connecting

Usually wrong Run key. Ask the admin to check it.

### 404 while connecting

Usually wrong Run ID or the server is running a different config.

### Server offline / connection error

Check:

```text
Server URL
server is running
internet or VPN connection
firewall / port access
```

### Runner ID is already in use

Another window or machine may already be connected with the same Runner ID.

Close the old window, wait a few seconds, then connect again.

### Splits did not load

Check Run ID and connection status. If the Run ID is wrong, Runner UI cannot load the intended run.

### Local bridge already in use

Another Runner UI window on this PC is already using the LiveSplit bridge port.

For testing two windows on one PC, this can be normal. For a real LiveSplit bridge setup, use one active Runner UI.

### Camera says Connect to server first

Click Connect first. Camera can start only after Runner UI is connected.

### Camera permission or blocked error

Check Windows camera privacy settings and close other apps that may be using the camera.

### Camera is online but admin cannot see it in OBS

Keep Runner UI open. Check that camera is still Online. If the admin asks, click Stop camera and Start camera again.
