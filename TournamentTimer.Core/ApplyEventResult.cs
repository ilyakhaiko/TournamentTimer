namespace TournamentTimer.Core;

public sealed record ApplyEventResult
{
    public required bool Accepted { get; init; }
    public required RunState State { get; init; }

    public string? RejectReason { get; init; }

    public static ApplyEventResult Accept(RunState state) => new()
    {
        Accepted = true,
        State = state
    };

    public static ApplyEventResult Reject(RunState state, string reason) => new()
    {
        Accepted = false,
        State = state,
        RejectReason = reason
    };
}
