namespace TournamentTimer.Core;

public sealed class TimerEngine
{
    public ApplyEventResult ApplyEvent(RunConfig config, RunState state, RunEvent runEvent)
    {
        if (string.IsNullOrWhiteSpace(runEvent.ClientEventId))
        {
            return ApplyEventResult.Reject(state, "missing_client_event_id");
        }

        if (state.SeenClientEventIds.Contains(runEvent.ClientEventId))
        {
            return ApplyEventResult.Reject(state, "duplicate_client_event_id");
        }

        if (runEvent.ClientElapsedMs < 0)
        {
            return ApplyEventResult.Reject(state, "invalid_elapsed_time");
        }

        return runEvent switch
        {
            StartRunEvent start => ApplyStart(state, start),
            SplitRunEvent split => ApplySplit(config, state, split),
            FinishRunEvent finish => ApplyFinish(config, state, finish),
            _ => ApplyEventResult.Reject(state, "unknown_event_type")
        };
    }

    private static ApplyEventResult ApplyStart(RunState state, StartRunEvent start)
    {
        if (state.Status != RunStatus.Ready)
        {
            return ApplyEventResult.Reject(state, "run_already_started");
        }

        var newState = AppendEvent(state, ToRecordedEvent(start, RunEventKind.Start)) with
        {
            Status = RunStatus.Running
        };

        return ApplyEventResult.Accept(newState);
    }

    private static ApplyEventResult ApplySplit(RunConfig config, RunState state, SplitRunEvent split)
    {
        if (state.Status == RunStatus.Ready)
        {
            return ApplyEventResult.Reject(state, "run_not_started");
        }

        if (state.Status == RunStatus.Finished)
        {
            return ApplyEventResult.Reject(state, "run_already_finished");
        }

        if (split.SplitIndex < 0 || split.SplitIndex >= config.Splits.Count)
        {
            return ApplyEventResult.Reject(state, "invalid_split_index");
        }

        var expectedSplitIndex = state.LastCompletedSplitIndex + 1;

        if (split.SplitIndex < expectedSplitIndex)
        {
            return ApplyEventResult.Reject(state, "split_already_recorded");
        }

        if (split.SplitIndex > expectedSplitIndex)
        {
            return ApplyEventResult.Reject(state, "split_out_of_order");
        }

        if (TimeWentBack(state, split.ClientElapsedMs))
        {
            return ApplyEventResult.Reject(state, "time_went_back");
        }

        if (SplitTooSoon(config, state, split.ClientElapsedMs))
        {
            return ApplyEventResult.Reject(state, "split_too_soon");
        }

        var isLastSplit = split.SplitIndex == config.Splits.Count - 1;

        var newStatus = config.FinishOnLastSplit && isLastSplit
            ? RunStatus.Finished
            : RunStatus.Running;

        long? finishedAtMs = config.FinishOnLastSplit && isLastSplit
            ? split.ClientElapsedMs
            : null;

        var newState = AppendEvent(state, ToRecordedEvent(split, RunEventKind.Split, split.SplitIndex)) with
        {
            LastCompletedSplitIndex = split.SplitIndex,
            Status = newStatus,
            FinishedAtMs = finishedAtMs
        };

        return ApplyEventResult.Accept(newState);
    }

    private static ApplyEventResult ApplyFinish(RunConfig config, RunState state, FinishRunEvent finish)
    {
        if (state.Status == RunStatus.Ready)
        {
            return ApplyEventResult.Reject(state, "run_not_started");
        }

        if (state.Status == RunStatus.Finished)
        {
            return ApplyEventResult.Reject(state, "run_already_finished");
        }

        var lastRequiredSplitIndex = config.Splits.Count - 1;

        if (config.RequireAllSplitsBeforeFinish && state.LastCompletedSplitIndex < lastRequiredSplitIndex)
        {
            return ApplyEventResult.Reject(state, "finish_before_all_splits");
        }

        if (TimeWentBack(state, finish.ClientElapsedMs))
        {
            return ApplyEventResult.Reject(state, "time_went_back");
        }

        var newState = AppendEvent(state, ToRecordedEvent(finish, RunEventKind.Finish)) with
        {
            Status = RunStatus.Finished,
            FinishedAtMs = finish.ClientElapsedMs
        };

        return ApplyEventResult.Accept(newState);
    }


    private static RecordedRunEvent ToRecordedEvent(
        RunEvent runEvent,
        RunEventKind kind,
        int? splitIndex = null)
    {
        return new RecordedRunEvent
        {
            ClientEventId = runEvent.ClientEventId,
            Kind = kind,
            SplitIndex = splitIndex,
            ClientElapsedMs = runEvent.ClientElapsedMs,
            TimingSource = runEvent.TimingSource,
            LiveSplitRealTimeMs = runEvent.LiveSplitRealTimeMs,
            LiveSplitGameTimeMs = runEvent.LiveSplitGameTimeMs,
            SourceEventId = runEvent.SourceEventId,
            SourceOccurredAtUtc = runEvent.SourceOccurredAtUtc
        };
    }

    private static bool TimeWentBack(RunState state, long nextElapsedMs)
    {
        var previousElapsedMs = state.Events.Count == 0
            ? 0
            : state.Events[^1].ClientElapsedMs;

        return nextElapsedMs < previousElapsedMs;
    }

    private static bool SplitTooSoon(RunConfig config, RunState state, long nextElapsedMs)
    {
        var lastSplit = state.Events
            .Where(e => e.Kind == RunEventKind.Split)
            .LastOrDefault();

        if (lastSplit is null)
        {
            return false;
        }

        return nextElapsedMs - lastSplit.ClientElapsedMs < config.MinimumMsBetweenSplits;
    }

    private static RunState AppendEvent(RunState state, RecordedRunEvent recordedEvent)
    {
        var events = state.Events.ToList();
        events.Add(recordedEvent);

        var seenIds = state.SeenClientEventIds.ToHashSet();
        seenIds.Add(recordedEvent.ClientEventId);

        return state with
        {
            Events = events,
            SeenClientEventIds = seenIds
        };
    }
}