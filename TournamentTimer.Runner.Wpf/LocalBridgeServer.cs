using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TournamentTimer.Runner.Wpf;

public sealed class LocalBridgeServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly int _port;
    private readonly Func<LocalBridgeEventRequest, Task<LocalBridgeEventResponse>> _handleEventAsync;
    private readonly Action<string> _log;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;

    public LocalBridgeServer(
        int port,
        Func<LocalBridgeEventRequest, Task<LocalBridgeEventResponse>> handleEventAsync,
        Action<string> log)
    {
        _port = port;
        _handleEventAsync = handleEventAsync;
        _log = log;
    }

    public Task StartAsync()
    {
        if (_listener is not null)
        {
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();

        _acceptLoopTask = AcceptLoopAsync(_cts.Token);

        _log($"Local bridge listening on http://127.0.0.1:{_port}/api/local/livesplit/events");

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_listener is null)
        {
            return;
        }

        _cts?.Cancel();
        _listener.Stop();

        if (_acceptLoopTask is not null)
        {
            try
            {
                await _acceptLoopTask;
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
            catch (ObjectDisposedException)
            {
                // Expected.
            }
        }

        _listener = null;
        _acceptLoopTask = null;

        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _ = Task.Run(
                async () => await HandleClientAsync(client, cancellationToken),
                cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        await using var stream = client.GetStream();

        try
        {
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);

            var requestLine = await reader.ReadLineAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(requestLine))
            {
                await WriteJsonAsync(stream, 400, new { error = "bad_request" }, cancellationToken);
                return;
            }

            var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 2)
            {
                await WriteJsonAsync(stream, 400, new { error = "bad_request" }, cancellationToken);
                return;
            }

            var method = parts[0];
            var path = parts[1].Split('?', 2)[0];

            var contentLength = 0;

            while (true)
            {
                var headerLine = await reader.ReadLineAsync(cancellationToken);

                if (headerLine is null)
                {
                    return;
                }

                if (headerLine.Length == 0)
                {
                    break;
                }

                var separatorIndex = headerLine.IndexOf(':');

                if (separatorIndex <= 0)
                {
                    continue;
                }

                var headerName = headerLine[..separatorIndex].Trim();
                var headerValue = headerLine[(separatorIndex + 1)..].Trim();

                if (string.Equals(headerName, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(headerValue, out contentLength);
                }
            }

            if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(stream, 405, new { error = "method_not_allowed" }, cancellationToken);
                return;
            }

            if (!string.Equals(path, "/api/local/livesplit/events", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(stream, 404, new { error = "not_found" }, cancellationToken);
                return;
            }

            if (contentLength <= 0)
            {
                await WriteJsonAsync(stream, 400, new { error = "empty_body" }, cancellationToken);
                return;
            }

            var bodyBuffer = new char[contentLength];
            var read = 0;

            while (read < contentLength)
            {
                var count = await reader.ReadAsync(
                    bodyBuffer.AsMemory(read, contentLength - read),
                    cancellationToken);

                if (count == 0)
                {
                    break;
                }

                read += count;
            }

            var body = new string(bodyBuffer, 0, read);
            var request = JsonSerializer.Deserialize<LocalBridgeEventRequest>(body, JsonOptions);

            if (request is null)
            {
                await WriteJsonAsync(stream, 400, new { error = "invalid_json" }, cancellationToken);
                return;
            }

            var response = await _handleEventAsync(request);
            await WriteJsonAsync(stream, 200, response, cancellationToken);
        }
        catch (JsonException ex)
        {
            await WriteJsonAsync(stream, 400, new { error = "invalid_json", message = ex.Message }, cancellationToken);
        }
        catch (Exception ex)
        {
            _log($"Local bridge error: {ex.Message}");
            await WriteJsonAsync(stream, 500, new { error = "bridge_error", message = ex.Message }, cancellationToken);
        }
    }

    private static async Task WriteJsonAsync(
        Stream stream,
        int statusCode,
        object payload,
        CancellationToken cancellationToken)
    {
        var reason = statusCode switch
        {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            405 => "Method Not Allowed",
            500 => "Internal Server Error",
            _ => "OK"
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bodyBytes = Encoding.UTF8.GetBytes(json);

        var header =
            $"HTTP/1.1 {statusCode} {reason}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\n" +
            "\r\n";

        var headerBytes = Encoding.ASCII.GetBytes(header);

        await stream.WriteAsync(headerBytes, cancellationToken);
        await stream.WriteAsync(bodyBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}

public sealed record LocalBridgeEventRequest
{
    public int ProtocolVersion { get; init; } = 1;
    public string Source { get; init; } = "livesplit";
    public required string EventType { get; init; }
    public string? SourceEventId { get; init; }
    public int? SplitIndex { get; init; }
    public string? SplitName { get; init; }
    public long? LiveSplitTimeMs { get; init; }
    public long? LiveSplitRealTimeMs { get; init; }
    public long? LiveSplitGameTimeMs { get; init; }
    public string? TimerPhase { get; init; }
    public DateTimeOffset? OccurredAtUtc { get; init; }
}

public sealed record LocalBridgeEventResponse
{
    public int ProtocolVersion { get; init; } = 1;
    public required bool Accepted { get; init; }
    public bool AlreadyProcessed { get; init; }
    public required string Status { get; init; }
    public required int LastCompletedSplitIndex { get; init; }
    public required long ClientElapsedMs { get; init; }
    public string? Message { get; init; }
}