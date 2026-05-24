using System.Text.Json;
using System.Text.Json.Serialization;
using TournamentTimer.Core;

namespace TournamentTimer.Runner;

public sealed class LocalServerSyncLogWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public string FilePath { get; }

    public LocalServerSyncLogWriter(string filePath)
    {
        FilePath = filePath;

        var directory = Path.GetDirectoryName(filePath);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public void AppendSyncAttempt(
    RunConfig config,
    string attemptId,
    string runnerId,
    RunEvent runEvent,
    ServerEventResponse response)
    {
        var entry = new LocalServerSyncLogEntry
        {
            RunId = config.RunId,
            AttemptId = attemptId,
            RunnerId = runnerId,

            ClientEventId = runEvent.ClientEventId,
            Kind = GetKind(runEvent),
            SplitIndex = runEvent is SplitRunEvent split ? split.SplitIndex : null,
            ClientElapsedMs = runEvent.ClientElapsedMs,

            TimingSource = runEvent.TimingSource,
            LiveSplitRealTimeMs = runEvent.LiveSplitRealTimeMs,
            LiveSplitGameTimeMs = runEvent.LiveSplitGameTimeMs,
            SourceEventId = runEvent.SourceEventId,
            SourceOccurredAtUtc = runEvent.SourceOccurredAtUtc,

            TransportSucceeded = response.Sent,
            TransportError = response.TransportError,

            ServerAccepted = response.Sent && response.Accepted,
            AlreadyProcessed = response.AlreadyProcessed,
            ServerRejectReason = response.RejectReason,

            ServerStatus = response.Status,
            ServerFinishedAtMs = response.FinishedAtMs,

            SyncedAtUtc = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(entry, JsonOptions);

        File.AppendAllText(FilePath, json + Environment.NewLine);
    }



    public IReadOnlySet<string> ReadSuccessfullySyncedClientEventIds(
        string filePath,
        string runId,
        string attemptId,
        string runnerId)
    {
        if (!File.Exists(filePath))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            LocalServerSyncLogEntry? entry;

            try
            {
                entry = JsonSerializer.Deserialize<LocalServerSyncLogEntry>(line, JsonOptions);
            }
            catch
            {
                continue;
            }

            if (entry is null ||
                !entry.TransportSucceeded ||
                !entry.ServerAccepted ||
                string.IsNullOrWhiteSpace(entry.ClientEventId) ||
                !string.Equals(entry.RunId, runId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(entry.AttemptId, attemptId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(NormalizeRunnerId(entry.RunnerId), NormalizeRunnerId(runnerId), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(entry.ClientEventId);
        }

        return result;
    }

    private static string NormalizeRunnerId(string? runnerId)
    {
        return string.IsNullOrWhiteSpace(runnerId)
            ? "runner-1"
            : runnerId.Trim();
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