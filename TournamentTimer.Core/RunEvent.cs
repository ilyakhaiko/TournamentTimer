namespace TournamentTimer.Core;

public abstract record RunEvent(string ClientEventId, long ClientElapsedMs)
{
    public RunTimingSource TimingSource { get; init; } = RunTimingSource.RunnerStopwatch;
    public long? LiveSplitRealTimeMs { get; init; }
    public long? LiveSplitGameTimeMs { get; init; }
    public string? SourceEventId { get; init; }
    public DateTimeOffset? SourceOccurredAtUtc { get; init; }
}

public sealed record StartRunEvent(string ClientEventId)
    : RunEvent(ClientEventId, 0);

public sealed record SplitRunEvent(
    string ClientEventId,
    int SplitIndex,
    long ClientElapsedMs)
    : RunEvent(ClientEventId, ClientElapsedMs);

public sealed record FinishRunEvent(
    string ClientEventId,
    long ClientElapsedMs)
    : RunEvent(ClientEventId, ClientElapsedMs);
