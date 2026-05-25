using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TournamentTimer.Core;

namespace TournamentTimer.Runner;

public sealed class ServerEventClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _httpClient;

    public string? LastError { get; private set; }

    public ServerEventClient(string baseUrl, string? runKey = null)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(10)
        };

        var normalizedRunKey = NormalizeAccessKey(runKey)
            ?? NormalizeAccessKey(Environment.GetEnvironmentVariable("TOURNAMENT_TIMER_RUN_KEY"));

        if (normalizedRunKey is not null)
        {
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-Run-Key", normalizedRunKey);
        }
    }

    public async Task<RunConfig?> GetRunConfigAsync(string runId)
    {
        return await GetJsonAsync<RunConfig>(
            $"/api/runs/{runId}",
            "CONFIG LOAD");
    }

    public async Task<RunnerRunStateResponse?> GetRunStateAsync(string runId, string runnerId)
    {
        return await GetJsonAsync<RunnerRunStateResponse>(
            $"/api/runs/{runId}/runners/{Uri.EscapeDataString(runnerId)}/state",
            "STATE LOAD");
    }

    public async Task<RunnerAttemptResponse?> GetAttemptAsync(string runId)
    {
        return await GetJsonAsync<RunnerAttemptResponse>(
            $"/api/runs/{runId}/attempt",
            "ATTEMPT LOAD");
    }

    public async Task<HeartbeatClientResponse> SendHeartbeatAsync(string runId, string attemptId, string runnerId, string clientId)
    {
        var request = new HeartbeatClientRequest
        {
            RunnerId = runnerId,
            AttemptId = attemptId,
            ClientId = clientId
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"/api/runs/{runId}/heartbeat",
                request,
                JsonOptions);

            if (!response.IsSuccessStatusCode)
            {
                return HeartbeatClientResponse.TransportFailed(
                    await BuildHttpErrorAsync("HEARTBEAT", response));
            }

            var result = await response.Content.ReadFromJsonAsync<HeartbeatClientResponse>(JsonOptions);

            return result ?? HeartbeatClientResponse.TransportFailed("empty_server_response");
        }
        catch (Exception ex)
        {
            return HeartbeatClientResponse.TransportFailed(FriendlyException(ex));
        }
    }

    public async Task<ServerEventResponse> SendEventAsync(string runId, string attemptId, string runnerId, string runnerClientId, RunEvent runEvent)
    {
        var request = new ServerEventRequest
        {
            RunnerId = runnerId,
            Type = GetEventType(runEvent),
            AttemptId = attemptId,
            ClientId = runnerClientId,
            ClientEventId = runEvent.ClientEventId,
            SplitIndex = runEvent is SplitRunEvent split ? split.SplitIndex : null,
            ClientElapsedMs = runEvent.ClientElapsedMs,
            TimingSource = runEvent.TimingSource,
            LiveSplitRealTimeMs = runEvent.LiveSplitRealTimeMs,
            LiveSplitGameTimeMs = runEvent.LiveSplitGameTimeMs,
            SourceEventId = runEvent.SourceEventId,
            SourceOccurredAtUtc = runEvent.SourceOccurredAtUtc
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"/api/runs/{runId}/events",
                request,
                JsonOptions);

            if (!response.IsSuccessStatusCode)
            {
                return ServerEventResponse.TransportFailed(
                    await BuildHttpErrorAsync("EVENT SYNC", response));
            }

            var result = await response.Content.ReadFromJsonAsync<ServerEventResponse>(JsonOptions);

            return result ?? ServerEventResponse.TransportFailed("empty_server_response");
        }
        catch (Exception ex)
        {
            return ServerEventResponse.TransportFailed(FriendlyException(ex));
        }
    }


    public async Task<DisplayTimeClientResponse> SendDisplayTimeAsync(
        string runId,
        string attemptId,
        string runnerId,
        string runnerClientId,
        RunnerLiveDisplayUpdate update)
    {
        var request = new DisplayTimeClientRequest
        {
            AttemptId = attemptId,
            ClientId = runnerClientId,
            DisplayElapsedMs = update.DisplayElapsedMs,
            TimingSource = update.TimingSource,
            LiveSplitRealTimeMs = update.LiveSplitRealTimeMs,
            LiveSplitGameTimeMs = update.LiveSplitGameTimeMs,
            GameTimeRunning = update.GameTimeRunning,
            SourceEventId = update.SourceEventId,
            SourceOccurredAtUtc = update.SourceOccurredAtUtc
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"/api/runs/{runId}/runners/{Uri.EscapeDataString(runnerId)}/display-time",
                request,
                JsonOptions);

            if (!response.IsSuccessStatusCode)
            {
                return DisplayTimeClientResponse.TransportFailed(
                    await BuildHttpErrorAsync("DISPLAY TIME", response));
            }

            var result = await response.Content.ReadFromJsonAsync<DisplayTimeClientResponse>(JsonOptions);

            return result ?? DisplayTimeClientResponse.TransportFailed("empty_server_response");
        }
        catch (Exception ex)
        {
            return DisplayTimeClientResponse.TransportFailed(FriendlyException(ex));
        }
    }

    public async Task<InputLockClientResponse> ReportInputLockAsync(
        string runId,
        string attemptId,
        string runnerId,
        string reason,
        string? sourceEventId)
    {
        var request = new InputLockClientRequest
        {
            AttemptId = attemptId,
            Source = "livesplit",
            SourceEventId = sourceEventId,
            Reason = reason
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"/api/runs/{runId}/runners/{Uri.EscapeDataString(runnerId)}/input-lock",
                request,
                JsonOptions);

            if (!response.IsSuccessStatusCode)
            {
                return InputLockClientResponse.TransportFailed(
                    await BuildHttpErrorAsync("INPUT LOCK", response));
            }

            var result = await response.Content.ReadFromJsonAsync<InputLockClientResponse>(JsonOptions);

            return result ?? InputLockClientResponse.TransportFailed("empty_server_response");
        }
        catch (Exception ex)
        {
            return InputLockClientResponse.TransportFailed(FriendlyException(ex));
        }
    }

    private async Task<T?> GetJsonAsync<T>(string path, string operation)
    {
        LastError = null;

        try
        {
            var response = await _httpClient.GetAsync(path);

            if (!response.IsSuccessStatusCode)
            {
                LastError = await BuildHttpErrorAsync(operation, response);
                Console.WriteLine($"{operation} ERROR: {LastError}");
                return default;
            }

            var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions);

            if (result is null)
            {
                LastError = $"{operation}: empty server response.";
                Console.WriteLine($"{operation} ERROR: {LastError}");
                return default;
            }

            return result;
        }
        catch (Exception ex)
        {
            LastError = $"{operation}: {FriendlyException(ex)}";
            Console.WriteLine($"{operation} ERROR: {LastError}");
            return default;
        }
    }

    private static async Task<string> BuildHttpErrorAsync(string operation, HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var errorCode = TryReadErrorCode(body);
        var hint = GetHttpErrorHint((int)response.StatusCode, errorCode);
        var bodyText = string.IsNullOrWhiteSpace(errorCode)
            ? TrimForLog(body)
            : errorCode;

        var result = $"{operation}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

        if (!string.IsNullOrWhiteSpace(hint))
        {
            result += $". {hint}";
        }

        if (!string.IsNullOrWhiteSpace(bodyText))
        {
            result += $" ({bodyText})";
        }

        return result;
    }

    private static string GetHttpErrorHint(int statusCode, string? errorCode)
    {
        if (statusCode == 401)
        {
            return errorCode switch
            {
                "run_key_required" => "Wrong or missing Run key.",
                "admin_key_required" => "Wrong or missing Admin key.",
                "view_key_required" => "Wrong or missing View key.",
                "access_key_required" => "Wrong or missing access key.",
                _ => "Wrong or missing access key."
            };
        }

        if (statusCode == 404)
        {
            return "Wrong RunId, wrong Server URL, or the server is running another config.";
        }

        if (statusCode >= 500)
        {
            return "Server error. Check the server console.";
        }

        return "";
    }

    private static string? TryReadErrorCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var errorElement) &&
                errorElement.ValueKind == JsonValueKind.String)
            {
                return errorElement.GetString();
            }
        }
        catch
        {
            // Response body is not JSON.
        }

        return null;
    }

    private static string TrimForLog(string value)
    {
        value = value.Replace("\r", " ").Replace("\n", " ").Trim();

        return value.Length <= 180
            ? value
            : value[..180] + "...";
    }

    private static string FriendlyException(Exception ex)
    {
        return ex switch
        {
            HttpRequestException => $"Cannot reach server. Check Server URL and whether the server is running. Details: {ex.Message}",
            TaskCanceledException => "Server request timed out. Check Server URL/network/firewall.",
            _ => ex.Message
        };
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private static string? NormalizeAccessKey(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static string GetEventType(RunEvent runEvent)
    {
        return runEvent switch
        {
            StartRunEvent => "start",
            SplitRunEvent => "split",
            FinishRunEvent => "finish",
            _ => throw new InvalidOperationException("Unknown run event type.")
        };
    }
}

public sealed record RunnerRunStateResponse
{
    public int StateApiVersion { get; init; }
    public string? RunId { get; init; }
    public string? AttemptId { get; init; }
    public string? RunnerId { get; init; }
    public required string Status { get; init; }
    public required int LastCompletedSplitIndex { get; init; }
    public long? FinishedAtMs { get; init; }
    public long DisplayElapsedMs { get; init; }
    public long ClientElapsedMs { get; init; }
    public long ServerElapsedMs { get; init; }
    public long TimerDeltaMs { get; init; }
    public IReadOnlyList<CompletedSplitClientResponse> CompletedSplits { get; init; } = Array.Empty<CompletedSplitClientResponse>();
    public long StateVersion { get; init; }
    public bool AdminControlMode { get; init; }
}

public sealed record CompletedSplitClientResponse
{
    public int SplitIndex { get; init; }
    public string? Name { get; init; }
    public long ClientElapsedMs { get; init; }
    public string? ClientElapsed { get; init; }
    public long ServerElapsedMs { get; init; }
    public string? ServerElapsed { get; init; }
    public long DeltaMs { get; init; }
    public string? Delta { get; init; }
    public DateTimeOffset ServerReceivedAtUtc { get; init; }
}

public sealed record ServerEventRequest
{
    public required string Type { get; init; }
    public required string AttemptId { get; init; }
    public required string RunnerId { get; init; }
    public required string ClientId { get; init; }
    public required string ClientEventId { get; init; }
    public int? SplitIndex { get; init; }
    public required long ClientElapsedMs { get; init; }
    public RunTimingSource TimingSource { get; init; } = RunTimingSource.RunnerStopwatch;
    public long? LiveSplitRealTimeMs { get; init; }
    public long? LiveSplitGameTimeMs { get; init; }
    public string? SourceEventId { get; init; }
    public DateTimeOffset? SourceOccurredAtUtc { get; init; }
}

public sealed record ServerEventResponse
{
    public bool Accepted { get; init; }
    public bool AlreadyProcessed { get; init; }
    public string? RejectReason { get; init; }
    public string? RunnerId { get; init; }
    public string? Status { get; init; }
    public int LastCompletedSplitIndex { get; init; }
    public long? FinishedAtMs { get; init; }
    public long StateVersion { get; init; }
    public bool AdminControlMode { get; init; }

    public string? TransportError { get; init; }

    public bool Sent => TransportError is null;

    public static ServerEventResponse TransportFailed(string error) => new()
    {
        Accepted = false,
        TransportError = error
    };
}

public sealed record HeartbeatClientRequest
{
    public required string ClientId { get; init; }
    public required string AttemptId { get; init; }
    public required string RunnerId { get; init; }
}

public sealed record HeartbeatClientResponse
{
    public bool Accepted { get; init; }
    public string? Status { get; init; }
    public DateTimeOffset? ServerReceivedAtUtc { get; init; }

    public string? TransportError { get; init; }
    public string? RunnerId { get; init; }

    public string? RejectReason { get; init; }
    public string? ActiveClientId { get; init; }

    public bool Sent => TransportError is null;

    public static HeartbeatClientResponse TransportFailed(string error) => new()
    {
        Accepted = false,
        TransportError = error
    };
}

public sealed record DisplayTimeClientRequest
{
    public required string AttemptId { get; init; }
    public required string ClientId { get; init; }
    public required long DisplayElapsedMs { get; init; }
    public required RunTimingSource TimingSource { get; init; }
    public long? LiveSplitRealTimeMs { get; init; }
    public long? LiveSplitGameTimeMs { get; init; }
    public bool GameTimeRunning { get; init; } = true;
    public string? SourceEventId { get; init; }
    public DateTimeOffset? SourceOccurredAtUtc { get; init; }
}

public sealed record DisplayTimeClientResponse
{
    public string? RunId { get; init; }
    public string? AttemptId { get; init; }
    public string? RunnerId { get; init; }
    public bool Accepted { get; init; }
    public string? RejectReason { get; init; }
    public required long DisplayElapsedMs { get; init; }
    public required RunTimingSource TimingSource { get; init; }
    public required bool DisplayAutoAdvance { get; init; }
    public DateTimeOffset? DisplayUpdatedAtUtc { get; init; }

    public string? TransportError { get; init; }
    public bool Sent => TransportError is null;

    public static DisplayTimeClientResponse TransportFailed(string error) => new()
    {
        Accepted = false,
        DisplayElapsedMs = 0,
        TimingSource = RunTimingSource.RunnerStopwatch,
        DisplayAutoAdvance = false,
        TransportError = error
    };
}

public sealed record InputLockClientRequest
{
    public required string AttemptId { get; init; }
    public string? Source { get; init; }
    public string? SourceEventId { get; init; }
    public required string Reason { get; init; }
}

public sealed record InputLockClientResponse
{
    public string? RunId { get; init; }
    public string? AttemptId { get; init; }
    public string? RunnerId { get; init; }
    public bool Accepted { get; init; }
    public string? RejectReason { get; init; }
    public string? Status { get; init; }
    public int LastCompletedSplitIndex { get; init; }
    public long? FinishedAtMs { get; init; }
    public long StateVersion { get; init; }
    public bool AdminControlMode { get; init; }

    public string? TransportError { get; init; }
    public bool Sent => TransportError is null;

    public static InputLockClientResponse TransportFailed(string error) => new()
    {
        Accepted = false,
        TransportError = error
    };
}

public sealed record RunnerAttemptResponse
{
    public required string RunId { get; init; }
    public required string AttemptId { get; init; }
    public required string Status { get; init; }
    public required int LastCompletedSplitIndex { get; init; }
    public long? FinishedAtMs { get; init; }
}