namespace TournamentTimer.Core;

public sealed record SplitDefinition
{
    public required int Index { get; init; }
    public required string Name { get; init; }
}
