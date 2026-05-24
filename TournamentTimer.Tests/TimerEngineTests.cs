using TournamentTimer.Core;

namespace TournamentTimer.Tests;

public sealed class TimerEngineTests
{
    private static readonly RunConfig TestConfig = new()
    {
        RunId = "run-1",
        Game = "Test Game",
        Category = "Any%",
        TimingMode = TimingMode.Rta,
        Splits =
        [
            new SplitDefinition { Index = 0, Name = "Chapter 1" },
            new SplitDefinition { Index = 1, Name = "Boss" },
            new SplitDefinition { Index = 2, Name = "Final" }
        ]
    };

    private readonly TimerEngine _engine = new();

    [Fact]
    public void Start_IsAccepted_WhenRunIsReady()
    {
        var result = _engine.ApplyEvent(
            TestConfig,
            RunState.Ready,
            new StartRunEvent("start-1"));

        Assert.True(result.Accepted);
        Assert.Equal(RunStatus.Running, result.State.Status);
        Assert.Single(result.State.Events);
    }

    [Fact]
    public void Split_IsRejected_WhenRunHasNotStarted()
    {
        var result = _engine.ApplyEvent(
            TestConfig,
            RunState.Ready,
            new SplitRunEvent("split-1", SplitIndex: 0, ClientElapsedMs: 1000));

        Assert.False(result.Accepted);
        Assert.Equal("run_not_started", result.RejectReason);
    }

    [Fact]
    public void SequentialSplitsAndFinish_AreAccepted()
    {
        var state = RunState.Ready;

        state = _engine.ApplyEvent(TestConfig, state, new StartRunEvent("start-1")).State;
        state = _engine.ApplyEvent(TestConfig, state, new SplitRunEvent("split-1", 0, 1000)).State;
        state = _engine.ApplyEvent(TestConfig, state, new SplitRunEvent("split-2", 1, 2500)).State;
        var finalSplit = _engine.ApplyEvent(TestConfig, state, new SplitRunEvent("split-3", 2, 4000));

        Assert.True(finalSplit.Accepted);
        Assert.Equal(RunStatus.Finished, finalSplit.State.Status);
        Assert.Equal(4000, finalSplit.State.FinishedAtMs);
        Assert.Equal(4, finalSplit.State.Events.Count);
    }

    [Fact]
    public void DuplicateClientEventId_IsRejected()
    {
        var state = RunState.Ready;

        state = _engine.ApplyEvent(TestConfig, state, new StartRunEvent("same-id")).State;

        var duplicate = _engine.ApplyEvent(
            TestConfig,
            state,
            new SplitRunEvent("same-id", 0, 1000));

        Assert.False(duplicate.Accepted);
        Assert.Equal("duplicate_client_event_id", duplicate.RejectReason);
    }

    [Fact]
    public void DuplicateSplit_IsRejected()
    {
        var state = RunState.Ready;

        state = _engine.ApplyEvent(TestConfig, state, new StartRunEvent("start-1")).State;
        state = _engine.ApplyEvent(TestConfig, state, new SplitRunEvent("split-1", 0, 1000)).State;

        var duplicateSplit = _engine.ApplyEvent(
            TestConfig,
            state,
            new SplitRunEvent("split-2", 0, 1100));

        Assert.False(duplicateSplit.Accepted);
        Assert.Equal("split_already_recorded", duplicateSplit.RejectReason);
    }

    [Fact]
    public void OutOfOrderSplit_IsRejected()
    {
        var state = RunState.Ready;

        state = _engine.ApplyEvent(TestConfig, state, new StartRunEvent("start-1")).State;

        var result = _engine.ApplyEvent(
            TestConfig,
            state,
            new SplitRunEvent("split-2", 1, 2000));

        Assert.False(result.Accepted);
        Assert.Equal("split_out_of_order", result.RejectReason);
    }

    [Fact]
    public void FinishBeforeAllSplits_IsRejected()
    {
        var state = RunState.Ready;

        state = _engine.ApplyEvent(TestConfig, state, new StartRunEvent("start-1")).State;
        state = _engine.ApplyEvent(TestConfig, state, new SplitRunEvent("split-1", 0, 1000)).State;

        var finish = _engine.ApplyEvent(
            TestConfig,
            state,
            new FinishRunEvent("finish-1", 3000));

        Assert.False(finish.Accepted);
        Assert.Equal("finish_before_all_splits", finish.RejectReason);
    }

    [Fact]
    public void TimeGoingBack_IsRejected()
    {
        var state = RunState.Ready;

        state = _engine.ApplyEvent(TestConfig, state, new StartRunEvent("start-1")).State;
        state = _engine.ApplyEvent(TestConfig, state, new SplitRunEvent("split-1", 0, 2000)).State;

        var result = _engine.ApplyEvent(
            TestConfig,
            state,
            new SplitRunEvent("split-2", 1, 1500));

        Assert.False(result.Accepted);
        Assert.Equal("time_went_back", result.RejectReason);
    }

    [Fact]
    public void LastSplit_FinishesRun_WhenFinishOnLastSplitIsEnabled()
    {
        var state = RunState.Ready;

        state = _engine.ApplyEvent(TestConfig, state, new StartRunEvent("start-1")).State;
        state = _engine.ApplyEvent(TestConfig, state, new SplitRunEvent("split-1", 0, 1000)).State;
        state = _engine.ApplyEvent(TestConfig, state, new SplitRunEvent("split-2", 1, 2000)).State;

        var finalSplit = _engine.ApplyEvent(
            TestConfig,
            state,
            new SplitRunEvent("split-3", 2, 3000));

        Assert.True(finalSplit.Accepted);
        Assert.Equal(RunStatus.Finished, finalSplit.State.Status);
        Assert.Equal(3000, finalSplit.State.FinishedAtMs);
    }

    [Fact]
    public void SeparateRunnerStates_StartOnRunnerOne_DoesNotStartRunnerTwo()
    {
        var runner1State = RunState.Ready;
        var runner2State = RunState.Ready;

        var runner1Start = _engine.ApplyEvent(
            TestConfig,
            runner1State,
            new StartRunEvent("runner-1-start"));

        runner1State = runner1Start.State;

        Assert.True(runner1Start.Accepted);
        Assert.Equal(RunStatus.Running, runner1State.Status);

        Assert.Equal(RunStatus.Ready, runner2State.Status);
        Assert.Empty(runner2State.Events);
        Assert.Equal(-1, runner2State.LastCompletedSplitIndex);
    }

    [Fact]
    public void SeparateRunnerStates_SplitOnRunnerTwo_DoesNotAdvanceRunnerOne()
    {
        var runner1State = RunState.Ready;
        var runner2State = RunState.Ready;

        runner1State = _engine.ApplyEvent(
            TestConfig,
            runner1State,
            new StartRunEvent("runner-1-start")).State;

        runner2State = _engine.ApplyEvent(
            TestConfig,
            runner2State,
            new StartRunEvent("runner-2-start")).State;

        var runner2Split = _engine.ApplyEvent(
            TestConfig,
            runner2State,
            new SplitRunEvent("runner-2-split-1", 0, 1200));

        runner2State = runner2Split.State;

        Assert.True(runner2Split.Accepted);

        Assert.Equal(RunStatus.Running, runner1State.Status);
        Assert.Equal(-1, runner1State.LastCompletedSplitIndex);
        Assert.Single(runner1State.Events);

        Assert.Equal(RunStatus.Running, runner2State.Status);
        Assert.Equal(0, runner2State.LastCompletedSplitIndex);
        Assert.Equal(2, runner2State.Events.Count);
    }

    [Fact]
    public void DuplicateClientEventId_IsScopedToSeparateRunnerState()
    {
        var runner1State = RunState.Ready;
        var runner2State = RunState.Ready;

        var runner1Start = _engine.ApplyEvent(
            TestConfig,
            runner1State,
            new StartRunEvent("same-start-id"));

        var runner2Start = _engine.ApplyEvent(
            TestConfig,
            runner2State,
            new StartRunEvent("same-start-id"));

        Assert.True(runner1Start.Accepted);
        Assert.True(runner2Start.Accepted);

        Assert.Equal(RunStatus.Running, runner1Start.State.Status);
        Assert.Equal(RunStatus.Running, runner2Start.State.Status);
    }
}
