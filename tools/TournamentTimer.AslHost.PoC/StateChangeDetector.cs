using System;
using LiveSplit.Model;

internal sealed class StateChangeDetector
{
    private readonly IRun _run;
    private readonly RunnerBridgeEventSink _sink;

    private bool _startSent;
    private int _lastSentCompletedSplitIndex = -1;

    public StateChangeDetector(IRun run, RunnerBridgeEventSink sink)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public Snapshot Capture(LiveSplitState state)
    {
        return new Snapshot(
            state.CurrentPhase.ToString(),
            state.CurrentSplitIndex);
    }

    public void DetectAndSend(Snapshot before, Snapshot after)
    {
        if (after.Phase == "Running" || after.Phase == "Paused")
        {
            if (!_startSent)
            {
                Console.WriteLine("[detect] START");
                _sink.SendStart();
                _startSent = true;
            }

            var completedUpTo = after.SplitIndex - 1;

            for (var completedIndex = _lastSentCompletedSplitIndex + 1;
                 completedIndex <= completedUpTo;
                 completedIndex++)
            {
                if (completedIndex < 0)
                {
                    continue;
                }

                var name = completedIndex >= 0 && completedIndex < _run.Count
                    ? _run[completedIndex].Name
                    : $"Split {completedIndex + 1}";

                Console.WriteLine($"[detect] SPLIT completed index={completedIndex}, name={name}");
                _sink.SendSplit(completedIndex, name);

                _lastSentCompletedSplitIndex = completedIndex;
            }

            return;
        }

        if (_startSent && after.Phase == "NotRunning")
        {
            Console.WriteLine("[detect] RESET/STOP");
            _sink.SendReset();

            _startSent = false;
            _lastSentCompletedSplitIndex = -1;
        }
    }
}

internal sealed class Snapshot
{
    public Snapshot(string phase, int splitIndex)
    {
        Phase = phase;
        SplitIndex = splitIndex;
    }

    public string Phase { get; }
    public int SplitIndex { get; }
}