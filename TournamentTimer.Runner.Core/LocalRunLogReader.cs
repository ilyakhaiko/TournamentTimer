using System.Text.Json;
using System.Text.Json.Serialization;
using TournamentTimer.Core;

namespace TournamentTimer.Runner;

public sealed class LocalRunLogReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly TimerEngine _engine = new();

    public IReadOnlySet<string> ReadRunIds(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new HashSet<string>();
        }

        return ReadEntries(filePath)
            .Select(entry => entry.RunId)
            .Where(runId => !string.IsNullOrWhiteSpace(runId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> ReadAttemptIds(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new HashSet<string>();
        }

        return ReadEntries(filePath)
            .Select(entry => entry.AttemptId)
            .Where(attemptId => !string.IsNullOrWhiteSpace(attemptId))
            .Select(attemptId => attemptId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> ReadRunnerIds(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new HashSet<string>();
        }

        return ReadEntries(filePath)
            .Select(entry => NormalizeRunnerId(entry.RunnerId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public LocalRunRestoreResult Restore(RunConfig config, string runnerId, string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new LocalRunRestoreResult
            {
                State = RunState.Ready,
                BaseElapsedMs = 0
            };
        }

        var state = RunState.Ready;
        long baseElapsedMs = 0;
        DateTimeOffset? lastAcceptedLoggedAtUtc = null;

        foreach (var entry in ReadEntries(filePath))
        {
            if (!entry.Accepted || entry.RunId != config.RunId || !string.Equals(NormalizeRunnerId(entry.RunnerId), runnerId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var runEvent = ToRunEvent(entry);
            var result = _engine.ApplyEvent(config, state, runEvent);

            if (result.Accepted)
            {
                state = result.State;
                baseElapsedMs = entry.ClientElapsedMs;
                lastAcceptedLoggedAtUtc = entry.LoggedAtUtc;
            }
        }

        if (state.Status == RunStatus.Running && lastAcceptedLoggedAtUtc is not null)
        {
            var downtimeMs = (long)(DateTimeOffset.UtcNow - lastAcceptedLoggedAtUtc.Value).TotalMilliseconds;

            if (downtimeMs > 0)
            {
                baseElapsedMs += downtimeMs;
            }
        }

        return new LocalRunRestoreResult
        {
            State = state,
            BaseElapsedMs = baseElapsedMs
        };
    }

    public IReadOnlyList<RunEvent> ReadAcceptedEvents(RunConfig config, string runnerId, string filePath)
    {
        if (!File.Exists(filePath))
        {
            return Array.Empty<RunEvent>();
        }

        return ReadEntries(filePath)
            .Where(entry => entry.Accepted
                && entry.RunId == config.RunId
                && string.Equals(NormalizeRunnerId(entry.RunnerId), runnerId, StringComparison.OrdinalIgnoreCase))
            .Select(ToRunEvent)
            .ToList();
    }

    private static IEnumerable<LocalRunLogEntry> ReadEntries(string filePath)
    {
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var entry = JsonSerializer.Deserialize<LocalRunLogEntry>(line, JsonOptions);

            if (entry is not null)
            {
                yield return entry;
            }
        }
    }

    private static string NormalizeRunnerId(string? runnerId)
    {
        return string.IsNullOrWhiteSpace(runnerId)
            ? "runner-1"
            : runnerId.Trim();
    }

    private static RunEvent ToRunEvent(LocalRunLogEntry entry)
    {
        RunEvent runEvent = entry.Kind switch
        {
            RunEventKind.Start => new StartRunEvent(entry.ClientEventId),

            RunEventKind.Split => new SplitRunEvent(
                entry.ClientEventId,
                entry.SplitIndex ?? throw new InvalidOperationException("Split event has no split index."),
                entry.ClientElapsedMs),

            RunEventKind.Finish => new FinishRunEvent(
                entry.ClientEventId,
                entry.ClientElapsedMs),

            _ => throw new InvalidOperationException("Unknown event kind.")
        };

        return runEvent with
        {
            TimingSource = entry.TimingSource,
            LiveSplitRealTimeMs = entry.LiveSplitRealTimeMs,
            LiveSplitGameTimeMs = entry.LiveSplitGameTimeMs,
            SourceEventId = entry.SourceEventId,
            SourceOccurredAtUtc = entry.SourceOccurredAtUtc
        };
    }
}