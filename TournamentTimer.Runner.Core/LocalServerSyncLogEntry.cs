using TournamentTimer.Core;

namespace TournamentTimer.Runner;

public sealed record LocalServerSyncLogEntry
{
    public required string RunId { get; init; }

    public required string AttemptId { get; init; }
    public required string RunnerId { get; init; }

    public required string ClientEventId { get; init; }
    public required RunEventKind Kind { get; init; }
    public int? SplitIndex { get; init; }
    public required long ClientElapsedMs { get; init; }

    public RunTimingSource TimingSource { get; init; } = RunTimingSource.RunnerStopwatch;
    public long? LiveSplitRealTimeMs { get; init; }
    public long? LiveSplitGameTimeMs { get; init; }
    public string? SourceEventId { get; init; }
    public DateTimeOffset? SourceOccurredAtUtc { get; init; }

    public required bool TransportSucceeded { get; init; }
    public string? TransportError { get; init; }

    public required bool ServerAccepted { get; init; }
    public required bool AlreadyProcessed { get; init; }
    public string? ServerRejectReason { get; init; }

    public string? ServerStatus { get; init; }
    public long? ServerFinishedAtMs { get; init; }

    public required DateTimeOffset SyncedAtUtc { get; init; }
}