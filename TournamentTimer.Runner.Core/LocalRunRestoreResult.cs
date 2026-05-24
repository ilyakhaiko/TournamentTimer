using TournamentTimer.Core;

namespace TournamentTimer.Runner;

public sealed record LocalRunRestoreResult
{
    public required RunState State { get; init; }
    public required long BaseElapsedMs { get; init; }
}