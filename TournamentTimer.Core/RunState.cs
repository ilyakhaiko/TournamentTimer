namespace TournamentTimer.Core;

public sealed record RunState
{
    public required RunStatus Status { get; init; }

    /// <summary>
    /// -1 means no split has been completed yet.
    /// </summary>
    public required int LastCompletedSplitIndex { get; init; }

    public long? FinishedAtMs { get; init; }

    public IReadOnlyList<RecordedRunEvent> Events { get; init; } = Array.Empty<RecordedRunEvent>();

    public IReadOnlySet<string> SeenClientEventIds { get; init; } = new HashSet<string>();

    public static RunState Ready => new()
    {
        Status = RunStatus.Ready,
        LastCompletedSplitIndex = -1,
        FinishedAtMs = null,
        Events = Array.Empty<RecordedRunEvent>(),
        SeenClientEventIds = new HashSet<string>()
    };
}
