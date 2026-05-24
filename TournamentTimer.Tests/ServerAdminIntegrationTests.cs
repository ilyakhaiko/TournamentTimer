using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace TournamentTimer.Tests;

public sealed class ServerAdminIntegrationTests
{
    private const string RunId = "integration-test-run";
    private const string RunnerId = "runner-1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task AdminSplit_enables_admin_control_mode()
    {
        using var factory = new TournamentTimerServerFactory();
        using var client = factory.CreateClient();

        var attemptId = await GetAttemptIdAsync(client);

        await SendRunnerEventAsync(client, attemptId, "start", "client-start-1", null, 0);

        var adminSplit = await PostJsonAsync(
            client,
            $"/api/runs/{RunId}/admin/runners/{RunnerId}/split");

        Assert.True(adminSplit.GetProperty("accepted").GetBoolean());
        Assert.True(adminSplit.GetProperty("adminControlMode").GetBoolean());
        Assert.Equal(0, adminSplit.GetProperty("lastCompletedSplitIndex").GetInt32());
        Assert.Equal("Running", adminSplit.GetProperty("status").GetString());

        var runner = await GetRunnerFromDisplayStateAsync(client);

        Assert.True(runner.GetProperty("adminControlMode").GetBoolean());
        Assert.Equal(0, runner.GetProperty("lastCompletedSplitIndex").GetInt32());
    }

    [Fact]
    public async Task Runner_event_after_admin_control_is_rejected()
    {
        using var factory = new TournamentTimerServerFactory();
        using var client = factory.CreateClient();

        var attemptId = await GetAttemptIdAsync(client);

        await SendRunnerEventAsync(client, attemptId, "start", "client-start-1", null, 0);
        await PostJsonAsync(client, $"/api/runs/{RunId}/admin/runners/{RunnerId}/split");

        var rejected = await SendRunnerEventAsync(
            client,
            attemptId,
            "split",
            "client-split-after-admin-control",
            splitIndex: 1,
            clientElapsedMs: 1_500);

        Assert.False(rejected.GetProperty("accepted").GetBoolean());
        Assert.Equal("admin_control_mode", rejected.GetProperty("rejectReason").GetString());

        var runner = await GetRunnerFromDisplayStateAsync(client);

        Assert.True(runner.GetProperty("adminControlMode").GetBoolean());
        Assert.Equal(0, runner.GetProperty("lastCompletedSplitIndex").GetInt32());
    }

    [Fact]
    public async Task Input_lock_enables_admin_control_without_changing_progress()
    {
        using var factory = new TournamentTimerServerFactory();
        using var client = factory.CreateClient();

        var attemptId = await GetAttemptIdAsync(client);

        await SendRunnerEventAsync(client, attemptId, "start", "client-start-1", null, 0);
        await SendRunnerEventAsync(client, attemptId, "split", "client-split-0", splitIndex: 0, clientElapsedMs: 1_000);

        var beforeLock = await GetRunnerFromDisplayStateAsync(client);
        var beforeElapsedMs = beforeLock.GetProperty("displayElapsedMs").GetInt64();

        var inputLock = await PostJsonAsync(
            client,
            $"/api/runs/{RunId}/runners/{RunnerId}/input-lock",
            new
            {
                attemptId,
                source = "test",
                sourceEventId = "bad-split-99",
                reason = "split_index_mismatch_expected_1_got_99"
            });

        Assert.True(inputLock.GetProperty("accepted").GetBoolean());
        Assert.True(inputLock.GetProperty("adminControlMode").GetBoolean());
        Assert.Equal(0, inputLock.GetProperty("lastCompletedSplitIndex").GetInt32());
        Assert.Equal("Running", inputLock.GetProperty("status").GetString());

        var afterLock = await GetRunnerFromDisplayStateAsync(client);

        Assert.True(afterLock.GetProperty("adminControlMode").GetBoolean());
        Assert.Equal(0, afterLock.GetProperty("lastCompletedSplitIndex").GetInt32());
        Assert.Equal("Running", afterLock.GetProperty("status").GetString());

        // The lock itself must not apply a split or finish.
        Assert.Single(afterLock.GetProperty("completedSplits").EnumerateArray());
        Assert.True(afterLock.GetProperty("displayElapsedMs").GetInt64() >= beforeElapsedMs);
    }

    [Fact]
    public async Task AdminUndo_removes_last_completed_split()
    {
        using var factory = new TournamentTimerServerFactory();
        using var client = factory.CreateClient();

        var attemptId = await GetAttemptIdAsync(client);

        await SendRunnerEventAsync(client, attemptId, "start", "client-start-1", null, 0);
        await SendRunnerEventAsync(client, attemptId, "split", "client-split-0", splitIndex: 0, clientElapsedMs: 1_000);
        await SendRunnerEventAsync(client, attemptId, "split", "client-split-1", splitIndex: 1, clientElapsedMs: 2_000);

        var beforeUndo = await GetRunnerFromDisplayStateAsync(client);
        Assert.Equal(1, beforeUndo.GetProperty("lastCompletedSplitIndex").GetInt32());
        Assert.Equal(2, beforeUndo.GetProperty("completedSplits").GetArrayLength());

        var undo = await PostJsonAsync(
            client,
            $"/api/runs/{RunId}/admin/runners/{RunnerId}/undo");

        Assert.True(undo.GetProperty("accepted").GetBoolean());
        Assert.True(undo.GetProperty("adminControlMode").GetBoolean());
        Assert.Equal(0, undo.GetProperty("lastCompletedSplitIndex").GetInt32());

        var afterUndo = await GetRunnerFromDisplayStateAsync(client);

        Assert.True(afterUndo.GetProperty("adminControlMode").GetBoolean());
        Assert.Equal(0, afterUndo.GetProperty("lastCompletedSplitIndex").GetInt32());
        Assert.Equal(1, afterUndo.GetProperty("completedSplits").GetArrayLength());
    }

    [Fact]
    public async Task AdminFinish_finishes_runner_without_all_splits()
    {
        using var factory = new TournamentTimerServerFactory();
        using var client = factory.CreateClient();

        var attemptId = await GetAttemptIdAsync(client);

        await SendRunnerEventAsync(client, attemptId, "start", "client-start-1", null, 0);
        await SendRunnerEventAsync(client, attemptId, "split", "client-split-0", splitIndex: 0, clientElapsedMs: 1_000);

        var finish = await PostJsonAsync(
            client,
            $"/api/runs/{RunId}/admin/runners/{RunnerId}/finish");

        Assert.True(finish.GetProperty("accepted").GetBoolean());
        Assert.True(finish.GetProperty("adminControlMode").GetBoolean());
        Assert.Equal("Finished", finish.GetProperty("status").GetString());
        Assert.True(finish.GetProperty("finishedAtMs").GetInt64() >= 1_000);

        var runner = await GetRunnerFromDisplayStateAsync(client);

        Assert.True(runner.GetProperty("adminControlMode").GetBoolean());
        Assert.Equal("Finished", runner.GetProperty("status").GetString());
        Assert.True(runner.GetProperty("finishedAtMs").GetInt64() >= 1_000);
    }

    [Fact]
    public async Task NewAttempt_clears_admin_control_and_runner_progress()
    {
        using var factory = new TournamentTimerServerFactory();
        using var client = factory.CreateClient();

        var attemptId = await GetAttemptIdAsync(client);

        await SendRunnerEventAsync(client, attemptId, "start", "client-start-1", null, 0);
        await PostJsonAsync(client, $"/api/runs/{RunId}/admin/runners/{RunnerId}/split");

        var beforeNewAttempt = await GetRunnerFromDisplayStateAsync(client);

        Assert.True(beforeNewAttempt.GetProperty("adminControlMode").GetBoolean());
        Assert.Equal(0, beforeNewAttempt.GetProperty("lastCompletedSplitIndex").GetInt32());

        var newAttempt = await PostJsonAsync(client, $"/api/runs/{RunId}/debug/new-attempt");

        Assert.Equal("new_attempt", newAttempt.GetProperty("status").GetString());

        var afterNewAttempt = await GetRunnerFromDisplayStateAsync(client);

        Assert.False(afterNewAttempt.GetProperty("adminControlMode").GetBoolean());
        Assert.Equal("Ready", afterNewAttempt.GetProperty("status").GetString());
        Assert.Equal(-1, afterNewAttempt.GetProperty("lastCompletedSplitIndex").GetInt32());
        Assert.Equal(0, afterNewAttempt.GetProperty("completedSplits").GetArrayLength());
    }

    private static async Task<string> GetAttemptIdAsync(HttpClient client)
    {
        var attempt = await GetJsonAsync(client, $"/api/runs/{RunId}/attempt");

        return attempt.GetProperty("attemptId").GetString()
            ?? throw new InvalidOperationException("attemptId missing");
    }

    private static Task<JsonElement> SendRunnerEventAsync(
        HttpClient client,
        string attemptId,
        string type,
        string clientEventId,
        int? splitIndex,
        long clientElapsedMs)
    {
        return PostJsonAsync(
            client,
            $"/api/runs/{RunId}/events",
            new
            {
                runnerId = RunnerId,
                type,
                attemptId,
                clientEventId,
                splitIndex,
                clientElapsedMs
            });
    }

    private static async Task<JsonElement> GetRunnerFromDisplayStateAsync(HttpClient client)
    {
        var state = await GetJsonAsync(client, $"/api/runs/{RunId}/display-state");
        var runners = state.GetProperty("runners").EnumerateArray();

        foreach (var runner in runners)
        {
            if (string.Equals(
                    runner.GetProperty("runnerId").GetString(),
                    RunnerId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return runner.Clone();
            }
        }

        throw new InvalidOperationException("runner not found in display state");
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        return await ReadJsonAsync(response);
    }

    private static Task<JsonElement> PostJsonAsync(HttpClient client, string path)
    {
        return PostJsonAsync(client, path, payload: null);
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string path, object? payload)
    {
        HttpResponseMessage response;

        if (payload is null)
        {
            response = await client.PostAsync(path, content: null);
        }
        else
        {
            response = await client.PostAsJsonAsync(path, payload, JsonOptions);
        }

        return await ReadJsonAsync(response);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"HTTP {(int)response.StatusCode}: {body}");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private sealed class TournamentTimerServerFactory : WebApplicationFactory<Program>
    {
        private readonly string _contentRootPath;
        private readonly string _configPath;
        private readonly string _serverRunsRoot;
        private readonly string _runAssetsRoot;

        public TournamentTimerServerFactory()
        {
            _contentRootPath = Path.Combine(
                Path.GetTempPath(),
                "TournamentTimer.Tests",
                Guid.NewGuid().ToString("N"));

            _serverRunsRoot = Path.Combine(_contentRootPath, "server-runs");
            _runAssetsRoot = Path.Combine(_serverRunsRoot, "assets");

            Directory.CreateDirectory(_contentRootPath);
            Directory.CreateDirectory(Path.Combine(_contentRootPath, "wwwroot"));
            Directory.CreateDirectory(_serverRunsRoot);
            Directory.CreateDirectory(_runAssetsRoot);

            _configPath = Path.Combine(_contentRootPath, "integration-test-run.json");

            File.WriteAllText(
                _configPath,
                CreateRunConfigJson(),
                Encoding.UTF8);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(_contentRootPath);

            builder.UseSetting("RunConfigPath", _configPath);
            builder.UseSetting("ServerRunsRoot", _serverRunsRoot);
            builder.UseSetting("RunAssetsRoot", _runAssetsRoot);

            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RunConfigPath"] = _configPath,
                    ["ServerRunsRoot"] = _serverRunsRoot,
                    ["RunAssetsRoot"] = _runAssetsRoot
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            try
            {
                if (Directory.Exists(_contentRootPath))
                {
                    Directory.Delete(_contentRootPath, recursive: true);
                }
            }
            catch
            {
                // Test cleanup best effort.
            }
        }

        private static string CreateRunConfigJson()
        {
            // TimingMode is numeric to avoid coupling this test to the enum member name.
            return """
        {
          "RunId": "integration-test-run",
          "Game": "Integration Test Game",
          "Category": "Any%",
          "TimingMode": 0,
          "RequireAllSplitsBeforeFinish": false,
          "FinishOnLastSplit": true,
          "MinimumMsBetweenSplits": 0,
          "Splits": [
            { "Index": 0, "Name": "Intro" },
            { "Index": 1, "Name": "Boss" },
            { "Index": 2, "Name": "Finish" }
          ]
        }
        """;
        }
    }
}
