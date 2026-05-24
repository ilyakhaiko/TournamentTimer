namespace TournamentTimer.Core;

public sealed record RecordedRunEvent
{
    public required string ClientEventId { get; init; }
    public required RunEventKind Kind { get; init; }
    public required long ClientElapsedMs { get; init; }

    public int? SplitIndex { get; init; }

    public RunTimingSource TimingSource { get; init; } = RunTimingSource.RunnerStopwatch;
    public long? LiveSplitRealTimeMs { get; init; }
    public long? LiveSplitGameTimeMs { get; init; }
    public string? SourceEventId { get; init; }
    public DateTimeOffset? SourceOccurredAtUtc { get; init; }
}
