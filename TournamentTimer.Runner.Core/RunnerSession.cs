using System.Diagnostics;
using TournamentTimer.Core;

namespace TournamentTimer.Runner;

public sealed class RunnerSession : IAsyncDisposable
{
    private readonly ServerEventClient _serverClient;
    private readonly LocalRunLogReader _logReader;
    private readonly LocalRunLogWriter _logWriter;
    private readonly LocalServerSyncLogWriter _syncLogWriter;
    private readonly TimerEngine _engine;
    private readonly CancellationTokenSource _heartbeatCts = new();
    private readonly SemaphoreSlim _serverSyncLock = new(1, 1);

    private Task? _heartbeatTask;
    private long? _resumeTimestamp;
    private bool _disposed;
    private readonly Dictionary<int, RunnerCompletedSplit> _completedSplits = [];

    private RunnerSession(
        RunnerSessionOptions options,
        ServerEventClient serverClient,
        LocalRunLogReader logReader,
        LocalRunLogWriter logWriter,
        LocalServerSyncLogWriter syncLogWriter,
        TimerEngine engine,
        RunConfig config,
        string attemptId,
        string logFilePath,
        string syncLogFilePath,
        bool explicitLogPath,
        bool logFileExistsAtStartup,
        RunState state,
        long baseElapsedMs)
    {
        Options = options;
        _serverClient = serverClient;
        _logReader = logReader;
        _logWriter = logWriter;
        _syncLogWriter = syncLogWriter;
        _engine = engine;

        Config = config;
        AttemptId = attemptId;
        LogFilePath = logFilePath;
        SyncLogFilePath = syncLogFilePath;
        ExplicitLogPath = explicitLogPath;
        LogFileExistsAtStartup = logFileExistsAtStartup;

        State = state;
        BaseElapsedMs = baseElapsedMs;

        if (State.Status == RunStatus.Running)
        {
            _resumeTimestamp = Stopwatch.GetTimestamp();
        }
    }

    public RunnerSessionOptions Options { get; }

    public RunConfig Config { get; }
    public string AttemptId { get; }
    public string RunnerId => Options.RunnerId;
    public string ServerUrl => Options.ServerUrl;

    public string LogFilePath { get; }
    public string SyncLogFilePath { get; }

    public bool ExplicitLogPath { get; }
    public bool LogFileExistsAtStartup { get; }

    public RunState State { get; private set; }
    public long BaseElapsedMs { get; private set; }
    public long ServerStateVersion { get; private set; }
    public bool AdminControlMode { get; private set; }
    public IReadOnlyDictionary<int, RunnerCompletedSplit> CompletedSplits => _completedSplits;

    public string LogModeLabel
    {
        get
        {
            if (ExplicitLogPath)
            {
                return "manual log path";
            }

            return LogFileExistsAtStartup
                ? "auto resume current attempt"
                : "auto new log for current attempt";
        }
    }

    public long CurrentElapsedMs => GetCurrentElapsedMs(_resumeTimestamp, BaseElapsedMs);

    public string? CurrentSplitName
    {
        get
        {
            var nextSplitIndex = State.LastCompletedSplitIndex + 1;

            if (State.Status == RunStatus.Running &&
                nextSplitIndex >= 0 &&
                nextSplitIndex < Config.Splits.Count)
            {
                return Config.Splits[nextSplitIndex].Name;
            }

            return null;
        }
    }

    public static async Task<RunnerSessionCreateResult> CreateAsync(RunnerSessionOptions options)
    {
        var messages = new List<string>();
        var serverClient = new ServerEventClient(options.ServerUrl, options.RunKey);

        RunnerSessionCreateResult Fail(params string[] errors)
        {
            serverClient.Dispose();

            return new RunnerSessionCreateResult
            {
                Session = null,
                Messages = messages,
                Errors = errors
            };
        }

        RunConfig config;

        if (options.RunId is not null)
        {
            var serverConfig = await serverClient.GetRunConfigAsync(options.RunId);

            if (serverConfig is null)
            {
                return Fail(
                    $"ERROR: failed to load run config from server for runId={options.RunId}",
                    serverClient.LastError ?? "Server did not return run config. Check Server URL, RunId and Run key.");
            }

            config = serverConfig;
            messages.Add($"Run config:      server / {options.RunId}");
        }
        else
        {
            config = RunConfigLoader.LoadFromFile(options.ConfigPath);
            messages.Add($"Run config:      {options.ConfigPath}");
        }

        string attemptId;

        if (options.RunId is not null)
        {
            var attempt = await serverClient.GetAttemptAsync(config.RunId);

            if (attempt is null)
            {
                return Fail(
                    $"ERROR: failed to load current attempt from server for runId={config.RunId}",
                    serverClient.LastError ?? "Server did not return current attempt. Check Server URL, RunId and Run key.");
            }

            attemptId = attempt.AttemptId;
        }
        else
        {
            attemptId = "local-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        }

        var logFilePath = GetLogFilePath(config, attemptId, options.RunnerId, options.LogPath);
        var syncLogFilePath = GetSyncLogFilePath(logFilePath);

        var explicitLogPath = options.ExplicitLogPath;
        var logFileExistsAtStartup = File.Exists(logFilePath);

        if (explicitLogPath && !logFileExistsAtStartup)
        {
            return Fail(
                "ERROR: specified local event log file does not exist.",
                $"Log path: {logFilePath}");
        }

        var logWriter = new LocalRunLogWriter(logFilePath);
        var syncLogWriter = new LocalServerSyncLogWriter(syncLogFilePath);
        var logReader = new LocalRunLogReader();
        var engine = new TimerEngine();

        var logRunIds = logReader.ReadRunIds(logFilePath);
        var logAttemptIds = logReader.ReadAttemptIds(logFilePath);
        var logRunnerIds = logReader.ReadRunnerIds(logFilePath);

        if (logRunIds.Count > 0 && !logRunIds.Contains(config.RunId))
        {
            return Fail(
                "ERROR: local event log belongs to a different run.",
                $"Current config runId: {config.RunId}",
                $"Log runId(s):         {string.Join(", ", logRunIds)}",
                "",
                "Use the correct log file or start a new run without passing an old log path.");
        }

        if (logRunIds.Count > 1)
        {
            return Fail(
                "ERROR: local event log contains multiple runIds.",
                $"Log runId(s): {string.Join(", ", logRunIds)}",
                "",
                "This log is mixed/corrupted and should not be used for restore.");
        }

        if (logAttemptIds.Count > 0 && !logAttemptIds.Contains(attemptId))
        {
            return Fail(
                "ERROR: local event log belongs to a different attempt.",
                $"Current attemptId: {attemptId}",
                $"Log attemptId(s):  {string.Join(", ", logAttemptIds)}",
                "",
                "Use the correct local log, or create/reset the server attempt from admin.");
        }

        if (logAttemptIds.Count > 1)
        {
            return Fail(
                "ERROR: local event log contains multiple attemptIds.",
                $"Log attemptId(s): {string.Join(", ", logAttemptIds)}",
                "",
                "This log is mixed/corrupted and should not be used for restore.");
        }

        if (logRunnerIds.Count > 0 && !logRunnerIds.Contains(options.RunnerId))
        {
            return Fail(
                "ERROR: local event log belongs to a different runner.",
                $"Current runnerId: {options.RunnerId}",
                $"Log runnerId(s):  {string.Join(", ", logRunnerIds)}",
                "",
                "Use the correct local log, or start this runner without passing another runner's log.");
        }

        if (logRunnerIds.Count > 1)
        {
            return Fail(
                "ERROR: local event log contains multiple runnerIds.",
                $"Log runnerId(s): {string.Join(", ", logRunnerIds)}",
                "",
                "This log is mixed/corrupted and should not be used for restore.");
        }

        if (options.RunId is not null && logRunIds.Count > 0 && logAttemptIds.Count == 0)
        {
            return Fail(
                "ERROR: local event log has no attemptId.",
                "This log was created before attempt tracking and cannot be safely resumed in server mode.",
                "",
                "Reset/create a new server attempt, or use a newer log with AttemptId.");
        }

        var restore = logReader.Restore(config, options.RunnerId, logFilePath);
        var localLogHasEvents = logRunIds.Count > 0;

        if (options.RunId is not null && !localLogHasEvents)
        {
            var serverState = await serverClient.GetRunStateAsync(config.RunId, options.RunnerId);

            if (serverState is null)
            {
                return Fail(
                    "ERROR: failed to read server run state.",
                    serverClient.LastError ?? "Server did not return runner state. Cannot safely start a new local log.");
            }

            if (!string.Equals(serverState.Status, "Ready", StringComparison.OrdinalIgnoreCase))
            {
                return Fail(
                    "ERROR: server already has an active or finished state for this run.",
                    $"Server status: {serverState.Status}",
                    $"Server last split: {serverState.LastCompletedSplitIndex}",
                    $"Server finished at: {FormatNullableMs(serverState.FinishedAtMs)}",
                    "",
                    "Use the previous local log to resume, or reset the server from admin before starting a new run.");
            }
        }
        var leaseResponse = await serverClient.SendHeartbeatAsync(
            config.RunId,
            attemptId,
            options.RunnerId,
            options.RunnerClientId);

        if (!leaseResponse.Sent)
        {
            return Fail(
                "ERROR: failed to claim runner slot.",
                $"Heartbeat failed: {leaseResponse.TransportError}");
        }

        if (!leaseResponse.Accepted)
        {
            return Fail(
                "ERROR: runner slot is already connected.",
                $"RunnerId: {options.RunnerId}",
                $"Reason: {leaseResponse.RejectReason ?? "heartbeat_rejected"}",
                "Close the existing runner window, or wait a few seconds after it disconnects.");
        }

        var session = new RunnerSession(
            options,
            serverClient,
            logReader,
            logWriter,
            syncLogWriter,
            engine,
            config,
            attemptId,
            logFilePath,
            syncLogFilePath,
            explicitLogPath,
            logFileExistsAtStartup,
            restore.State,
            restore.BaseElapsedMs);

        session.RestoreCompletedSplitsFromEvents(
            logReader.ReadAcceptedEvents(config, options.RunnerId, logFilePath));

        var startupSyncOk = await session.SyncAcceptedEventsToServerAsync(messages);

        if (!startupSyncOk)
        {
            await session.DisposeAsync();

            return new RunnerSessionCreateResult
            {
                Session = null,
                Messages = messages,
                Errors =
                [
                    "",
                    "FATAL: startup sync failed.",
                    "The selected local log does not match the current server state.",
                    "Use the correct local log, or reset the server from admin before starting/resuming."
                ]
            };
        }

        var initialServerState = await session.RefreshServerStateAsync();

        if (initialServerState.Applied)
        {
            messages.Add($"SERVER STATE APPLIED: status={session.State.Status}, lastSplit={session.State.LastCompletedSplitIndex}, adminControl={session.AdminControlMode}");
        }

        session.StartHeartbeat();

        return new RunnerSessionCreateResult
        {
            Session = session,
            Messages = messages,
            Errors = []
        };
    }

    public async Task<RunnerSessionActionResult> StartAsync()
    {
        var pendingResumeTimestamp = Stopwatch.GetTimestamp();

        return await ApplyLocalAndSyncAsync(
            new StartRunEvent(Guid.NewGuid().ToString("N")),
            pendingResumeTimestamp);
    }

    public async Task<RunnerSessionActionResult> StartFromExternalTimingAsync(RunnerExternalTiming timing)
    {
        var pendingResumeTimestamp = Stopwatch.GetTimestamp();

        var runEvent = new StartRunEvent(CreateClientEventId(timing.SourceEventId)) with
        {
            TimingSource = timing.TimingSource,
            LiveSplitRealTimeMs = timing.LiveSplitRealTimeMs,
            LiveSplitGameTimeMs = timing.LiveSplitGameTimeMs,
            SourceEventId = timing.SourceEventId,
            SourceOccurredAtUtc = timing.SourceOccurredAtUtc
        };

        return await ApplyLocalAndSyncAsync(runEvent, pendingResumeTimestamp);
    }

    public async Task<RunnerSessionActionResult> SplitAsync()
    {
        if (State.Status == RunStatus.Finished)
        {
            return RunnerSessionActionResult.LocalRejected("SplitRunEvent", "run_already_finished");
        }

        var nextSplitIndex = State.LastCompletedSplitIndex + 1;

        if (nextSplitIndex >= Config.Splits.Count)
        {
            return RunnerSessionActionResult.LocalRejected("SplitRunEvent", "no_next_split");
        }

        var elapsedMs = CurrentElapsedMs;

        return await ApplyLocalAndSyncAsync(
            new SplitRunEvent(Guid.NewGuid().ToString("N"), nextSplitIndex, elapsedMs),
            pendingResumeTimestamp: null);
    }

    public async Task<RunnerSessionActionResult> SplitFromExternalTimingAsync(int splitIndex, RunnerExternalTiming timing)
    {
        if (State.Status == RunStatus.Finished)
        {
            return RunnerSessionActionResult.LocalRejected("SplitRunEvent", "run_already_finished");
        }

        var nextSplitIndex = State.LastCompletedSplitIndex + 1;

        if (nextSplitIndex >= Config.Splits.Count)
        {
            return RunnerSessionActionResult.LocalRejected("SplitRunEvent", "no_next_split");
        }

        if (splitIndex != nextSplitIndex)
        {
            return RunnerSessionActionResult.LocalRejected(
                "SplitRunEvent",
                $"split_index_mismatch_expected_{nextSplitIndex}_got_{splitIndex}");
        }

        if (timing.OfficialElapsedMs < 0)
        {
            return RunnerSessionActionResult.LocalRejected("SplitRunEvent", "invalid_external_elapsed_time");
        }

        var runEvent = new SplitRunEvent(
            CreateClientEventId(timing.SourceEventId),
            splitIndex,
            timing.OfficialElapsedMs) with
        {
            TimingSource = timing.TimingSource,
            LiveSplitRealTimeMs = timing.LiveSplitRealTimeMs,
            LiveSplitGameTimeMs = timing.LiveSplitGameTimeMs,
            SourceEventId = timing.SourceEventId,
            SourceOccurredAtUtc = timing.SourceOccurredAtUtc
        };

        return await ApplyLocalAndSyncAsync(runEvent, pendingResumeTimestamp: null);
    }

    public async Task<RunnerSessionActionResult> FinishAsync()
    {
        var elapsedMs = CurrentElapsedMs;

        return await ApplyLocalAndSyncAsync(
            new FinishRunEvent(Guid.NewGuid().ToString("N"), elapsedMs),
            pendingResumeTimestamp: null);
    }

    public async Task<InputLockClientResponse> ReportInputDesyncAsync(
        string reason,
        string? sourceEventId)
    {
        if (Options.RunId is null)
        {
            return InputLockClientResponse.TransportFailed("local_mode");
        }

        var response = await _serverClient.ReportInputLockAsync(
            Config.RunId,
            AttemptId,
            RunnerId,
            reason,
            sourceEventId);

        if (response.Sent && response.Accepted)
        {
            var refresh = await RefreshServerStateAsync();

            if (!refresh.Success)
            {
                AdminControlMode = response.AdminControlMode;
                ServerStateVersion = Math.Max(ServerStateVersion, response.StateVersion);
            }
        }

        return response;
    }

    public async Task StopAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _heartbeatCts.Cancel();

        if (_heartbeatTask is not null)
        {
            try
            {
                await _heartbeatTask;
            }
            catch (OperationCanceledException)
            {
                // Expected on quit.
            }
        }

        _heartbeatCts.Dispose();
        _serverSyncLock.Dispose();
        _serverClient.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private async Task<RunnerSessionActionResult> ApplyLocalAndSyncAsync(
        RunEvent runEvent,
        long? pendingResumeTimestamp)
    {
        if (AdminControlMode)
        {
            return RunnerSessionActionResult.LocalRejected(
                runEvent.GetType().Name,
                "admin_control_mode");
        }

        var result = _engine.ApplyEvent(Config, State, runEvent);

        _logWriter.AppendEventAttempt(Config, AttemptId, RunnerId, runEvent, result);

        if (!result.Accepted)
        {
            return RunnerSessionActionResult.LocalRejected(
                runEvent.GetType().Name,
                result.RejectReason ?? "unknown_local_reject");
        }

        State = result.State;
        BaseElapsedMs = runEvent.ClientElapsedMs;

        if (runEvent is SplitRunEvent splitEvent)
        {
            UpsertCompletedSplit(splitEvent.SplitIndex, splitEvent.ClientElapsedMs);
        }

        if (State.Status == RunStatus.Finished)
        {
            _resumeTimestamp = null;
        }
        else if (runEvent is StartRunEvent && pendingResumeTimestamp is not null)
        {
            _resumeTimestamp = pendingResumeTimestamp;
        }
        else if (State.Status == RunStatus.Running)
        {
            _resumeTimestamp = Stopwatch.GetTimestamp();
        }

        var serverResponse = await SyncPendingEventsToServerAsync(runEvent.ClientEventId);

        var fatalServerMismatch =
            serverResponse.Sent &&
            !serverResponse.Accepted;

        return new RunnerSessionActionResult
        {
            EventName = runEvent.GetType().Name,
            ClientElapsedMs = runEvent.ClientElapsedMs,
            LocalAccepted = true,
            LocalRejectReason = null,
            ServerResponse = serverResponse,
            FatalServerMismatch = fatalServerMismatch
        };
    }

    public async Task<RunnerServerStateRefreshResult> RefreshServerStateAsync()
    {
        if (Options.RunId is null)
        {
            return RunnerServerStateRefreshResult.Skipped("local_mode");
        }

        var serverState = await _serverClient.GetRunStateAsync(Config.RunId, RunnerId);

        if (serverState is null)
        {
            return RunnerServerStateRefreshResult.Failed("server_state_unavailable");
        }

        if (!string.IsNullOrWhiteSpace(serverState.AttemptId) &&
            !string.Equals(serverState.AttemptId, AttemptId, StringComparison.OrdinalIgnoreCase))
        {
            return RunnerServerStateRefreshResult.Failed("wrong_attempt");
        }

        var shouldApply =
            serverState.StateVersion > ServerStateVersion ||
            serverState.AdminControlMode != AdminControlMode;

        if (!shouldApply)
        {
            return RunnerServerStateRefreshResult.NoChange(
                serverState.StateVersion,
                serverState.AdminControlMode);
        }

        if (!Enum.TryParse<RunStatus>(serverState.Status, ignoreCase: true, out var status))
        {
            return RunnerServerStateRefreshResult.Failed($"unknown_status_{serverState.Status}");
        }

        State = new RunState
        {
            Status = status,
            LastCompletedSplitIndex = serverState.LastCompletedSplitIndex,
            FinishedAtMs = serverState.FinishedAtMs,
            Events = State.Events,
            SeenClientEventIds = State.SeenClientEventIds
        };

        BaseElapsedMs = status switch
        {
            RunStatus.Ready => 0,
            RunStatus.Finished => serverState.FinishedAtMs ?? serverState.DisplayElapsedMs,
            _ => serverState.DisplayElapsedMs
        };

        _resumeTimestamp = status == RunStatus.Running
            ? Stopwatch.GetTimestamp()
            : null;

        ApplyCompletedSplitsFromServer(serverState.CompletedSplits);

        ServerStateVersion = serverState.StateVersion;
        AdminControlMode = serverState.AdminControlMode;

        return RunnerServerStateRefreshResult.CreateApplied(
            serverState.StateVersion,
            serverState.AdminControlMode,
            serverState.Status,
            serverState.LastCompletedSplitIndex);
    }

    private void RestoreCompletedSplitsFromEvents(IEnumerable<RunEvent> events)
    {
        _completedSplits.Clear();

        foreach (var runEvent in events)
        {
            if (runEvent is SplitRunEvent split)
            {
                UpsertCompletedSplit(split.SplitIndex, split.ClientElapsedMs);
            }
        }
    }

    private void ApplyCompletedSplitsFromServer(IEnumerable<CompletedSplitClientResponse> completedSplits)
    {
        _completedSplits.Clear();

        foreach (var split in completedSplits.OrderBy(split => split.SplitIndex))
        {
            var name = string.IsNullOrWhiteSpace(split.Name)
                ? GetSplitName(split.SplitIndex)
                : split.Name!;

            _completedSplits[split.SplitIndex] = new RunnerCompletedSplit
            {
                SplitIndex = split.SplitIndex,
                Name = name,
                ClientElapsedMs = split.ClientElapsedMs,
                ClientElapsed = string.IsNullOrWhiteSpace(split.ClientElapsed)
                    ? FormatMs(split.ClientElapsedMs)
                    : split.ClientElapsed!
            };
        }
    }

    private void UpsertCompletedSplit(int splitIndex, long clientElapsedMs)
    {
        _completedSplits[splitIndex] = new RunnerCompletedSplit
        {
            SplitIndex = splitIndex,
            Name = GetSplitName(splitIndex),
            ClientElapsedMs = clientElapsedMs,
            ClientElapsed = FormatMs(clientElapsedMs)
        };

        foreach (var key in _completedSplits.Keys.Where(key => key > State.LastCompletedSplitIndex).ToArray())
        {
            _completedSplits.Remove(key);
        }
    }

    private string GetSplitName(int splitIndex)
    {
        return splitIndex >= 0 && splitIndex < Config.Splits.Count
            ? Config.Splits[splitIndex].Name
            : $"Split {splitIndex + 1}";
    }

    private async Task<bool> SyncAcceptedEventsToServerAsync(List<string> messages)
    {
        var response = await SyncPendingEventsToServerAsync(
            targetClientEventId: null,
            messages: messages);

        if (!response.Sent)
        {
            return false;
        }

        return response.Accepted;
    }

    private async Task<ServerEventResponse> SyncPendingEventsToServerAsync(
        string? targetClientEventId,
        List<string>? messages = null)
    {
        await _serverSyncLock.WaitAsync();

        try
        {
            var acceptedEvents = _logReader.ReadAcceptedEvents(Config, RunnerId, LogFilePath);

            if (acceptedEvents.Count == 0)
            {
                return BuildAlreadySyncedServerResponse();
            }

            var successfullySyncedIds = _syncLogWriter.ReadSuccessfullySyncedClientEventIds(
                SyncLogFilePath,
                Config.RunId,
                AttemptId,
                RunnerId);

            if (targetClientEventId is not null && successfullySyncedIds.Contains(targetClientEventId))
            {
                return BuildAlreadySyncedServerResponse();
            }

            var pendingEvents = acceptedEvents
                .Where(runEvent => !successfullySyncedIds.Contains(runEvent.ClientEventId))
                .ToList();

            if (pendingEvents.Count == 0)
            {
                messages?.Add("SYNC OK: no pending local event(s).");
                return BuildAlreadySyncedServerResponse();
            }

            messages?.Add($"SYNC: sending {pendingEvents.Count} pending local event(s) to server...");

            var synced = 0;
            var alreadyProcessed = 0;
            ServerEventResponse? targetResponse = null;

            foreach (var pendingEvent in pendingEvents)
            {
                var response = await _serverClient.SendEventAsync(
                    Config.RunId,
                    AttemptId,
                    RunnerId,
                    Options.RunnerClientId,
                    pendingEvent);

                _syncLogWriter.AppendSyncAttempt(Config, AttemptId, RunnerId, pendingEvent, response);

                if (pendingEvent.ClientEventId == targetClientEventId)
                {
                    targetResponse = response;
                }

                if (!response.Sent)
                {
                    messages?.Add($"SYNC STOPPED: server unavailable: {response.TransportError}");
                    return targetResponse ?? response;
                }

                if (!response.Accepted)
                {
                    messages?.Add($"SYNC STOPPED: server rejected {pendingEvent.ClientEventId}: {response.RejectReason}");
                    return targetResponse ?? response;
                }

                ApplyAcceptedServerResponse(response);

                if (response.AlreadyProcessed)
                {
                    alreadyProcessed++;
                }
                else
                {
                    synced++;
                }
            }

            messages?.Add($"SYNC OK: new={synced}, alreadyProcessed={alreadyProcessed}");
            messages?.Add("");

            return targetResponse ?? BuildAlreadySyncedServerResponse();
        }
        finally
        {
            _serverSyncLock.Release();
        }
    }

    private void ApplyAcceptedServerResponse(ServerEventResponse response)
    {
        if (response.Sent && response.Accepted && response.StateVersion > 0)
        {
            ServerStateVersion = response.StateVersion;
            AdminControlMode = response.AdminControlMode;
        }
    }

    private ServerEventResponse BuildAlreadySyncedServerResponse()
    {
        return new ServerEventResponse
        {
            Accepted = true,
            AlreadyProcessed = true,
            RunnerId = RunnerId,
            Status = State.Status.ToString(),
            LastCompletedSplitIndex = State.LastCompletedSplitIndex,
            FinishedAtMs = State.FinishedAtMs,
            StateVersion = ServerStateVersion,
            AdminControlMode = AdminControlMode
        };
    }

    private void StartHeartbeat()
    {
        _heartbeatTask = HeartbeatAndSyncLoopAsync(_heartbeatCts.Token);
    }

    private async Task HeartbeatAndSyncLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

        while (!cancellationToken.IsCancellationRequested)
        {
            var response = await _serverClient.SendHeartbeatAsync(
                Config.RunId,
                AttemptId,
                RunnerId,
                Options.RunnerClientId);

            if (response.Sent && response.Accepted)
            {
                var syncResponse = await SyncPendingEventsToServerAsync(targetClientEventId: null);

                if (syncResponse.Sent && syncResponse.Accepted)
                {
                    // Pending local events, if any, have been replayed to the server.
                }
            }

            await timer.WaitForNextTickAsync(cancellationToken);
        }
    }

    private static string CreateClientEventId(string? sourceEventId)
    {
        return string.IsNullOrWhiteSpace(sourceEventId)
            ? Guid.NewGuid().ToString("N")
            : sourceEventId.Trim();
    }

    private static long GetCurrentElapsedMs(long? resumeTimestamp, long baseElapsedMs)
    {
        if (resumeTimestamp is null)
        {
            return baseElapsedMs;
        }

        var elapsedTicks = Stopwatch.GetTimestamp() - resumeTimestamp.Value;
        var elapsedSinceResumeMs = (long)(elapsedTicks * 1000.0 / Stopwatch.Frequency);

        return baseElapsedMs + elapsedSinceResumeMs;
    }

    private static string GetLogFilePath(
        RunConfig config,
        string attemptId,
        string runnerId,
        string? explicitLogPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitLogPath))
        {
            return Path.GetFullPath(explicitLogPath);
        }

        var safeRunnerId = SanitizeFileNamePart(runnerId);
        var logFileName = $"{config.RunId}.{attemptId}.{safeRunnerId}.events.jsonl";

        return Path.Combine(Environment.CurrentDirectory, "local-runs", logFileName);
    }

    private static string GetSyncLogFilePath(string eventLogFilePath)
    {
        const string eventLogSuffix = ".events.jsonl";

        if (eventLogFilePath.EndsWith(eventLogSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return eventLogFilePath[..^eventLogSuffix.Length] + ".server-sync.jsonl";
        }

        return eventLogFilePath + ".server-sync.jsonl";
    }

    private static string SanitizeFileNamePart(string value)
    {
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalidChar, '_');
        }

        return value;
    }

    private static string FormatNullableMs(long? ms)
    {
        return ms is null ? "null" : FormatMs(ms.Value);
    }

    private static string FormatMs(long ms)
    {
        var time = TimeSpan.FromMilliseconds(ms);

        return time.Hours > 0
            ? time.ToString(@"h\:mm\:ss\.fff")
            : time.ToString(@"m\:ss\.fff");
    }
}

public sealed record RunnerExternalTiming
{
    public required long OfficialElapsedMs { get; init; }
    public required RunTimingSource TimingSource { get; init; }
    public long? LiveSplitRealTimeMs { get; init; }
    public long? LiveSplitGameTimeMs { get; init; }
    public string? SourceEventId { get; init; }
    public DateTimeOffset? SourceOccurredAtUtc { get; init; }
}

public sealed record RunnerSessionOptions
{
    public required string ServerUrl { get; init; }
    public string? RunId { get; init; }
    public required string ConfigPath { get; init; }
    public required string RunnerId { get; init; }
    public string? RunKey { get; init; }
    public string? LogPath { get; init; }
    public required bool ExplicitLogPath { get; init; }
    public required string RunnerClientId { get; init; }
}

public sealed record RunnerSessionCreateResult
{
    public RunnerSession? Session { get; init; }
    public required IReadOnlyList<string> Messages { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }

    public bool Success => Session is not null;
}

public sealed record RunnerServerStateRefreshResult
{
    public required bool Success { get; init; }
    public required bool Applied { get; init; }
    public string? Message { get; init; }
    public long StateVersion { get; init; }
    public bool AdminControlMode { get; init; }
    public string? Status { get; init; }
    public int? LastCompletedSplitIndex { get; init; }

    public static RunnerServerStateRefreshResult Skipped(string message) => new()
    {
        Success = true,
        Applied = false,
        Message = message
    };

    public static RunnerServerStateRefreshResult Failed(string message) => new()
    {
        Success = false,
        Applied = false,
        Message = message
    };

    public static RunnerServerStateRefreshResult NoChange(long stateVersion, bool adminControlMode) => new()
    {
        Success = true,
        Applied = false,
        StateVersion = stateVersion,
        AdminControlMode = adminControlMode
    };

    public static RunnerServerStateRefreshResult CreateApplied(
        long stateVersion,
        bool adminControlMode,
        string status,
        int lastCompletedSplitIndex) => new()
    {
        Success = true,
        Applied = true,
        StateVersion = stateVersion,
        AdminControlMode = adminControlMode,
        Status = status,
        LastCompletedSplitIndex = lastCompletedSplitIndex
    };
}

public sealed record RunnerCompletedSplit
{
    public required int SplitIndex { get; init; }
    public required string Name { get; init; }
    public required long ClientElapsedMs { get; init; }
    public required string ClientElapsed { get; init; }
}

public sealed record RunnerSessionActionResult
{
    public required string EventName { get; init; }
    public long ClientElapsedMs { get; init; }
    public required bool LocalAccepted { get; init; }
    public string? LocalRejectReason { get; init; }
    public ServerEventResponse? ServerResponse { get; init; }
    public bool FatalServerMismatch { get; init; }

    public static RunnerSessionActionResult LocalRejected(string eventName, string rejectReason)
    {
        return new RunnerSessionActionResult
        {
            EventName = eventName,
            ClientElapsedMs = 0,
            LocalAccepted = false,
            LocalRejectReason = rejectReason,
            ServerResponse = null,
            FatalServerMismatch = false
        };
    }
}