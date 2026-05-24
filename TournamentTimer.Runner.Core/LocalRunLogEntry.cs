using TournamentTimer.Core;

namespace TournamentTimer.Runner;

public sealed record LocalRunLogEntry
{
    public required string RunId { get; init; }
    public string? AttemptId { get; init; }
    public string? RunnerId { get; init; }
    public required string Game { get; init; }
    public required string Category { get; init; }
    public required TimingMode TimingMode { get; init; }

    public required string ClientEventId { get; init; }
    public required RunEventKind Kind { get; init; }
    public int? SplitIndex { get; init; }

    public required long ClientElapsedMs { get; init; }

    public RunTimingSource TimingSource { get; init; } = RunTimingSource.RunnerStopwatch;
    public long? LiveSplitRealTimeMs { get; init; }
    public long? LiveSplitGameTimeMs { get; init; }
    public string? SourceEventId { get; init; }
    public DateTimeOffset? SourceOccurredAtUtc { get; init; }

    public required bool Accepted { get; init; }
    public string? RejectReason { get; init; }

    public required DateTimeOffset LoggedAtUtc { get; init; }
}