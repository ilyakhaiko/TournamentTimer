using System.Text.Json;
using System.Text.Json.Serialization;
using TournamentTimer.Core;

namespace TournamentTimer.Runner;

public sealed class LocalRunLogWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public string FilePath { get; }

    public LocalRunLogWriter(string filePath)
    {
        FilePath = filePath;

        var directory = Path.GetDirectoryName(filePath);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public void AppendEventAttempt(
    RunConfig config,
    string attemptId,
    string runnerId,
    RunEvent runEvent,
    ApplyEventResult result)
    {
        var entry = new LocalRunLogEntry
        {
            RunId = config.RunId,
            AttemptId = attemptId,
            RunnerId = runnerId,
            Game = config.Game,
            Category = config.Category,
            TimingMode = config.TimingMode,

            ClientEventId = runEvent.ClientEventId,
            Kind = GetKind(runEvent),
            SplitIndex = runEvent is SplitRunEvent split ? split.SplitIndex : null,

            ClientElapsedMs = runEvent.ClientElapsedMs,

            TimingSource = runEvent.TimingSource,
            LiveSplitRealTimeMs = runEvent.LiveSplitRealTimeMs,
            LiveSplitGameTimeMs = runEvent.LiveSplitGameTimeMs,
            SourceEventId = runEvent.SourceEventId,
            SourceOccurredAtUtc = runEvent.SourceOccurredAtUtc,

            Accepted = result.Accepted,
            RejectReason = result.RejectReason,

            LoggedAtUtc = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(entry, JsonOptions);

        File.AppendAllText(FilePath, json + Environment.NewLine);
    }

    private static RunEventKind GetKind(RunEvent runEvent)
    {
        return runEvent switch
        {
            StartRunEvent => RunEventKind.Start,
            SplitRunEvent => RunEventKind.Split,
            FinishRunEvent => RunEventKind.Finish,
            _ => throw new InvalidOperationException("Unknown run event type.")
        };
    }
}