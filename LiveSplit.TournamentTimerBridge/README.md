# LiveSplit.TournamentTimerBridge

LiveSplit component for TournamentTimer.

It sends local LiveSplit timer events to the WPF Runner UI bridge:

POST http://127.0.0.1:52991/api/local/livesplit/events

Events:
- start
- split
- reset diagnostic only

The plugin sends LiveSplit real time and game time. Runner UI uses LiveSplit game time as the official elapsed value for LiveSplit split/final events, which allows load-removed timing from LiveSplit/autosplitters. LiveSplit real time is kept as audit metadata.

For load-removed runs, LiveSplit must be configured to use Game Time and the relevant autosplitter/load remover must be active. If Runner UI receives an invalid GameTime value for a split/final event, it rejects the event and locks LiveSplit input for that attempt.

Runner UI remains the owner of local logging and server sync.

## Build

This is a .NET Framework 4.8.1 LiveSplit component project.

You need references to:
- LiveSplit.Core.dll
- UpdateManager.dll

Example with MSBuild:

msbuild .\LiveSplit.TournamentTimerBridge.csproj /p:Configuration=Release /p:LiveSplitDir="C:\Path\To\LiveSplit"

Then copy:

bin\Release\LiveSplit.TournamentTimerBridge.dll

to:

C:\Path\To\LiveSplit\Components\LiveSplit.TournamentTimerBridge.dll

Restart LiveSplit, then add the component in Layout Editor:

Control -> TournamentTimer Bridge

## Test flow

1. Start TournamentTimer server.
2. Start WPF Runner UI from a fresh build.
3. Connect Runner UI.
4. Start LiveSplit.
5. Add the TournamentTimer Bridge component to the LiveSplit layout.
6. Set LiveSplit to Game Time for load-removed timing.
7. Split in LiveSplit.

The WPF Runner UI log should show accepted local/server events. Server logs should show `TimingSource=LiveSplitGameTime` for LiveSplit splits.
