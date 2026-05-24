namespace TournamentTimer.Core;

public sealed record RunConfig
{
    public required string RunId { get; init; }
    public required string Game { get; init; }
    public required string Category { get; init; }
    public required TimingMode TimingMode { get; init; }

    public IReadOnlyList<SplitDefinition> Splits { get; init; } = Array.Empty<SplitDefinition>();

    public bool RequireAllSplitsBeforeFinish { get; init; } = true;

    public bool FinishOnLastSplit { get; init; } = true;

    public int MinimumMsBetweenSplits { get; init; } = 500;
}