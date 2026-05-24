using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using TournamentTimer.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

app.UseStaticFiles();

var engine = new TimerEngine();
var runLock = new object();
var mediaLock = new object();
var mediaOffers = new Dictionary<string, Dictionary<string, MediaOfferState>>(StringComparer.OrdinalIgnoreCase);
var cameraDisconnectThreshold = TimeSpan.FromSeconds(8);
var mediaOfferTtl = TimeSpan.FromSeconds(45);

var expectedRunId = NormalizeSecret(
    builder.Configuration["RunId"]
    ?? Environment.GetEnvironmentVariable("TOURNAMENT_TIMER_RUN_ID"));

var configPath = ResolveConfigPath(
    builder.Configuration["RunConfigPath"],
    app.Environment.ContentRootPath,
    expectedRunId);

var configDirectory = Path.GetDirectoryName(configPath)
    ?? Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "configs"));

var configuredConfig = RunConfigLoader.LoadFromFile(configPath);

if (expectedRunId is not null &&
    !string.Equals(expectedRunId, configuredConfig.RunId, StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("ERROR: configured runId does not match run config runId.");
    Console.WriteLine($"Settings/env runId: {expectedRunId}");
    Console.WriteLine($"Config runId:       {configuredConfig.RunId}");
    Console.WriteLine($"Config path:        {configPath}");
    Console.WriteLine("Fix timer-settings.json runId or the runId inside the selected config file.");
    Console.WriteLine("Note: server RunId is the tournament/config RunId, not an ASL catalog folder name.");
    throw new InvalidOperationException("run_id_mismatch");
}

Console.WriteLine($"RUN CONFIG: {configPath}");

var defaultServerRunsRoot = Path.GetFullPath(
    Path.Combine(app.Environment.ContentRootPath, "..", "server-runs"));

var serverLogDirectory = ResolveServerRunsRoot(
    builder.Configuration["ServerRunsRoot"]
    ?? Environment.GetEnvironmentVariable("TOURNAMENT_TIMER_SERVER_RUNS_ROOT"),
    app.Environment.ContentRootPath,
    defaultServerRunsRoot);

Directory.CreateDirectory(serverLogDirectory);

var defaultRunAssetsRoot = Path.Combine(serverLogDirectory, "assets");
var runAssetsRoot = ResolveRunAssetsRoot(
    builder.Configuration["RunAssetsRoot"]
    ?? Environment.GetEnvironmentVariable("TOURNAMENT_TIMER_RUN_ASSETS_ROOT"),
    app.Environment.ContentRootPath,
    defaultRunAssetsRoot);
Directory.CreateDirectory(runAssetsRoot);

var runAssetDirectory = GetRunAssetDirectory(runAssetsRoot, configuredConfig.RunId);
Directory.CreateDirectory(runAssetDirectory);

var config = ApplyLiveSplitSplitsFromAssets(configuredConfig, runAssetDirectory);

Console.WriteLine($"SERVER RUNS ROOT: {serverLogDirectory}");
Console.WriteLine($"RUN ASSETS ROOT: {runAssetsRoot}");
Console.WriteLine($"RUN ASSETS: {runAssetDirectory}");
Console.WriteLine($"RUN SPLITS SOURCE: {(HasLiveSplitSplitsAsset(runAssetDirectory) ? "assets/splits.lss" : "config")}");

var currentAttemptPath = Path.Combine(serverLogDirectory, $"{config.RunId}.current-attempt.txt");
var currentAttemptId = LoadOrCreateCurrentAttemptId(currentAttemptPath);

var serverLogPath = GetServerLogPath(serverLogDirectory, config.RunId, currentAttemptId);
var serverSnapshotPath = GetServerSnapshotPath(serverLogDirectory, config.RunId, currentAttemptId);
var adminAuditLogPath = GetAdminAuditLogPath(serverLogDirectory, config.RunId, currentAttemptId);

var runners = RestoreServerRunnersFromLog(config, engine, serverLogPath);
ApplyLatestServerSnapshots(config, runners, serverSnapshotPath);
EnsureRunner(runners, "runner-1");

var newAttemptCooldown = TimeSpan.FromSeconds(10);
DateTimeOffset? lastNewAttemptCreatedAtUtc = null;

var runnerDisconnectThreshold = TimeSpan.FromSeconds(6);

var adminKey = GetConfiguredSecret(
    "TOURNAMENT_TIMER_ADMIN_KEY",
    "ADMIN_KEY",
    "AdminKey");
var runKey = GetConfiguredSecret(
    "TOURNAMENT_TIMER_RUN_KEY",
    "RUN_KEY",
    "RunKey");
var viewKey = GetConfiguredSecret(
    "TOURNAMENT_TIMER_VIEW_KEY",
    "VIEW_KEY",
    "ViewKey");

var cameraIceServers = LoadCameraIceServers(builder.Configuration);
var cameraIceTransportPolicy = GetCameraIceTransportPolicy(builder.Configuration);

Console.WriteLine(
    $"SERVER RESTORED: attempt={currentAttemptId}, runners={runners.Count}");
Console.WriteLine(
    $"ACCESS KEYS: admin={(IsSecretConfigured(adminKey) ? "on" : "off")}, run={(IsSecretConfigured(runKey) ? "on" : "off")}, view={(IsSecretConfigured(viewKey) ? "on" : "off")}");
Console.WriteLine(
    $"CAMERA ICE: servers={cameraIceServers.Count}, turn={(cameraIceServers.Any(server => server.Urls.Any(url => url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase))) ? "on" : "off")}, policy={cameraIceTransportPolicy}");

app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api"))
    {
        await next();
        return;
    }

    var accessDenied = GetAccessDeniedResult(context.Request);

    if (accessDenied is not null)
    {
        await accessDenied.ExecuteAsync(context);
        return;
    }

    await next();
});

app.MapGet("/", () => Results.Ok(new
{
    Name = "TournamentTimer Server",
    Status = "ok"
}));

app.MapGet("/api/server-info", () =>
{
    lock (runLock)
    {
        return Results.Ok(new
        {
            Server = "TournamentTimer Server",
            Status = "ok",
            ActiveRunId = config.RunId,
            Game = config.Game,
            Category = config.Category,
            TimingMode = config.TimingMode,
            AttemptId = currentAttemptId,
            ConfigPath = configPath,
            ConfigDirectory = configDirectory,
            AssetsRoot = runAssetsRoot,
            AssetsPath = runAssetDirectory,
            SplitCount = config.Splits.Count,
            AccessKeys = new
            {
                Admin = IsSecretConfigured(adminKey),
                Run = IsSecretConfigured(runKey),
                View = IsSecretConfigured(viewKey)
            },
            CameraIce = new
            {
                ServerCount = cameraIceServers.Count,
                TurnEnabled = cameraIceServers.Any(server => server.Urls.Any(url =>
                    url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase))),
                IceTransportPolicy = cameraIceTransportPolicy
            },
            ServerTimeUtc = DateTimeOffset.UtcNow
        });
    }
});

app.MapGet("/api/runs/{runId}", (string runId) =>
{
    if (runId != config.RunId)
    {
        return Results.NotFound(new { error = "run_not_found" });
    }

    return Results.Ok(config);
});

app.MapGet("/api/runs/{runId}/media/ice-servers", (string runId) =>
{
    if (runId != config.RunId)
    {
        return Results.NotFound(new { error = "run_not_found" });
    }

    return Results.Ok(new CameraIceServersResponse
    {
        RunId = config.RunId,
        IceServers = cameraIceServers,
        IceTransportPolicy = cameraIceTransportPolicy
    });
});

app.MapGet("/api/runs/{runId}/assets", (string runId, HttpRequest request) =>
{
    if (runId != config.RunId)
    {
        return Results.NotFound(new { error = "run_not_found" });
    }

    var splitsPath = GetSplitsAssetPath(runAssetDirectory);
    var autosplitterPath = GetAutosplitterAssetPath(runAssetDirectory);

    return Results.Ok(new RunAssetsResponse
    {
        RunId = config.RunId,
        HasSplits = splitsPath is not null,
        SplitsFileName = splitsPath is null ? null : Path.GetFileName(splitsPath),
        SplitsUrl = splitsPath is not null
            ? BuildAbsoluteUrl(request, $"/api/runs/{Uri.EscapeDataString(config.RunId)}/assets/splits.lss")
            : null,
        SplitCount = config.Splits.Count,
        SplitSource = splitsPath is not null ? $"assets/{Path.GetFileName(splitsPath)}" : "config",

        HasAutosplitter = autosplitterPath is not null,
        AutosplitterFileName = autosplitterPath is null ? null : Path.GetFileName(autosplitterPath),
        AutosplitterUrl = autosplitterPath is null
            ? null
            : BuildAbsoluteUrl(request, $"/api/runs/{Uri.EscapeDataString(config.RunId)}/assets/autosplitter")
    });
});

app.MapGet("/api/runs/{runId}/assets/splits.lss", (string runId) =>
{
    if (runId != config.RunId)
    {
        return Results.NotFound(new { error = "run_not_found" });
    }

    var splitsPath = GetSplitsAssetPath(runAssetDirectory);

    if (splitsPath is null)
    {
        return Results.NotFound(new { error = "splits_asset_not_found" });
    }

    return Results.File(splitsPath, "application/xml");
});

app.MapGet("/api/runs/{runId}/assets/splits", (string runId) =>
{
    if (runId != config.RunId)
    {
        return Results.NotFound(new { error = "run_not_found" });
    }

    var splitsPath = GetSplitsAssetPath(runAssetDirectory);

    if (splitsPath is null)
    {
        return Results.NotFound(new { error = "splits_asset_not_found" });
    }

    return Results.File(splitsPath, "application/xml");
});

app.MapGet("/api/runs/{runId}/assets/autosplitter", (string runId) =>
{
    if (runId != config.RunId)
    {
        return Results.NotFound(new { error = "run_not_found" });
    }

    var autosplitterPath = GetAutosplitterAssetPath(runAssetDirectory);

    if (autosplitterPath is null)
    {
        return Results.NotFound(new { error = "autosplitter_asset_not_found" });
    }

    return Results.File(autosplitterPath, GetAssetContentType(autosplitterPath));
});

app.MapGet("/api/runs/{runId}/assets/autosplitter.asl", (string runId) =>
{
    if (runId != config.RunId)
    {
        return Results.NotFound(new { error = "run_not_found" });
    }

    var preferredPath = Path.Combine(runAssetDirectory, "autosplitter.asl");

    var autosplitterPath = File.Exists(preferredPath)
        ? preferredPath
        : Directory.Exists(runAssetDirectory)
            ? Directory
                .EnumerateFiles(runAssetDirectory, "*.asl", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
            : null;

    if (autosplitterPath is null)
    {
        return Results.NotFound(new { error = "autosplitter_asl_asset_not_found" });
    }

    return Results.File(autosplitterPath, "text/plain");
});

app.MapGet("/api/runs/{runId}/state", (string runId) =>
{
    if (runId != config.RunId)
    {
        return Results.NotFound(new { error = "run_not_found" });
    }

    lock (runLock)
    {
        var primaryRunner = EnsureRunner(runners, "runner-1");
        return Results.Ok(primaryRunner.State);
    }
});

app.MapGet("/api/runs/{runId}/runners/{runnerId}/state", (string runId, string runnerId) =>
{
    if (runId != config.RunId)
    {
        return Results.NotFound(new { error = "run_not_found" });
    }

    runnerId = NormalizeRunnerId(runnerId);

    if (!IsValidRunnerId(runnerId))
    {
        return Results.BadRequest(new { error = "invalid_runner_id" });
    }

    lock (runLock)
    {
        var runner = EnsureRunner(runners, runnerId);
        var displayElapsedMs = GetDisplayElapsedMs(
            runner.State,
            runner.LastAcceptedClientElapsedMs,
            runner.LastAcceptedServerReceivedAtUtc);

        return Results.Ok(new RunnerServerStateResponse
        {
            StateApiVersion = 1,
            RunId = config.RunId,
            AttemptId = currentAttemptId,
            RunnerId = runnerId,
            Status = runner.State.Status,
            LastCompletedSplitIndex = runner.State.LastCompletedSplitIndex,
            FinishedAtMs = runner.State.FinishedAtMs,
            DisplayElapsedMs = displayElapsedMs,
            ClientElapsedMs = displayElapsedMs,
            ServerElapsedMs = GetServerDisplayElapsedMs(runner),
            TimerDeltaMs = GetServerDisplayElapsedMs(runner) - displayElapsedMs,
            CompletedSplits = GetCompletedSplits(runner),
            StateVersion = runner.StateVersion,
            AdminControlMode = runner.AdminControlMode
        });
    }
});

app.MapGet("/api/runs/{runId}/display-state", (string runId) =>
{
    if (runId != config.RunId)
    {
        return Results.NotFound(new { error = "run_not_found" });
    }

    lock (runLock)
    {
        EnsureRunner(runners, "runner-1");

        var runnerResponses = runners
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => ToRunnerDisplayState(
                pair.Key,
                pair.Value,
                config,
                runnerDisconnectThreshold,
                cameraDisconnectThreshold))
            .ToArray();

        var primaryRunner = runnerResponses.FirstOrDefault(runner =>
            string.Equals(runner.RunnerId, "runner-1", StringComparison.OrdinalIgnoreCase))
            ?? runnerResponses[0];

        return Results.Ok(new DisplayStateResponse
        {
            RunId = config.RunId,
            AttemptId = currentAttemptId,
            Game = config.Game,
            Category = config.Category,
            TimingMode = config.TimingMode,

            Status = primaryRunner.Status,

            RunnerClientId = primaryRunner.ClientId,
            RunnerConnected = primaryRunner.Connected,
            LastRunnerHeartbeatAtUtc = primaryRunner.LastHeartbeatAtUtc,

            DisplayElapsedMs = primaryRunner.DisplayElapsedMs,
            DisplayElapsed = primaryRunner.DisplayElapsed,
            ClientElapsedMs = primaryRunner.ClientElapsedMs,
            ClientElapsed = primaryRunner.ClientElapsed,
            ServerElapsedMs = primaryRunner.ServerElapsedMs,
            ServerElapsed = primaryRunner.ServerElapsed,
            TimerDeltaMs = primaryRunner.TimerDeltaMs,
            TimerDelta = primaryRunner.TimerDelta,

            LastCompletedSplitIndex = primaryRunner.LastCompletedSplitIndex,
            CurrentSplitName = primaryRunner.CurrentSplitName,

            FinishedAtMs = primaryRunner.FinishedAtMs,
            FinishedAt = primaryRunner.FinishedAt,

            Runners = runnerResponses
        });
    }
});

app.MapGet("/api/runs/{runId}/attempt", (string runId) =>
{
    if (runId != config.RunId)
    {
        return Results.NotFound(new { error = "run_not_found" });
    }

    lock (runLock)
    {
        EnsureRunner(runners, "runner-1");

        var runnerAttempts = runners
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new RunnerAttemptResponse
            {
                RunnerId = pair.Key,
                Status = pair.Value.State.Status,
                LastCompletedSplitIndex = pair.Value.State.LastCompletedSplitIndex,
                FinishedAtMs = pair.Value.State.FinishedAtMs
            })
            .ToArray();

        var primaryRunner = runnerAttempts.FirstOrDefault(runner =>
            string.Equals(runner.RunnerId, "runner-1", StringComparison.OrdinalIgnoreCase))
            ?? runnerAttempts[0];

        return Results.Ok(new AttemptResponse
        {
            RunId = config.RunId,
            AttemptId = currentAttemptId,
            Status = primaryRunner.Status,
            LastCompletedSplitIndex = primaryRunner.LastCompletedSplitIndex,
            FinishedAtMs = primaryRunner.FinishedAtMs,
            Runners = runnerAttempts
        });
    }
});

app.MapPost("/api/runs/{runId}/media/runners/{runnerId}/camera-heartbeat", (string runId, string runnerId, CameraHeartbeatRequest request) =>
{
    if (runId != config.RunId)
    {
        return Results.NotFound(new { error = "run_not_found" });
    }

    runnerId = NormalizeRunnerId(runnerId);

    if (!IsValidRunnerId(runnerId))
    {
        return Results.BadRequest(new { error = "invalid_runner_id" });
    }

    lock (runLock)
    {
        var runner = EnsureRunner(runners, runnerId);
        var requestedAttemptId = NormalizeAttemptId(request.AttemptId);

        if (!string.Equals(requestedAttemptId, currentAttemptId, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(new
            {
                accepted = false,
                error = string.IsNullOrWhiteSpace(requestedAttemptId) ? "missing_attempt_id" : "wrong_attempt",
                runnerId,
                attemptId = requestedAttemptId,
                currentAttemptId,
                online = false,
                status = "wrong_attempt"
            });
        }

        runner.CameraAttemptId = currentAttemptId;
        runner.CameraStatus = string.IsNullOrWhiteSpace(request.Status) ? "online" : request.Status.Trim();
        runner.CameraClientId = request.ClientId;
        runner.CameraViewerCount = Math.Max(0, request.ViewerCount);
        runner.CameraLastHeartbeatAtUtc = DateTimeOffset.UtcNow;

        return Results.Ok(ToCameraStateResponse(runnerId, runner, cameraDisconnectThreshold, currentAttemptId));
    }
});

app.MapPost("/api/runs/{runId}/media/runners/{runnerId}/camera-stop", (string runId, string runnerId, HttpRequest request) =>
{
    if (runId != config.RunId)
    {
        return Results.NotFound(new { error = "run_not_found" });
    }

    runnerId = NormalizeRunnerId(runnerId);

    if (!IsValidRunnerId(runnerId))
    {
        return Results.BadRequest(new { error = "invalid_runner_id" });
    }

    var requestedAttemptId = GetRequestAttemptId(request, null);
    var accepted = false;

    lock (runLock)
    {
        var runner = EnsureRunner(runners, runnerId);

        if (string.IsNullOrWhiteSpace(requestedAttemptId) ||
            string.Equals(runner.CameraAttemptId, requestedAttemptId, StringComparison.OrdinalIgnoreCase))
        {
            runner.CameraStatus = "offline";
            runner.CameraViewerCount = 0;
            runner.CameraLastHeartbeatAtUtc = null;
            runner.CameraAttemptId = null;
            accepted = true;
        }
    }

    if (accepted)
    {
        lock (mediaLock)
        {
            mediaOffers.Remove(runnerId);
        }
    }

    return Results.Ok(new { accepted, runnerId, status = accepted ? "offline" : "wrong_attempt" });
});

app.MapGet("/api/runs/{runId}/media/runners/{runnerId}/camera-state", (string runId, string runnerId) =>
{
    if (runId != config.RunId)
    {
        return Results.NotFound(new { error = "run_not_found" });
    }

    runnerId = NormalizeRunnerId(runnerId);

    if (!IsValidRunnerId(runnerId))
    {
        return Results.BadRequest(new { error = "invalid_runner_id" });
    }

    lock (runLock)
    {
        var runner = EnsureRunner(runners, runnerId);
        return Results.Ok(ToCameraStateResponse(runnerId, runner, cameraDisconnectThreshold, currentAttemptId));
    }
});

app.MapPost("/api/runs/{runId}/media/runners/{runnerId}/offers", (string runId, string runnerId, MediaOfferRequest request, HttpRequest httpRequest) =>
{
    if (runId != config.RunId)
    {
        return Results.NotFound(new { error = "run_not_found" });
    }

    runnerId = NormalizeRunnerId(runnerId);

    if (!IsValidRunnerId(runnerId))
    {
        return Results.BadRequest(new { error = "invalid_runner_id" });
    }

    if (string.IsNullOrWhiteSpace(request.ViewerId) || string.IsNullOrWhiteSpace(request.OfferSdp))
    {
        return Results.BadRequest(new { error = "missing_viewer_id_or_offer" });
    }

    lock (mediaLock)
    {
        CleanupExpiredMediaOffers(mediaOffers, mediaOfferTtl);

        if (!mediaOffers.TryGetValue(runnerId, out var runnerOffers))
        {
            runnerOffers = new Dictionary<string, MediaOfferState>(StringComparer.OrdinalIgnoreCase);
            mediaOffers[runnerId] = runnerOffers;
        }

        runnerOffers[request.ViewerId] = new MediaOfferState
        {
            ViewerId = request.ViewerId,
            OfferSdp = request.OfferSdp,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    return Results.Ok(new { accepted = true, runnerId, viewerId = request.ViewerId });
});

app.MapGet("/api/runs/{runId}/media/runners/{runnerId}/offers/pending", (string runId, string runnerId, HttpRequest request) =>
{
    if (runId != config.RunId)
    {
        return Results.NotFound(new { error = "run_not_found" });
    }

    runnerId = NormalizeRunnerId(runnerId);

    if (!IsValidRunnerId(runnerId))
    {
        return Results.BadRequest(new { error = "invalid_runner_id" });
    }

    lock (mediaLock)
    {
        CleanupExpiredMediaOffers(mediaOffers, mediaOfferTtl);

        if (!mediaOffers.TryGetValue(runnerId, out var runnerOffers))
        {
            return Results.Ok(new PendingMediaOffersResponse { Offers = Array.Empty<PendingMediaOfferResponse>() });
        }

        var pending = runnerOffers.Values
            .Where(offer => string.IsNullOrWhiteSpace(offer.AnswerSdp))
            .OrderBy(offer => offer.CreatedAtUtc)
            .Select(offer => new PendingMediaOfferResponse
            {
                ViewerId = offer.ViewerId,
                OfferSdp = offer.OfferSdp,
                CreatedAtUtc = offer.CreatedAtUtc
            })
            .ToArray();

        return Results.Ok(new PendingMediaOffersResponse { Offers = pending });
    }
});

app.MapPost("/api/runs/{runId}/media/runners/{runnerId}/answers", (string runId, string runnerId, MediaAnswerRequest request, HttpRequest httpRequest) =>
{
    if (runId != config.RunId)
    {
        return Results.NotFound(new { error = "run_not_found" });
    }

    runnerId = NormalizeRunnerId(runnerId);

    if (!IsValidRunnerId(runnerId))
    {
        return Results.BadRequest(new { error = "invalid_runner_id" });
    }

    if (string.IsNullOrWhiteSpace(request.ViewerId) || string.IsNullOrWhiteSpace(request.AnswerSdp))
    {
        return Results.BadRequest(new { error = "missing_viewer_id_or_answer" });
    }

    lock (mediaLock)
    {
        CleanupExpiredMediaOffers(mediaOffers, mediaOfferTtl);

        if (!mediaOffers.TryGetValue(runnerId, out var runnerOffers) ||
            !runnerOffers.TryGetValue(request.ViewerId, out var offer))
        {
            return Results.NotFound(new { error = "offer_not_found" });
        }

        offer.AnswerSdp = request.AnswerSdp;
        offer.AnsweredAtUtc = DateTimeOffset.UtcNow;
    }

    return Results.Ok(new { accepted = true, runnerId, viewerId = request.ViewerId });
});

app.MapGet("/api/runs/{runId}/media/runners/{runnerId}/offers/{viewerId}/answer", (string runId, string runnerId, string viewerId, HttpRequest request) =>
{
    if (runId != config.RunId)
    {
        return Results.NotFound(new { error = "run_not_found" });
    }

    runnerId = NormalizeRunnerId(runnerId);

    if (!IsValidRunnerId(runnerId))
    {
        return Results.BadRequest(new { error = "invalid_runner_id" });
    }

    lock (mediaLock)
    {
        CleanupExpiredMediaOffers(mediaOffers, mediaOfferTtl);

        if (!mediaOffers.TryGetValue(runnerId, out var runnerOffers) ||
            !runnerOffers.TryGetValue(viewerId, out var offer))
        {
            return Results.NotFound(new { error = "offer_not_found" });
        }

        return Results.Ok(new MediaAnswerResponse
        {
            Ready = !string.IsNullOrWhiteSpace(offer.AnswerSdp),
            ViewerId = viewerId,
            AnswerSdp = offer.AnswerSdp
        });
    }
});

app.MapPost("/api/runs/{runId}/heartbeat", (string runId, HeartbeatRequest request) =>
{
    if (runId != config.RunId)
    {
        return Results.NotFound(new { error = "run_not_found" });
    }

    if (string.IsNullOrWhiteSpace(request.ClientId))
    {
        return Results.BadRequest(new { error = "missing_client_id" });
    }

    if (string.IsNullOrWhiteSpace(request.AttemptId))
    {
        return Results.BadRequest(new { error = "missing_attempt_id" });
    }

    var runnerId = NormalizeRunnerId(request.RunnerId);

    if (!IsValidRunnerId(runnerId))
    {
        return Results.BadRequest(new { error = "invalid_runner_id" });
    }

    lock (runLock)
    {
        var runner = EnsureRunner(runners, runnerId);
        var serverReceivedAtUtc = DateTimeOffset.UtcNow;

        if (!string.Equals(request.AttemptId, currentAttemptId, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(new HeartbeatResponse
            {
                Accepted = false,
                RunnerId = runnerId,
                RejectReason = "wrong_attempt",
                Status = runner.State.Status,
                ServerReceivedAtUtc = serverReceivedAtUtc
            });
        }

        if (HasActiveDifferentClient(
            runner,
            request.ClientId,
            serverReceivedAtUtc,
            runnerDisconnectThreshold))
        {
            return Results.Ok(new HeartbeatResponse
            {
                Accepted = false,
                RunnerId = runnerId,
                RejectReason = "runner_already_connected",
                ActiveClientId = runner.ClientId,
                Status = runner.State.Status,
                ServerReceivedAtUtc = serverReceivedAtUtc
            });
        }

        runner.ClientId = request.ClientId;
        runner.LastHeartbeatAtUtc = serverReceivedAtUtc;

        return Results.Ok(new HeartbeatResponse
        {
            Accepted = true,
            RunnerId = runnerId,
            Status = runner.State.Status,
            ServerReceivedAtUtc = serverReceivedAtUtc
        });
    }
});

app.MapPost("/api/runs/{runId}/events", (string runId, ClientEventRequest request) =>
{
    if (runId != config.RunId)
    {
        return Results.NotFound(new { error = "run_not_found" });
    }

    var runnerId = NormalizeRunnerId(request.RunnerId);

    if (!IsValidRunnerId(runnerId))
    {
        return Results.BadRequest(new { error = "invalid_runner_id" });
    }

    RunEvent runEvent;

    try
    {
        runEvent = ToRunEventFromRequest(request);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    lock (runLock)
    {
        var runner = EnsureRunner(runners, runnerId);
        var serverReceivedAtUtc = DateTimeOffset.UtcNow;
if (string.IsNullOrWhiteSpace(request.AttemptId))
        {
            return Results.Ok(new ServerEventResponse
            {
                Accepted = false,
                AlreadyProcessed = false,
                RunnerId = runnerId,
                RejectReason = "missing_attempt_id",
                Status = runner.State.Status,
                LastCompletedSplitIndex = runner.State.LastCompletedSplitIndex,
                FinishedAtMs = runner.State.FinishedAtMs
            });
        }

        if (!string.Equals(request.AttemptId, currentAttemptId, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(new ServerEventResponse
            {
                Accepted = false,
                AlreadyProcessed = false,
                RunnerId = runnerId,
                RejectReason = "wrong_attempt",
                Status = runner.State.Status,
                LastCompletedSplitIndex = runner.State.LastCompletedSplitIndex,
                FinishedAtMs = runner.State.FinishedAtMs
            });
        }
        if (!string.IsNullOrWhiteSpace(request.ClientId))
        {
            if (HasActiveDifferentClient(
                runner,
                request.ClientId,
                serverReceivedAtUtc,
                runnerDisconnectThreshold))
            {
                return Results.Ok(new ServerEventResponse
                {
                    Accepted = false,
                    AlreadyProcessed = false,
                    RunnerId = runnerId,
                    RejectReason = "runner_already_connected",
                    Status = runner.State.Status,
                    LastCompletedSplitIndex = runner.State.LastCompletedSplitIndex,
                    FinishedAtMs = runner.State.FinishedAtMs,
                    StateVersion = runner.StateVersion,
                    AdminControlMode = runner.AdminControlMode
                });
            }

            runner.ClientId = request.ClientId;
            runner.LastHeartbeatAtUtc = serverReceivedAtUtc;
        }

        if (runner.State.SeenClientEventIds.Contains(runEvent.ClientEventId))
        {
            return Results.Ok(new ServerEventResponse
            {
                Accepted = true,
                AlreadyProcessed = true,
                RunnerId = runnerId,
                RejectReason = null,
                Status = runner.State.Status,
                LastCompletedSplitIndex = runner.State.LastCompletedSplitIndex,
                FinishedAtMs = runner.State.FinishedAtMs,
                StateVersion = runner.StateVersion,
                AdminControlMode = runner.AdminControlMode
            });
        }

        if (runner.AdminControlMode)
        {
            return Results.Ok(new ServerEventResponse
            {
                Accepted = false,
                AlreadyProcessed = false,
                RunnerId = runnerId,
                RejectReason = "admin_control_mode",
                Status = runner.State.Status,
                LastCompletedSplitIndex = runner.State.LastCompletedSplitIndex,
                FinishedAtMs = runner.State.FinishedAtMs,
                StateVersion = runner.StateVersion,
                AdminControlMode = runner.AdminControlMode
            });
        }

        var timingRejectReason = ValidateRunEventTiming(runEvent);

        if (timingRejectReason is not null)
        {
            return Results.Ok(new ServerEventResponse
            {
                Accepted = false,
                AlreadyProcessed = false,
                RunnerId = runnerId,
                RejectReason = timingRejectReason,
                Status = runner.State.Status,
                LastCompletedSplitIndex = runner.State.LastCompletedSplitIndex,
                FinishedAtMs = runner.State.FinishedAtMs,
                StateVersion = runner.StateVersion,
                AdminControlMode = runner.AdminControlMode
            });
        }

        var result = engine.ApplyEvent(config, runner.State, runEvent);

        if (result.Accepted)
        {
            runner.State = result.State;

            var serverElapsedOverride = GetLateReplayServerElapsedOverride(
                runner,
                runEvent,
                serverReceivedAtUtc);

            ApplyAcceptedServerTiming(runner, runEvent, serverReceivedAtUtc, serverElapsedOverride);
            runner.StateVersion++;

            if (runEvent is SplitRunEvent split)
            {
                UpsertCompletedSplit(runner, config, split, serverReceivedAtUtc, runner.LastAcceptedServerElapsedMs);
            }
        }

        AppendServerLog(
            config,
            currentAttemptId,
            runnerId,
            runEvent,
            result,
            runner.State,
            serverLogPath,
            serverReceivedAtUtc,
            result.Accepted ? runner.LastAcceptedServerElapsedMs : null);

        return Results.Ok(new ServerEventResponse
        {
            Accepted = result.Accepted,
            AlreadyProcessed = false,
            RunnerId = runnerId,
            RejectReason = result.RejectReason,
            Status = runner.State.Status,
            LastCompletedSplitIndex = runner.State.LastCompletedSplitIndex,
            FinishedAtMs = runner.State.FinishedAtMs,
            StateVersion = runner.StateVersion,
            AdminControlMode = runner.AdminControlMode
        });
    }
});

app.MapPost("/api/runs/{runId}/runners/{runnerId}/input-lock", (string runId, string runnerId, RunnerInputLockRequest request) =>
    RunRunnerInputLock(runId, runnerId, request));

app.MapGet("/api/runs", () =>
{
    lock (runLock)
    {
        var availableRuns = Directory.Exists(configDirectory)
            ? Directory
                .EnumerateFiles(configDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .Select(path => Path.GetFileNameWithoutExtension(path))
                .Where(runId => !string.IsNullOrWhiteSpace(runId))
                .OrderBy(runId => runId, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();

        return Results.Ok(new
        {
            ActiveRunId = config.RunId,
            ConfigDirectory = configDirectory,
            AvailableRuns = availableRuns
        });
    }
});

app.MapPost("/api/admin/load-run", (LoadRunRequest request) =>
    LoadRunFromAdmin(request.RunId));

app.MapPost("/api/runs/{runId}/admin/load", (string runId) =>
    LoadRunFromAdmin(runId));
app.MapPost("/api/runs/{runId}/admin/runners/{runnerId}/start", (string runId, string runnerId) =>
    RunAdminRunnerAction(runId, runnerId, "start"));

app.MapPost("/api/runs/{runId}/admin/runners/{runnerId}/split", (string runId, string runnerId) =>
    RunAdminRunnerAction(runId, runnerId, "split"));

app.MapPost("/api/runs/{runId}/admin/runners/{runnerId}/undo", (string runId, string runnerId) =>
    RunAdminRunnerAction(runId, runnerId, "undo"));

app.MapPost("/api/runs/{runId}/admin/runners/{runnerId}/finish", (string runId, string runnerId) =>
    RunAdminRunnerAction(runId, runnerId, "finish"));

// Admin-protected reset/new-attempt endpoints.
app.MapPost("/api/runs/{runId}/debug/reset", (string runId) =>
{
    if (runId != config.RunId)
    {
        return Results.NotFound(new { error = "run_not_found" });
    }

    return ResetCurrentRun();
});

app.MapPost("/api/runs/{runId}/debug/new-attempt", (string runId) =>
{
    if (runId != config.RunId)
    {
        return Results.NotFound(new { error = "run_not_found" });
    }

    return CreateNewAttempt();
});

// Backward-compatible dev endpoints.
app.MapPost("/api/debug/reset", () => ResetCurrentRun());
app.MapPost("/api/debug/new-attempt", () => CreateNewAttempt());

IResult LoadRunFromAdmin(string requestedRunId)
{
    requestedRunId = NormalizeRunId(requestedRunId);

    if (!IsValidRunId(requestedRunId))
    {
        return Results.BadRequest(new
        {
            error = "invalid_run_id",
            runId = requestedRunId
        });
    }

    lock (runLock)
    {
        var requestedConfigPath = GetConfigPathForRunId(configDirectory, requestedRunId);

        if (!File.Exists(requestedConfigPath))
        {
            return Results.NotFound(new
            {
                error = "config_not_found",
                runId = requestedRunId,
                expectedConfigPath = requestedConfigPath
            });
        }

        RunConfig loadedConfig;

        try
        {
            loadedConfig = RunConfigLoader.LoadFromFile(requestedConfigPath);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new
            {
                error = "config_load_failed",
                runId = requestedRunId,
                message = ex.Message,
                expectedConfigPath = requestedConfigPath
            });
        }

        if (!string.Equals(loadedConfig.RunId, requestedRunId, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new
            {
                error = "config_run_id_mismatch",
                requestedRunId,
                configRunId = loadedConfig.RunId,
                expectedConfigPath = requestedConfigPath
            });
        }

        SwitchActiveRun(loadedConfig, requestedConfigPath);

        return Results.Ok(new LoadRunResponse
        {
            Status = "loaded",
            RunId = config.RunId,
            AttemptId = currentAttemptId,
            Game = config.Game,
            Category = config.Category,
            SplitCount = config.Splits.Count,
            ConfigPath = requestedConfigPath,
            AssetsRoot = runAssetsRoot,
            AssetsPath = runAssetDirectory
        });
    }
}

void SwitchActiveRun(RunConfig loadedConfig, string loadedConfigPath)
{
    configPath = loadedConfigPath;

    runAssetDirectory = GetRunAssetDirectory(runAssetsRoot, loadedConfig.RunId);
    Directory.CreateDirectory(runAssetDirectory);

    config = ApplyLiveSplitSplitsFromAssets(loadedConfig, runAssetDirectory);

    currentAttemptPath = Path.Combine(serverLogDirectory, $"{config.RunId}.current-attempt.txt");
    currentAttemptId = LoadOrCreateCurrentAttemptId(currentAttemptPath);

    serverLogPath = GetServerLogPath(serverLogDirectory, config.RunId, currentAttemptId);
    serverSnapshotPath = GetServerSnapshotPath(serverLogDirectory, config.RunId, currentAttemptId);
    adminAuditLogPath = GetAdminAuditLogPath(serverLogDirectory, config.RunId, currentAttemptId);

    runners = RestoreServerRunnersFromLog(config, engine, serverLogPath);
    ApplyLatestServerSnapshots(config, runners, serverSnapshotPath);
    EnsureRunner(runners, "runner-1");

    lock (mediaLock)
    {
        mediaOffers.Clear();
    }

    lastNewAttemptCreatedAtUtc = null;

    Console.WriteLine($"RUN CONFIG: {configPath}");
    Console.WriteLine($"RUN ASSETS ROOT: {runAssetsRoot}");
    Console.WriteLine($"RUN ASSETS: {runAssetDirectory}");
    Console.WriteLine($"RUN SPLITS SOURCE: {(HasLiveSplitSplitsAsset(runAssetDirectory) ? "assets/splits.lss" : "config")}");
    Console.WriteLine($"SERVER RUN LOADED: runId={config.RunId}, attempt={currentAttemptId}, runners={runners.Count}");
}

static string NormalizeRunId(string? runId)
{
    return string.IsNullOrWhiteSpace(runId)
        ? ""
        : runId.Trim();
}

static bool IsValidRunId(string runId)
{
    if (string.IsNullOrWhiteSpace(runId) || runId.Length > 64)
    {
        return false;
    }

    return runId.All(ch =>
        char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.');
}

static string GetConfigPathForRunId(string configDirectory, string runId)
{
    return Path.Combine(configDirectory, $"{runId}.json");
}
IResult ResetCurrentRun()
{
    lock (runLock)
    {
        runners = RestoreServerRunnersFromLog(config, engine, serverLogPath);
        ApplyLatestServerSnapshots(config, runners, serverSnapshotPath);
        EnsureRunner(runners, "runner-1");
    }

    return Results.Ok(new
    {
        status = "reset",
        runId = config.RunId,
        attemptId = currentAttemptId,
        rebuiltFromServerLog = true
    });
}

IResult CreateNewAttempt()
{
    lock (runLock)
    {
        var now = DateTimeOffset.UtcNow;

        if (lastNewAttemptCreatedAtUtc is not null &&
            now - lastNewAttemptCreatedAtUtc.Value < newAttemptCooldown)
        {
            var retryAfterSeconds = (int)Math.Ceiling(
                (newAttemptCooldown - (now - lastNewAttemptCreatedAtUtc.Value)).TotalSeconds);

            return Results.Conflict(new
            {
                error = "new_attempt_cooldown",
                retryAfterSeconds
            });
        }

        lastNewAttemptCreatedAtUtc = now;

        currentAttemptId = CreateAttemptId();
        File.WriteAllText(currentAttemptPath, currentAttemptId);

        serverLogPath = GetServerLogPath(serverLogDirectory, config.RunId, currentAttemptId);
        serverSnapshotPath = GetServerSnapshotPath(serverLogDirectory, config.RunId, currentAttemptId);
        adminAuditLogPath = GetAdminAuditLogPath(serverLogDirectory, config.RunId, currentAttemptId);

        runners.Clear();
        EnsureRunner(runners, "runner-1");

        lock (mediaLock)
        {
            mediaOffers.Clear();
        }
    }

    return Results.Ok(new
    {
        status = "new_attempt",
        runId = config.RunId,
        attemptId = currentAttemptId
    });
}

IResult RunRunnerInputLock(string runId, string runnerId, RunnerInputLockRequest request)
{
    if (runId != config.RunId)
    {
        return Results.NotFound(new { error = "run_not_found" });
    }

    runnerId = NormalizeRunnerId(runnerId);

    if (!IsValidRunnerId(runnerId))
    {
        return Results.BadRequest(new { error = "invalid_runner_id" });
    }

    if (string.IsNullOrWhiteSpace(request.AttemptId))
    {
        return Results.Ok(new InputLockResponse
        {
            RunId = config.RunId,
            AttemptId = currentAttemptId,
            RunnerId = runnerId,
            Accepted = false,
            RejectReason = "missing_attempt_id",
            Status = RunStatus.Ready,
            LastCompletedSplitIndex = -1,
            FinishedAtMs = null,
            StateVersion = 0,
            AdminControlMode = false
        });
    }

    lock (runLock)
    {
        var runner = EnsureRunner(runners, runnerId);

        if (!string.Equals(request.AttemptId, currentAttemptId, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(new InputLockResponse
            {
                RunId = config.RunId,
                AttemptId = currentAttemptId,
                RunnerId = runnerId,
                Accepted = false,
                RejectReason = "wrong_attempt",
                Status = runner.State.Status,
                LastCompletedSplitIndex = runner.State.LastCompletedSplitIndex,
                FinishedAtMs = runner.State.FinishedAtMs,
                StateVersion = runner.StateVersion,
                AdminControlMode = runner.AdminControlMode
            });
        }

        var now = DateTimeOffset.UtcNow;
        var reason = string.IsNullOrWhiteSpace(request.Reason)
            ? "input_desync"
            : request.Reason.Trim();

        var source = string.IsNullOrWhiteSpace(request.Source)
            ? null
            : request.Source.Trim();

        var sourceEventId = string.IsNullOrWhiteSpace(request.SourceEventId)
            ? null
            : request.SourceEventId.Trim();

        var inputLockChanged =
            !runner.InputLocked ||
            !string.Equals(runner.InputLockReason, reason, StringComparison.Ordinal) ||
            !string.Equals(runner.InputLockSource, source, StringComparison.Ordinal) ||
            !string.Equals(runner.InputLockSourceEventId, sourceEventId, StringComparison.Ordinal);

        runner.InputLocked = true;
        runner.InputLockReason = reason;
        runner.InputLockSource = source;
        runner.InputLockSourceEventId = sourceEventId;
        runner.InputLockedAtUtc = now;

        if (!runner.AdminControlMode || inputLockChanged)
        {
            runner.AdminControlMode = true;
            runner.StateVersion++;

            AppendServerSnapshot(
                config,
                currentAttemptId,
                runnerId,
                runner,
                serverSnapshotPath,
                adminAction: $"input-lock:{reason}");
        }

        AppendAdminAuditLog(
            config,
            currentAttemptId,
            runnerId,
            $"input-lock:{reason}",
            accepted: true,
            rejectReason: null,
            runner,
            adminAuditLogPath,
            now);

        return Results.Ok(new InputLockResponse
        {
            RunId = config.RunId,
            AttemptId = currentAttemptId,
            RunnerId = runnerId,
            Accepted = true,
            RejectReason = null,
            Status = runner.State.Status,
            LastCompletedSplitIndex = runner.State.LastCompletedSplitIndex,
            FinishedAtMs = runner.State.FinishedAtMs,
            StateVersion = runner.StateVersion,
            AdminControlMode = runner.AdminControlMode
        });
    }
}

IResult RunAdminRunnerAction(string runId, string runnerId, string action)
{
    if (runId != config.RunId)
    {
        return Results.NotFound(new { error = "run_not_found" });
    }

    runnerId = NormalizeRunnerId(runnerId);

    if (!IsValidRunnerId(runnerId))
    {
        return Results.BadRequest(new { error = "invalid_runner_id" });
    }

    lock (runLock)
    {
        var runner = EnsureRunner(runners, runnerId);

        return action switch
        {
            "start" => ApplyAdminStart(runnerId, runner),
            "split" => ApplyAdminSplit(runnerId, runner),
            "undo" => ApplyAdminUndo(runnerId, runner),
            "finish" => ApplyAdminFinish(runnerId, runner),
            _ => Results.BadRequest(new { error = "unknown_admin_action" })
        };
    }
}

IResult ApplyAdminStart(string runnerId, RunnerRuntimeState runner)
{
    if (runner.State.Status != RunStatus.Ready)
    {
        return AdminRejected(runnerId, runner, "start", "runner_not_ready");
    }

    var runEvent = new StartRunEvent(CreateAdminClientEventId("start"));

    return ApplyAdminTimerEvent(runnerId, runner, "start", runEvent);
}

IResult ApplyAdminSplit(string runnerId, RunnerRuntimeState runner)
{
    if (runner.State.Status != RunStatus.Running)
    {
        return AdminRejected(runnerId, runner, "split", "runner_not_running");
    }

    var nextSplitIndex = runner.State.LastCompletedSplitIndex + 1;

    if (nextSplitIndex >= config.Splits.Count)
    {
        return AdminRejected(runnerId, runner, "split", "no_next_split");
    }

    var elapsedMs = GetAdminElapsedMs(runner);
    var runEvent = new SplitRunEvent(
        CreateAdminClientEventId("split"),
        nextSplitIndex,
        elapsedMs);

    return ApplyAdminTimerEvent(runnerId, runner, "split", runEvent);
}

IResult ApplyAdminFinish(string runnerId, RunnerRuntimeState runner)
{
    if (runner.State.Status != RunStatus.Running)
    {
        return AdminRejected(runnerId, runner, "finish", "runner_not_running");
    }

    var now = DateTimeOffset.UtcNow;
    var elapsedMs = GetAdminElapsedMs(runner);

    runner.State = new RunState
    {
        Status = RunStatus.Finished,
        LastCompletedSplitIndex = runner.State.LastCompletedSplitIndex,
        FinishedAtMs = elapsedMs,
        Events = runner.State.Events,
        SeenClientEventIds = runner.State.SeenClientEventIds
    };

    runner.ServerStartedAtUtc ??= now - TimeSpan.FromMilliseconds(Math.Max(0, elapsedMs));
    runner.ServerFinishedAtUtc = now;
    runner.LastAcceptedClientElapsedMs = elapsedMs;
    runner.LastAcceptedServerReceivedAtUtc = now;
    runner.LastAcceptedServerElapsedMs = GetServerElapsedMsAt(runner, now);
    runner.AdminControlMode = true;
    runner.StateVersion++;

    AppendServerSnapshot(
        config,
        currentAttemptId,
        runnerId,
        runner,
        serverSnapshotPath,
        adminAction: "finish");

    AppendAdminAuditLog(
        config,
        currentAttemptId,
        runnerId,
        "finish",
        accepted: true,
        rejectReason: null,
        runner,
        adminAuditLogPath,
        now);

    return Results.Ok(ToAdminActionResponse(runnerId, "finish", accepted: true, rejectReason: null, runner));
}

IResult ApplyAdminUndo(string runnerId, RunnerRuntimeState runner)
{
    if (runner.State.Status == RunStatus.Ready)
    {
        return AdminRejected(runnerId, runner, "undo", "runner_ready_nothing_to_undo");
    }

    var now = DateTimeOffset.UtcNow;
    var elapsedMs = GetAdminElapsedMs(runner);

    if (runner.State.Status == RunStatus.Finished)
    {
        runner.State = new RunState
        {
            Status = RunStatus.Running,
            LastCompletedSplitIndex = runner.State.LastCompletedSplitIndex,
            FinishedAtMs = null,
            Events = runner.State.Events,
            SeenClientEventIds = runner.State.SeenClientEventIds
        };
    }
    else
    {
        var newLastCompletedSplitIndex = runner.State.LastCompletedSplitIndex - 1;

        if (newLastCompletedSplitIndex < -1)
        {
            return AdminRejected(runnerId, runner, "undo", "no_completed_split_to_undo");
        }

        runner.CompletedSplits.RemoveAll(split =>
            split.SplitIndex > newLastCompletedSplitIndex);

        runner.State = new RunState
        {
            Status = RunStatus.Running,
            LastCompletedSplitIndex = newLastCompletedSplitIndex,
            FinishedAtMs = null,
            Events = runner.State.Events,
            SeenClientEventIds = runner.State.SeenClientEventIds
        };
    }

    runner.ServerStartedAtUtc ??= now - TimeSpan.FromMilliseconds(Math.Max(0, elapsedMs));
    runner.ServerFinishedAtUtc = null;
    runner.LastAcceptedClientElapsedMs = elapsedMs;
    runner.LastAcceptedServerReceivedAtUtc = now;
    runner.LastAcceptedServerElapsedMs = GetServerElapsedMsAt(runner, now);
    runner.AdminControlMode = true;
    runner.StateVersion++;

    AppendServerSnapshot(
        config,
        currentAttemptId,
        runnerId,
        runner,
        serverSnapshotPath,
        adminAction: "undo");

    AppendAdminAuditLog(
        config,
        currentAttemptId,
        runnerId,
        "undo",
        accepted: true,
        rejectReason: null,
        runner,
        adminAuditLogPath,
        now);

    return Results.Ok(ToAdminActionResponse(runnerId, "undo", accepted: true, rejectReason: null, runner));
}

IResult ApplyAdminTimerEvent(
    string runnerId,
    RunnerRuntimeState runner,
    string adminAction,
    RunEvent runEvent)
{
    var result = engine.ApplyEvent(config, runner.State, runEvent);
    var serverReceivedAtUtc = DateTimeOffset.UtcNow;

    if (result.Accepted)
    {
        runner.State = result.State;
        ApplyAcceptedServerTiming(runner, runEvent, serverReceivedAtUtc);
        runner.AdminControlMode = true;
        runner.StateVersion++;

        if (runEvent is SplitRunEvent split)
        {
            UpsertCompletedSplit(runner, config, split, serverReceivedAtUtc, runner.LastAcceptedServerElapsedMs);
        }
    }

    AppendServerLog(
        config,
        currentAttemptId,
        runnerId,
        runEvent,
        result,
        runner.State,
        serverLogPath,
        serverReceivedAtUtc,
        result.Accepted ? runner.LastAcceptedServerElapsedMs : null);

    if (result.Accepted)
    {
        AppendServerSnapshot(
            config,
            currentAttemptId,
            runnerId,
            runner,
            serverSnapshotPath,
            adminAction);
    }

    AppendAdminAuditLog(
        config,
        currentAttemptId,
        runnerId,
        adminAction,
        result.Accepted,
        result.RejectReason,
        runner,
        adminAuditLogPath,
        serverReceivedAtUtc);

    return Results.Ok(ToAdminActionResponse(
        runnerId,
        adminAction,
        result.Accepted,
        result.RejectReason,
        runner));
}

IResult AdminRejected(
    string runnerId,
    RunnerRuntimeState runner,
    string adminAction,
    string rejectReason)
{
    var now = DateTimeOffset.UtcNow;

    AppendAdminAuditLog(
        config,
        currentAttemptId,
        runnerId,
        adminAction,
        accepted: false,
        rejectReason,
        runner,
        adminAuditLogPath,
        now);

    return Results.Ok(ToAdminActionResponse(
        runnerId,
        adminAction,
        accepted: false,
        rejectReason,
        runner));
}

static string CreateAdminClientEventId(string action)
{
    return $"admin:{action}:{Guid.NewGuid():N}";
}

static long GetAdminElapsedMs(RunnerRuntimeState runner)
{
    if (runner.State.Status == RunStatus.Ready)
    {
        return 0;
    }

    if (runner.LastAcceptedServerReceivedAtUtc is null)
    {
        return runner.LastAcceptedClientElapsedMs;
    }

    var elapsedSinceLastAcceptedEventMs =
        (long)(DateTimeOffset.UtcNow - runner.LastAcceptedServerReceivedAtUtc.Value).TotalMilliseconds;

    if (elapsedSinceLastAcceptedEventMs < 0)
    {
        elapsedSinceLastAcceptedEventMs = 0;
    }

    return runner.LastAcceptedClientElapsedMs + elapsedSinceLastAcceptedEventMs;
}

AdminActionResponse ToAdminActionResponse(
    string runnerId,
    string action,
    bool accepted,
    string? rejectReason,
    RunnerRuntimeState runner)
{
    return new AdminActionResponse
    {
        RunId = config.RunId,
        AttemptId = currentAttemptId,
        RunnerId = runnerId,
        Action = action,
        Accepted = accepted,
        RejectReason = rejectReason,
        Status = runner.State.Status,
        LastCompletedSplitIndex = runner.State.LastCompletedSplitIndex,
        FinishedAtMs = runner.State.FinishedAtMs,
        StateVersion = runner.StateVersion,
        AdminControlMode = runner.AdminControlMode
    };
}

static string ResolveConfigPath(string? configuredPath, string contentRootPath, string? configuredRunId)
{
    if (string.IsNullOrWhiteSpace(configuredPath))
    {
        var configFileName = string.IsNullOrWhiteSpace(configuredRunId)
            ? "local-test-run.json"
            : configuredRunId.Trim() + ".json";

        return Path.GetFullPath(
            Path.Combine(contentRootPath, "..", "configs", configFileName));
    }

    if (Path.IsPathRooted(configuredPath))
    {
        return Path.GetFullPath(configuredPath);
    }

    return Path.GetFullPath(
        Path.Combine(contentRootPath, "..", configuredPath));
}

static string ResolveServerRunsRoot(string? configuredPath, string contentRootPath, string defaultServerRunsRoot)
{
    if (string.IsNullOrWhiteSpace(configuredPath))
    {
        return Path.GetFullPath(defaultServerRunsRoot);
    }

    if (Path.IsPathRooted(configuredPath))
    {
        return Path.GetFullPath(configuredPath);
    }

    return Path.GetFullPath(
        Path.Combine(contentRootPath, "..", configuredPath));
}

static string ResolveRunAssetsRoot(string? configuredPath, string contentRootPath, string defaultRunAssetsRoot)
{
    if (string.IsNullOrWhiteSpace(configuredPath))
    {
        return Path.GetFullPath(defaultRunAssetsRoot);
    }

    if (Path.IsPathRooted(configuredPath))
    {
        return Path.GetFullPath(configuredPath);
    }

    return Path.GetFullPath(
        Path.Combine(contentRootPath, "..", configuredPath));
}

app.Run();

static long GetDisplayElapsedMs(
    RunState state,
    long lastAcceptedClientElapsedMs,
    DateTimeOffset? lastAcceptedServerReceivedAtUtc)
{
    if (state.Status == RunStatus.Ready)
    {
        return 0;
    }

    if (state.Status == RunStatus.Finished)
    {
        return state.FinishedAtMs ?? lastAcceptedClientElapsedMs;
    }

    if (lastAcceptedServerReceivedAtUtc is null)
    {
        return lastAcceptedClientElapsedMs;
    }

    var elapsedSinceLastAcceptedEventMs =
        (long)(DateTimeOffset.UtcNow - lastAcceptedServerReceivedAtUtc.Value).TotalMilliseconds;

    if (elapsedSinceLastAcceptedEventMs < 0)
    {
        elapsedSinceLastAcceptedEventMs = 0;
    }

    return lastAcceptedClientElapsedMs + elapsedSinceLastAcceptedEventMs;
}

static RunnerDisplayStateResponse ToRunnerDisplayState(
    string runnerId,
    RunnerRuntimeState runner,
    RunConfig config,
    TimeSpan runnerDisconnectThreshold,
    TimeSpan cameraDisconnectThreshold)
{
    var clientElapsedMs = GetDisplayElapsedMs(
        runner.State,
        runner.LastAcceptedClientElapsedMs,
        runner.LastAcceptedServerReceivedAtUtc);
    var serverElapsedMs = GetServerDisplayElapsedMs(runner);
    var timerDeltaMs = serverElapsedMs - clientElapsedMs;

    var nextSplitIndex = runner.State.LastCompletedSplitIndex + 1;

    var currentSplitName = runner.State.Status == RunStatus.Running
                           && nextSplitIndex >= 0
                           && nextSplitIndex < config.Splits.Count
        ? config.Splits[nextSplitIndex].Name
        : null;

    var runnerConnected =
        runner.LastHeartbeatAtUtc is not null
        && DateTimeOffset.UtcNow - runner.LastHeartbeatAtUtc.Value <= runnerDisconnectThreshold;

    return new RunnerDisplayStateResponse
    {
        RunnerId = runnerId,
        ClientId = runner.ClientId,
        Connected = runnerConnected,
        LastHeartbeatAtUtc = runner.LastHeartbeatAtUtc,
        CameraOnline = IsCameraOnline(runner, cameraDisconnectThreshold),
        CameraStatus = GetCameraStatus(runner, cameraDisconnectThreshold),
        CameraLastHeartbeatAtUtc = runner.CameraLastHeartbeatAtUtc,
        CameraViewerCount = runner.CameraViewerCount,

        Status = runner.State.Status,
        DisplayElapsedMs = clientElapsedMs,
        DisplayElapsed = FormatMs(clientElapsedMs),
        ClientElapsedMs = clientElapsedMs,
        ClientElapsed = FormatMs(clientElapsedMs),
        ServerElapsedMs = serverElapsedMs,
        ServerElapsed = FormatMs(serverElapsedMs),
        TimerDeltaMs = timerDeltaMs,
        TimerDelta = FormatSignedMs(timerDeltaMs),

        LastCompletedSplitIndex = runner.State.LastCompletedSplitIndex,
        CurrentSplitName = currentSplitName,
        CompletedSplits = GetCompletedSplits(runner),
        StateVersion = runner.StateVersion,
        AdminControlMode = runner.AdminControlMode,

        InputLocked = runner.InputLocked,
        InputLockReason = runner.InputLockReason,
        InputLockSource = runner.InputLockSource,
        InputLockSourceEventId = runner.InputLockSourceEventId,
        InputLockedAtUtc = runner.InputLockedAtUtc,

        FinishedAtMs = runner.State.FinishedAtMs,
        FinishedAt = runner.State.FinishedAtMs is null ? null : FormatMs(runner.State.FinishedAtMs.Value),
        ServerFinishedAtMs = runner.State.Status == RunStatus.Finished ? serverElapsedMs : null,
        ServerFinishedAt = runner.State.Status == RunStatus.Finished ? FormatMs(serverElapsedMs) : null
    };
}

static RunConfig ApplyLiveSplitSplitsFromAssets(
    RunConfig baseConfig,
    string runAssetDirectory)
{
    var splitsPath = GetSplitsAssetPath(runAssetDirectory);

    if (splitsPath is null)
    {
        return baseConfig;
    }

    var splits = ReadLiveSplitSplitDefinitions(splitsPath);

    if (splits.Count == 0)
    {
        Console.WriteLine("WARNING: assets/splits.lss exists, but no segments were found. Falling back to config splits.");
        return baseConfig;
    }

    return baseConfig with
    {
        Splits = splits
    };
}

static bool HasLiveSplitSplitsAsset(string runAssetDirectory)
{
    return GetSplitsAssetPath(runAssetDirectory) is not null;
}

static string GetRunAssetDirectory(string runAssetsRoot, string runId)
{
    return Path.Combine(runAssetsRoot, runId);
}

static string? GetSplitsAssetPath(string runAssetDirectory)
{
    var preferredPath = Path.Combine(runAssetDirectory, "splits.lss");

    if (File.Exists(preferredPath))
    {
        return preferredPath;
    }

    if (!Directory.Exists(runAssetDirectory))
    {
        return null;
    }

    return Directory
        .EnumerateFiles(runAssetDirectory, "*.lss", SearchOption.TopDirectoryOnly)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();
}

static string? GetAutosplitterAssetPath(string runAssetDirectory)
{
    if (!Directory.Exists(runAssetDirectory))
    {
        return null;
    }

    var preferredCandidates = new[]
    {
        "autosplitter.asl",
        "autosplitter.wasm",
        "autosplitter.dll"
    };

    foreach (var candidate in preferredCandidates)
    {
        var path = Path.Combine(runAssetDirectory, candidate);

        if (File.Exists(path))
        {
            return path;
        }
    }

    var extensionPriority = new[]
    {
        "*.asl",
        "*.wasm",
        "*.dll"
    };

    foreach (var pattern in extensionPriority)
    {
        var path = Directory
            .EnumerateFiles(runAssetDirectory, pattern, SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (path is not null)
        {
            return path;
        }
    }

    return null;
}

static string GetAssetContentType(string path)
{
    var extension = Path.GetExtension(path);

    return extension.ToLowerInvariant() switch
    {
        ".lss" => "application/xml",
        ".asl" => "text/plain",
        ".wasm" => "application/wasm",
        ".dll" => "application/octet-stream",
        _ => "application/octet-stream"
    };
}

static string BuildAbsoluteUrl(HttpRequest request, string path)
{
    return $"{request.Scheme}://{request.Host}{request.PathBase}{path}";
}

static IReadOnlyList<SplitDefinition> ReadLiveSplitSplitDefinitions(string splitsPath)
{
    try
    {
        var document = XDocument.Load(splitsPath);

        var segmentElements = document
            .Descendants()
            .Where(element => element.Name.LocalName == "Segment")
            .ToArray();

        var result = new List<SplitDefinition>();

        for (var index = 0; index < segmentElements.Length; index++)
        {
            var name = segmentElements[index]
                .Elements()
                .FirstOrDefault(element => element.Name.LocalName == "Name")
                ?.Value
                ?.Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"Split {index + 1}";
            }

            result.Add(new SplitDefinition
            {
                Index = index,
                Name = name
            });
        }

        return result;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"WARNING: failed to read LiveSplit splits asset: {ex.Message}");
        return Array.Empty<SplitDefinition>();
    }
}

static void UpsertCompletedSplit(
    RunnerRuntimeState runner,
    RunConfig config,
    SplitRunEvent split,
    DateTimeOffset serverReceivedAtUtc,
    long serverElapsedMs)
{
    runner.CompletedSplits.RemoveAll(existing =>
        existing.SplitIndex == split.SplitIndex);

    var splitName = config.Splits
        .FirstOrDefault(configSplit => configSplit.Index == split.SplitIndex)
        ?.Name ?? $"Split {split.SplitIndex + 1}";

    runner.CompletedSplits.Add(new CompletedSplitRuntimeState
    {
        SplitIndex = split.SplitIndex,
        Name = splitName,
        ClientElapsedMs = split.ClientElapsedMs,
        ServerElapsedMs = serverElapsedMs,
        DeltaMs = serverElapsedMs - split.ClientElapsedMs,
        ServerReceivedAtUtc = serverReceivedAtUtc
    });

    runner.CompletedSplits.Sort((left, right) =>
        left.SplitIndex.CompareTo(right.SplitIndex));
}

static IReadOnlyList<CompletedSplitResponse> GetCompletedSplits(
    RunnerRuntimeState runner)
{
    return runner.CompletedSplits
        .OrderBy(split => split.SplitIndex)
        .Select(split => new CompletedSplitResponse
        {
            SplitIndex = split.SplitIndex,
            Name = split.Name,
            ClientElapsedMs = split.ClientElapsedMs,
            ClientElapsed = FormatMs(split.ClientElapsedMs),
            ServerElapsedMs = split.ServerElapsedMs,
            ServerElapsed = FormatMs(split.ServerElapsedMs),
            DeltaMs = split.DeltaMs,
            Delta = FormatSignedMs(split.DeltaMs),
            ServerReceivedAtUtc = split.ServerReceivedAtUtc
        })
        .ToArray();
}

static string GetServerSnapshotPath(string serverLogDirectory, string runId, string attemptId)
{
    return Path.Combine(serverLogDirectory, $"{runId}.{attemptId}.server.snapshots.jsonl");
}

static string GetAdminAuditLogPath(string serverLogDirectory, string runId, string attemptId)
{
    return Path.Combine(serverLogDirectory, $"{runId}.{attemptId}.admin.events.jsonl");
}

static void AppendServerSnapshot(
    RunConfig config,
    string attemptId,
    string runnerId,
    RunnerRuntimeState runner,
    string serverSnapshotPath,
    string adminAction)
{
    var entry = new ServerRunnerStateSnapshot
    {
        RunId = config.RunId,
        AttemptId = attemptId,
        RunnerId = runnerId,
        StateVersion = runner.StateVersion,
        Status = runner.State.Status,
        LastCompletedSplitIndex = runner.State.LastCompletedSplitIndex,
        FinishedAtMs = runner.State.FinishedAtMs,
        LastAcceptedClientElapsedMs = runner.LastAcceptedClientElapsedMs,
        LastAcceptedServerReceivedAtUtc = runner.LastAcceptedServerReceivedAtUtc,
        LastAcceptedServerElapsedMs = runner.LastAcceptedServerElapsedMs,
        ServerStartedAtUtc = runner.ServerStartedAtUtc,
        ServerFinishedAtUtc = runner.ServerFinishedAtUtc,
        AdminControlMode = runner.AdminControlMode,

        InputLocked = runner.InputLocked,
        InputLockReason = runner.InputLockReason,
        InputLockSource = runner.InputLockSource,
        InputLockSourceEventId = runner.InputLockSourceEventId,
        InputLockedAtUtc = runner.InputLockedAtUtc,

        AdminAction = adminAction,
        CompletedSplits = runner.CompletedSplits
            .OrderBy(split => split.SplitIndex)
            .Select(split => new CompletedSplitRuntimeState
            {
                SplitIndex = split.SplitIndex,
                Name = split.Name,
                ClientElapsedMs = split.ClientElapsedMs,
                ServerElapsedMs = split.ServerElapsedMs,
                DeltaMs = split.DeltaMs,
                ServerReceivedAtUtc = split.ServerReceivedAtUtc
            })
            .ToArray(),
        SavedAtUtc = DateTimeOffset.UtcNow
    };

    var options = new JsonSerializerOptions
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    var json = JsonSerializer.Serialize(entry, options);
    File.AppendAllText(serverSnapshotPath, json + Environment.NewLine);
}

static void AppendAdminAuditLog(
    RunConfig config,
    string attemptId,
    string runnerId,
    string action,
    bool accepted,
    string? rejectReason,
    RunnerRuntimeState runner,
    string adminAuditLogPath,
    DateTimeOffset loggedAtUtc)
{
    var entry = new AdminAuditLogEntry
    {
        RunId = config.RunId,
        AttemptId = attemptId,
        RunnerId = runnerId,
        Action = action,
        Accepted = accepted,
        RejectReason = rejectReason,
        StatusAfter = runner.State.Status,
        LastCompletedSplitIndexAfter = runner.State.LastCompletedSplitIndex,
        FinishedAtMsAfter = runner.State.FinishedAtMs,
        StateVersionAfter = runner.StateVersion,
        AdminControlModeAfter = runner.AdminControlMode,
        LoggedAtUtc = loggedAtUtc
    };

    var options = new JsonSerializerOptions
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    var json = JsonSerializer.Serialize(entry, options);
    File.AppendAllText(adminAuditLogPath, json + Environment.NewLine);
}

static void ApplyLatestServerSnapshots(
    RunConfig config,
    Dictionary<string, RunnerRuntimeState> runners,
    string serverSnapshotPath)
{
    if (!File.Exists(serverSnapshotPath))
    {
        return;
    }

    var options = new JsonSerializerOptions
    {
        Converters = { new JsonStringEnumConverter() }
    };

    var latestByRunner = new Dictionary<string, ServerRunnerStateSnapshot>(StringComparer.OrdinalIgnoreCase);

    foreach (var line in File.ReadLines(serverSnapshotPath))
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        ServerRunnerStateSnapshot? entry;

        try
        {
            entry = JsonSerializer.Deserialize<ServerRunnerStateSnapshot>(line, options);
        }
        catch
        {
            continue;
        }

        if (entry is null ||
            entry.RunId != config.RunId ||
            string.IsNullOrWhiteSpace(entry.RunnerId))
        {
            continue;
        }

        latestByRunner[NormalizeRunnerId(entry.RunnerId)] = entry;
    }

    foreach (var pair in latestByRunner)
    {
        var runner = EnsureRunner(runners, pair.Key);
        var snapshot = pair.Value;

        runner.State = new RunState
        {
            Status = snapshot.Status,
            LastCompletedSplitIndex = snapshot.LastCompletedSplitIndex,
            FinishedAtMs = snapshot.FinishedAtMs,
            Events = runner.State.Events,
            SeenClientEventIds = runner.State.SeenClientEventIds
        };

        runner.LastAcceptedClientElapsedMs = snapshot.LastAcceptedClientElapsedMs;
        runner.LastAcceptedServerReceivedAtUtc = snapshot.LastAcceptedServerReceivedAtUtc;
        runner.LastAcceptedServerElapsedMs = snapshot.LastAcceptedServerElapsedMs;
        runner.ServerStartedAtUtc = snapshot.ServerStartedAtUtc
            ?? EstimateServerStartedAtUtc(snapshot.LastAcceptedServerReceivedAtUtc, snapshot.LastAcceptedServerElapsedMs, snapshot.LastAcceptedClientElapsedMs);
        runner.ServerFinishedAtUtc = snapshot.ServerFinishedAtUtc;
        runner.AdminControlMode = snapshot.AdminControlMode;
        runner.InputLocked = snapshot.InputLocked;
        runner.InputLockReason = snapshot.InputLockReason;
        runner.InputLockSource = snapshot.InputLockSource;
        runner.InputLockSourceEventId = snapshot.InputLockSourceEventId;
        runner.InputLockedAtUtc = snapshot.InputLockedAtUtc;
        runner.StateVersion = Math.Max(runner.StateVersion, snapshot.StateVersion);

        runner.CompletedSplits.Clear();

        foreach (var split in snapshot.CompletedSplits.OrderBy(split => split.SplitIndex))
        {
            runner.CompletedSplits.Add(new CompletedSplitRuntimeState
            {
                SplitIndex = split.SplitIndex,
                Name = split.Name,
                ClientElapsedMs = split.ClientElapsedMs,
                ServerElapsedMs = split.ServerElapsedMs,
                DeltaMs = split.DeltaMs,
                ServerReceivedAtUtc = split.ServerReceivedAtUtc
            });
        }
    }
}

static string LoadOrCreateCurrentAttemptId(string currentAttemptPath)
{
    if (File.Exists(currentAttemptPath))
    {
        var existing = File.ReadAllText(currentAttemptPath).Trim();

        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }
    }

    var attemptId = CreateAttemptId();

    var directory = Path.GetDirectoryName(currentAttemptPath);

    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    File.WriteAllText(currentAttemptPath, attemptId);

    return attemptId;
}

static string CreateAttemptId()
{
    return DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
}

static string GetServerLogPath(string serverLogDirectory, string runId, string attemptId)
{
    return Path.Combine(serverLogDirectory, $"{runId}.{attemptId}.server.events.jsonl");
}

static Dictionary<string, RunnerRuntimeState> RestoreServerRunnersFromLog(
    RunConfig config,
    TimerEngine engine,
    string serverLogPath)
{
    var runners = new Dictionary<string, RunnerRuntimeState>(StringComparer.OrdinalIgnoreCase);

    if (!File.Exists(serverLogPath))
    {
        return runners;
    }

    var options = new JsonSerializerOptions
    {
        Converters = { new JsonStringEnumConverter() }
    };

    foreach (var line in File.ReadLines(serverLogPath))
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        var entry = JsonSerializer.Deserialize<ServerRunLogEntry>(line, options);

        if (entry is null || !entry.Accepted || entry.RunId != config.RunId)
        {
            continue;
        }

        var runnerId = NormalizeRunnerId(entry.RunnerId);
        var runner = EnsureRunner(runners, runnerId);
        var runEvent = ToRunEventFromServerLogEntry(entry);
        var result = engine.ApplyEvent(config, runner.State, runEvent);

        if (result.Accepted)
        {
            runner.State = result.State;
            ApplyAcceptedServerTiming(runner, runEvent, entry.ServerReceivedAtUtc, entry.ServerElapsedMsAfterEvent);
            runner.StateVersion++;

            if (runEvent is SplitRunEvent split)
            {
                UpsertCompletedSplit(runner, config, split, entry.ServerReceivedAtUtc, runner.LastAcceptedServerElapsedMs);
            }
        }
    }

    return runners;
}

static RunnerRuntimeState EnsureRunner(
    Dictionary<string, RunnerRuntimeState> runners,
    string runnerId)
{
    if (!runners.TryGetValue(runnerId, out var runner))
    {
        runner = new RunnerRuntimeState();
        runners.Add(runnerId, runner);
    }

    return runner;
}

static string NormalizeRunnerId(string? runnerId)
{
    return string.IsNullOrWhiteSpace(runnerId)
        ? "runner-1"
        : runnerId.Trim();
}

static bool HasActiveDifferentClient(
    RunnerRuntimeState runner,
    string clientId,
    DateTimeOffset now,
    TimeSpan disconnectThreshold)
{
    if (string.IsNullOrWhiteSpace(runner.ClientId) ||
        runner.LastHeartbeatAtUtc is null)
    {
        return false;
    }

    if (string.Equals(runner.ClientId, clientId, StringComparison.Ordinal))
    {
        return false;
    }

    return now - runner.LastHeartbeatAtUtc.Value <= disconnectThreshold;
}

static bool IsValidRunnerId(string runnerId)
{
    if (string.IsNullOrWhiteSpace(runnerId) || runnerId.Length > 64)
    {
        return false;
    }

    return runnerId.All(ch =>
        char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.');
}


static string? ValidateRunEventTiming(RunEvent runEvent)
{
    if (runEvent.LiveSplitRealTimeMs is < 0 || runEvent.LiveSplitGameTimeMs is < 0)
    {
        return "invalid_livesplit_elapsed_time";
    }

    if (runEvent.SourceEventId is not null && runEvent.SourceEventId.Length > 160)
    {
        return "source_event_id_too_long";
    }

    if (runEvent is StartRunEvent)
    {
        return null;
    }

    return runEvent.TimingSource switch
    {
        RunTimingSource.RunnerStopwatch => null,

        RunTimingSource.LiveSplitGameTime when runEvent.LiveSplitGameTimeMs is null =>
            "missing_livesplit_game_time",

        RunTimingSource.LiveSplitGameTime when runEvent.LiveSplitGameTimeMs.Value != runEvent.ClientElapsedMs =>
            "livesplit_game_time_mismatch",

        RunTimingSource.LiveSplitRealTime when runEvent.LiveSplitRealTimeMs is not null &&
                                             runEvent.LiveSplitRealTimeMs.Value != runEvent.ClientElapsedMs =>
            "livesplit_real_time_mismatch",

        RunTimingSource.LiveSplitRealTime when runEvent.LiveSplitRealTimeMs is null =>
            "missing_livesplit_real_time",

        _ => null
    };
}

static RunEvent ToRunEventFromRequest(ClientEventRequest request)
{
    if (string.IsNullOrWhiteSpace(request.ClientEventId))
    {
        throw new InvalidOperationException("missing_client_event_id");
    }

    RunEvent runEvent = request.Type.Trim().ToLowerInvariant() switch
    {
        "start" => new StartRunEvent(request.ClientEventId),

        "split" => new SplitRunEvent(
            request.ClientEventId,
            request.SplitIndex ?? throw new InvalidOperationException("missing_split_index"),
            request.ClientElapsedMs),

        "finish" => new FinishRunEvent(
            request.ClientEventId,
            request.ClientElapsedMs),

        _ => throw new InvalidOperationException("unknown_event_type")
    };

    return runEvent with
    {
        TimingSource = request.TimingSource,
        LiveSplitRealTimeMs = request.LiveSplitRealTimeMs,
        LiveSplitGameTimeMs = request.LiveSplitGameTimeMs,
        SourceEventId = request.SourceEventId,
        SourceOccurredAtUtc = request.SourceOccurredAtUtc
    };
}

static RunEvent ToRunEventFromServerLogEntry(ServerRunLogEntry entry)
{
    RunEvent runEvent = entry.Kind switch
    {
        RunEventKind.Start => new StartRunEvent(entry.ClientEventId),

        RunEventKind.Split => new SplitRunEvent(
            entry.ClientEventId,
            entry.SplitIndex ?? throw new InvalidOperationException("Split event has no split index."),
            entry.ClientElapsedMs),

        RunEventKind.Finish => new FinishRunEvent(
            entry.ClientEventId,
            entry.ClientElapsedMs),

        _ => throw new InvalidOperationException("unknown_event_type")
    };

    return runEvent with
    {
        TimingSource = entry.TimingSource,
        LiveSplitRealTimeMs = entry.LiveSplitRealTimeMs,
        LiveSplitGameTimeMs = entry.LiveSplitGameTimeMs,
        SourceEventId = entry.SourceEventId,
        SourceOccurredAtUtc = entry.SourceOccurredAtUtc
    };
}

static void AppendServerLog(
    RunConfig config,
    string attemptId,
    string runnerId,
    RunEvent runEvent,
    ApplyEventResult result,
    RunState state,
    string serverLogPath,
    DateTimeOffset serverReceivedAtUtc,
    long? serverElapsedMsAfterEvent)
{
    var entry = new ServerRunLogEntry
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

        ServerStatusAfterEvent = state.Status,
        ServerFinishedAtMsAfterEvent = state.FinishedAtMs,
        ServerElapsedMsAfterEvent = serverElapsedMsAfterEvent,

        ServerReceivedAtUtc = serverReceivedAtUtc
    };

    var options = new JsonSerializerOptions
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    var json = JsonSerializer.Serialize(entry, options);

    File.AppendAllText(serverLogPath, json + Environment.NewLine);
}

static RunEventKind GetKind(RunEvent runEvent)
{
    return runEvent switch
    {
        StartRunEvent => RunEventKind.Start,
        SplitRunEvent => RunEventKind.Split,
        FinishRunEvent => RunEventKind.Finish,
        _ => throw new InvalidOperationException("Unknown run event type.")
    };
}

IResult? GetAccessDeniedResult(HttpRequest request)
{
    var path = request.Path.Value ?? "";
    var method = request.Method;

    if (IsAdminApi(path))
    {
        return RequireAdminAccess(request);
    }

    if (IsRunnerWriteApi(path, method) || IsRunnerMediaApi(path, method))
    {
        return RequireRunnerAccess(request);
    }

    if (IsViewMediaApi(path, method))
    {
        return RequireViewAccess(request);
    }

    if (IsViewApi(path, method))
    {
        return RequireViewAccess(request);
    }

    if (IsReadApi(path, method))
    {
        return RequireReadAccess(request);
    }

    return null;
}

static bool IsAdminApi(string path)
{
    return path.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/admin/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/debug/", StringComparison.OrdinalIgnoreCase);
}

static bool IsRunnerWriteApi(string path, string method)
{
    return string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
        && (path.EndsWith("/heartbeat", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/events", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/input-lock", StringComparison.OrdinalIgnoreCase));
}

static bool IsRunnerMediaApi(string path, string method)
{
    if (!path.Contains("/media/runners/", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
    {
        return path.EndsWith("/camera-heartbeat", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/camera-stop", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/answers", StringComparison.OrdinalIgnoreCase);
    }

    return string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
        && path.EndsWith("/offers/pending", StringComparison.OrdinalIgnoreCase);
}

static bool IsViewMediaApi(string path, string method)
{
    if (!path.Contains("/media/runners/", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
    {
        return path.EndsWith("/camera-state", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/offers/", StringComparison.OrdinalIgnoreCase);
    }

    return string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
        && path.EndsWith("/offers", StringComparison.OrdinalIgnoreCase);
}

static bool IsViewApi(string path, string method)
{
    return string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
        && path.EndsWith("/display-state", StringComparison.OrdinalIgnoreCase);
}

static bool IsReadApi(string path, string method)
{
    return string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase);
}

IResult? RequireAdminAccess(HttpRequest request)
{
    if (!IsSecretConfigured(adminKey))
    {
        return null;
    }

    return HasAnyAccessKey(request, adminKey)
        ? null
        : AccessDenied("admin_key_required");
}

IResult? RequireRunnerAccess(HttpRequest request)
{
    if (!IsSecretConfigured(runKey))
    {
        return null;
    }

    return HasAnyAccessKey(request, runKey, adminKey)
        ? null
        : AccessDenied("run_key_required");
}

IResult? RequireViewAccess(HttpRequest request)
{
    if (!IsSecretConfigured(viewKey))
    {
        return null;
    }

    return HasAnyAccessKey(request, viewKey, adminKey)
        ? null
        : AccessDenied("view_key_required");
}

IResult? RequireReadAccess(HttpRequest request)
{
    if (!IsSecretConfigured(adminKey) && !IsSecretConfigured(runKey) && !IsSecretConfigured(viewKey))
    {
        return null;
    }

    return HasAnyAccessKey(request, adminKey, runKey, viewKey)
        ? null
        : AccessDenied("access_key_required");
}

static IResult AccessDenied(string error)
{
    return Results.Json(new { error }, statusCode: 401);
}

bool HasAnyAccessKey(HttpRequest request, params string?[] expectedKeys)
{
    var providedKeys = GetProvidedAccessKeys(request);

    return providedKeys.Any(provided => expectedKeys.Any(expected => SecretMatches(provided, expected)));
}

static IReadOnlyList<string> GetProvidedAccessKeys(HttpRequest request)
{
    var result = new List<string>();

    foreach (var headerName in new[]
    {
        "X-Admin-Key",
        "X-Run-Key",
        "X-View-Key",
        "X-Tournament-Timer-Key"
    })
    {
        if (request.Headers.TryGetValue(headerName, out var values))
        {
            result.AddRange(values.Select(value => value?.Trim()).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!));
        }
    }

    if (request.Headers.TryGetValue("Authorization", out var authorizationValues))
    {
        foreach (var authorization in authorizationValues)
        {
            const string bearerPrefix = "Bearer ";

            if (!string.IsNullOrWhiteSpace(authorization) &&
                authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(authorization[bearerPrefix.Length..].Trim());
            }
        }
    }

    foreach (var queryName in new[] { "adminKey", "runKey", "viewKey", "key" })
    {
        if (request.Query.TryGetValue(queryName, out var values))
        {
            result.AddRange(values.Select(value => value?.Trim()).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!));
        }
    }

    return result;
}

string? GetConfiguredSecret(params string[] names)
{
    foreach (var name in names)
    {
        var value = builder.Configuration[name] ?? Environment.GetEnvironmentVariable(name);
        var normalized = NormalizeSecret(value);

        if (normalized is not null)
        {
            return normalized;
        }
    }

    return null;
}

static string? NormalizeSecret(string? value)
{
    return string.IsNullOrWhiteSpace(value)
        ? null
        : value.Trim();
}

static bool IsSecretConfigured(string? value)
{
    return !string.IsNullOrWhiteSpace(value);
}

static bool SecretMatches(string? provided, string? expected)
{
    return !string.IsNullOrWhiteSpace(provided)
        && !string.IsNullOrWhiteSpace(expected)
        && string.Equals(provided.Trim(), expected.Trim(), StringComparison.Ordinal);
}

static long? GetLateReplayServerElapsedOverride(
    RunnerRuntimeState runner,
    RunEvent runEvent,
    DateTimeOffset serverReceivedAtUtc)
{
    if (runEvent is StartRunEvent)
    {
        return null;
    }

    var serverElapsedAtReceive = GetServerElapsedMsAt(runner, serverReceivedAtUtc);
    var deltaMs = serverElapsedAtReceive - runEvent.ClientElapsedMs;

    // If the server was down, runner events can be replayed much later.
    // Client time is the official timing source; server time is only audit/control.
    return Math.Abs(deltaMs) > 1000
        ? runEvent.ClientElapsedMs
        : null;
}

static IReadOnlyList<CameraIceServerConfig> LoadCameraIceServers(
    Microsoft.Extensions.Configuration.IConfiguration configuration)
{
    var result = new List<CameraIceServerConfig>();
    var section = configuration.GetSection("CameraIceServers");

    foreach (var child in section.GetChildren())
    {
        var urls = ReadConfigStringList(child.GetSection("Urls"));

        if (urls.Count == 0)
        {
            urls = ReadConfigStringList(child.GetSection("urls"));
        }

        var singleUrl = NormalizeSecret(child["Url"] ?? child["url"]);

        if (urls.Count == 0 && singleUrl is not null)
        {
            urls = [singleUrl];
        }

        if (urls.Count == 0)
        {
            continue;
        }

        result.Add(new CameraIceServerConfig
        {
            Urls = urls,
            Username = NormalizeSecret(child["Username"] ?? child["username"]),
            Credential = NormalizeSecret(child["Credential"] ?? child["credential"] ?? child["Password"] ?? child["password"])
        });
    }

    if (result.Count == 0)
    {
        result.Add(new CameraIceServerConfig
        {
            Urls = ["stun:stun.l.google.com:19302"]
        });
    }

    return result;
}

static IReadOnlyList<string> ReadConfigStringList(
    Microsoft.Extensions.Configuration.IConfigurationSection section)
{
    var children = section.GetChildren()
        .Select(child => NormalizeSecret(child.Value))
        .Where(value => value is not null)
        .Select(value => value!)
        .ToArray();

    if (children.Length > 0)
    {
        return children;
    }

    var single = NormalizeSecret(section.Value);

    return single is null
        ? []
        : [single];
}

static string GetCameraIceTransportPolicy(
    Microsoft.Extensions.Configuration.IConfiguration configuration)
{
    var value = NormalizeSecret(
        configuration["CameraIceTransportPolicy"]
        ?? Environment.GetEnvironmentVariable("TOURNAMENT_TIMER_CAMERA_ICE_TRANSPORT_POLICY"));

    return string.Equals(value, "relay", StringComparison.OrdinalIgnoreCase)
        ? "relay"
        : "all";
}

static void ApplyAcceptedServerTiming(
    RunnerRuntimeState runner,
    RunEvent runEvent,
    DateTimeOffset serverReceivedAtUtc,
    long? serverElapsedMsOverride = null)
{
    if (runEvent is StartRunEvent)
    {
        runner.ServerStartedAtUtc = serverReceivedAtUtc;
        runner.ServerFinishedAtUtc = null;
        runner.LastAcceptedServerElapsedMs = serverElapsedMsOverride ?? 0;
    }
    else
    {
        if (runner.ServerStartedAtUtc is null)
        {
            var elapsedMsForEstimate = serverElapsedMsOverride ?? runEvent.ClientElapsedMs;
            runner.ServerStartedAtUtc = serverReceivedAtUtc - TimeSpan.FromMilliseconds(Math.Max(0, elapsedMsForEstimate));
        }

        runner.LastAcceptedServerElapsedMs = serverElapsedMsOverride ?? GetServerElapsedMsAt(runner, serverReceivedAtUtc);

        if (runEvent is FinishRunEvent)
        {
            runner.ServerFinishedAtUtc = serverElapsedMsOverride is not null && runner.ServerStartedAtUtc is not null
                ? runner.ServerStartedAtUtc.Value + TimeSpan.FromMilliseconds(serverElapsedMsOverride.Value)
                : serverReceivedAtUtc;
        }
    }

    runner.LastAcceptedClientElapsedMs = runEvent.ClientElapsedMs;
    runner.LastAcceptedServerReceivedAtUtc = serverReceivedAtUtc;
}

static DateTimeOffset? EstimateServerStartedAtUtc(
    DateTimeOffset? lastAcceptedServerReceivedAtUtc,
    long lastAcceptedServerElapsedMs,
    long fallbackClientElapsedMs)
{
    if (lastAcceptedServerReceivedAtUtc is null)
    {
        return null;
    }

    var elapsedMs = lastAcceptedServerElapsedMs > 0
        ? lastAcceptedServerElapsedMs
        : fallbackClientElapsedMs;

    return lastAcceptedServerReceivedAtUtc.Value - TimeSpan.FromMilliseconds(Math.Max(0, elapsedMs));
}

static long GetServerDisplayElapsedMs(RunnerRuntimeState runner)
{
    if (runner.State.Status == RunStatus.Ready)
    {
        return 0;
    }

    if (runner.ServerStartedAtUtc is null)
    {
        return runner.LastAcceptedServerElapsedMs > 0
            ? runner.LastAcceptedServerElapsedMs
            : runner.LastAcceptedClientElapsedMs;
    }

    var end = runner.State.Status == RunStatus.Finished
        ? runner.ServerFinishedAtUtc ?? runner.LastAcceptedServerReceivedAtUtc ?? DateTimeOffset.UtcNow
        : DateTimeOffset.UtcNow;

    return Math.Max(0, (long)(end - runner.ServerStartedAtUtc.Value).TotalMilliseconds);
}

static long GetServerElapsedMsAt(RunnerRuntimeState runner, DateTimeOffset atUtc)
{
    if (runner.ServerStartedAtUtc is null)
    {
        return runner.LastAcceptedServerElapsedMs > 0
            ? runner.LastAcceptedServerElapsedMs
            : runner.LastAcceptedClientElapsedMs;
    }

    return Math.Max(0, (long)(atUtc - runner.ServerStartedAtUtc.Value).TotalMilliseconds);
}

static string FormatSignedMs(long ms)
{
    var sign = ms > 0 ? "+" : ms < 0 ? "-" : "+/-";
    return sign + FormatMs(Math.Abs(ms));
}

static string FormatMs(long ms)
{
    var time = TimeSpan.FromMilliseconds(ms);

    return time.Hours > 0
        ? time.ToString(@"h\:mm\:ss\.fff")
        : time.ToString(@"m\:ss\.fff");
}

static CameraStateResponse ToCameraStateResponse(
    string runnerId,
    RunnerRuntimeState runner,
    TimeSpan cameraDisconnectThreshold,
    string currentAttemptId)
{
    var cameraAttemptIsCurrent =
        !string.IsNullOrWhiteSpace(runner.CameraAttemptId) &&
        string.Equals(runner.CameraAttemptId, currentAttemptId, StringComparison.OrdinalIgnoreCase);

    return new CameraStateResponse
    {
        RunnerId = runnerId,
        AttemptId = runner.CameraAttemptId,
        CurrentAttemptId = currentAttemptId,
        Online = cameraAttemptIsCurrent && IsCameraOnline(runner, cameraDisconnectThreshold),
        Status = cameraAttemptIsCurrent ? GetCameraStatus(runner, cameraDisconnectThreshold) : "offline",
        ClientId = cameraAttemptIsCurrent ? runner.CameraClientId : null,
        LastHeartbeatAtUtc = cameraAttemptIsCurrent ? runner.CameraLastHeartbeatAtUtc : null,
        ViewerCount = cameraAttemptIsCurrent ? runner.CameraViewerCount : 0
    };
}

static bool IsCameraOnline(RunnerRuntimeState runner, TimeSpan cameraDisconnectThreshold)
{
    return runner.CameraLastHeartbeatAtUtc is not null
        && DateTimeOffset.UtcNow - runner.CameraLastHeartbeatAtUtc.Value <= cameraDisconnectThreshold
        && !string.Equals(runner.CameraStatus, "offline", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(runner.CameraStatus, "stopped", StringComparison.OrdinalIgnoreCase);
}

static string GetCameraStatus(RunnerRuntimeState runner, TimeSpan cameraDisconnectThreshold)
{
    if (runner.CameraLastHeartbeatAtUtc is null)
    {
        return "offline";
    }

    if (DateTimeOffset.UtcNow - runner.CameraLastHeartbeatAtUtc.Value > cameraDisconnectThreshold)
    {
        return "stale";
    }

    return string.IsNullOrWhiteSpace(runner.CameraStatus)
        ? "online"
        : runner.CameraStatus;
}

static void CleanupExpiredMediaOffers(
    Dictionary<string, Dictionary<string, MediaOfferState>> mediaOffers,
    TimeSpan mediaOfferTtl)
{
    var now = DateTimeOffset.UtcNow;

    foreach (var runnerId in mediaOffers.Keys.ToArray())
    {
        var runnerOffers = mediaOffers[runnerId];

        foreach (var viewerId in runnerOffers.Keys.ToArray())
        {
            var offer = runnerOffers[viewerId];

            if (now - offer.CreatedAtUtc > mediaOfferTtl)
            {
                runnerOffers.Remove(viewerId);
            }
        }

        if (runnerOffers.Count == 0)
        {
            mediaOffers.Remove(runnerId);
        }
    }
}

static string? GetRequestAttemptId(HttpRequest request, string? payloadAttemptId)
{
    var normalizedPayloadAttemptId = NormalizeAttemptId(payloadAttemptId);

    if (normalizedPayloadAttemptId is not null)
    {
        return normalizedPayloadAttemptId;
    }

    if (request.Query.TryGetValue("attemptId", out var values))
    {
        return NormalizeAttemptId(values.FirstOrDefault());
    }

    return null;
}

static string? NormalizeAttemptId(string? attemptId)
{
    return string.IsNullOrWhiteSpace(attemptId)
        ? null
        : attemptId.Trim();
}


public sealed class RunnerRuntimeState
{
    public RunState State { get; set; } = RunState.Ready;
    public long LastAcceptedClientElapsedMs { get; set; }
    public DateTimeOffset? LastAcceptedServerReceivedAtUtc { get; set; }
    public long LastAcceptedServerElapsedMs { get; set; }
    public DateTimeOffset? ServerStartedAtUtc { get; set; }
    public DateTimeOffset? ServerFinishedAtUtc { get; set; }
    public string? ClientId { get; set; }
    public DateTimeOffset? LastHeartbeatAtUtc { get; set; }

    public string CameraStatus { get; set; } = "offline";
    public string? CameraAttemptId { get; set; }
    public string? CameraClientId { get; set; }
    public DateTimeOffset? CameraLastHeartbeatAtUtc { get; set; }
    public int CameraViewerCount { get; set; }

    public long StateVersion { get; set; }
    public bool AdminControlMode { get; set; }
    public bool InputLocked { get; set; }
    public string? InputLockReason { get; set; }
    public string? InputLockSource { get; set; }
    public string? InputLockSourceEventId { get; set; }
    public DateTimeOffset? InputLockedAtUtc { get; set; }

    public List<CompletedSplitRuntimeState> CompletedSplits { get; } = [];
}

public sealed record CameraIceServersResponse
{
    public required string RunId { get; init; }
    public required IReadOnlyList<CameraIceServerConfig> IceServers { get; init; }
    public required string IceTransportPolicy { get; init; }
}

public sealed record CameraIceServerConfig
{
    [JsonPropertyName("urls")]
    public required IReadOnlyList<string> Urls { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("credential")]
    public string? Credential { get; init; }
}

public sealed record CameraHeartbeatRequest
{
    public string? AttemptId { get; init; }
    public string? ClientId { get; init; }
    public string? Status { get; init; }
    public int ViewerCount { get; init; }
}

public sealed record CameraStateResponse
{
    public required string RunnerId { get; init; }
    public string? AttemptId { get; init; }
    public required string CurrentAttemptId { get; init; }
    public required bool Online { get; init; }
    public required string Status { get; init; }
    public string? ClientId { get; init; }
    public DateTimeOffset? LastHeartbeatAtUtc { get; init; }
    public required int ViewerCount { get; init; }
}

public sealed record MediaOfferRequest
{
    public string? AttemptId { get; init; }
    public required string ViewerId { get; init; }
    public required string OfferSdp { get; init; }
}

public sealed class MediaOfferState
{
    public required string ViewerId { get; init; }
    public required string OfferSdp { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public string? AnswerSdp { get; set; }
    public DateTimeOffset? AnsweredAtUtc { get; set; }
}

public sealed record PendingMediaOffersResponse
{
    public required IReadOnlyList<PendingMediaOfferResponse> Offers { get; init; }
}

public sealed record PendingMediaOfferResponse
{
    public required string ViewerId { get; init; }
    public required string OfferSdp { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed record MediaAnswerRequest
{
    public string? AttemptId { get; init; }
    public required string ViewerId { get; init; }
    public required string AnswerSdp { get; init; }
}

public sealed record MediaAnswerResponse
{
    public required bool Ready { get; init; }
    public required string ViewerId { get; init; }
    public string? AnswerSdp { get; init; }
}

public sealed record CompletedSplitRuntimeState
{
    public required int SplitIndex { get; init; }
    public required string Name { get; init; }
    public required long ClientElapsedMs { get; init; }
    public long ServerElapsedMs { get; init; }
    public long DeltaMs { get; init; }
    public required DateTimeOffset ServerReceivedAtUtc { get; init; }
}

public sealed record LoadRunRequest
{
    public required string RunId { get; init; }
}

public sealed record LoadRunResponse
{
    public required string Status { get; init; }
    public required string RunId { get; init; }
    public required string AttemptId { get; init; }
    public required string Game { get; init; }
    public required string Category { get; init; }
    public required int SplitCount { get; init; }
    public required string ConfigPath { get; init; }
    public required string AssetsRoot { get; init; }
    public required string AssetsPath { get; init; }
}
public sealed record RunAssetsResponse
{
    public required string RunId { get; init; }

    public required bool HasSplits { get; init; }
    public string? SplitsFileName { get; init; }
    public string? SplitsUrl { get; init; }
    public required int SplitCount { get; init; }
    public required string SplitSource { get; init; }

    public required bool HasAutosplitter { get; init; }
    public string? AutosplitterFileName { get; init; }
    public string? AutosplitterUrl { get; init; }
}

public sealed record RunnerInputLockRequest
{
    public required string AttemptId { get; init; }
    public string? Source { get; init; }
    public string? SourceEventId { get; init; }
    public string? Reason { get; init; }
}

public sealed record InputLockResponse
{
    public required string RunId { get; init; }
    public required string AttemptId { get; init; }
    public required string RunnerId { get; init; }
    public required bool Accepted { get; init; }
    public string? RejectReason { get; init; }
    public required RunStatus Status { get; init; }
    public required int LastCompletedSplitIndex { get; init; }
    public long? FinishedAtMs { get; init; }
    public required long StateVersion { get; init; }
    public required bool AdminControlMode { get; init; }
}

public sealed record AdminActionResponse
{
    public required string RunId { get; init; }
    public required string AttemptId { get; init; }
    public required string RunnerId { get; init; }
    public required string Action { get; init; }
    public required bool Accepted { get; init; }
    public string? RejectReason { get; init; }
    public required RunStatus Status { get; init; }
    public required int LastCompletedSplitIndex { get; init; }
    public long? FinishedAtMs { get; init; }
    public required long StateVersion { get; init; }
    public required bool AdminControlMode { get; init; }
}

public sealed record ClientEventRequest
{
    public string? RunnerId { get; init; }
    public required string Type { get; init; }
    public required string AttemptId { get; init; }
    public string? ClientId { get; init; }
    public required string ClientEventId { get; init; }
    public int? SplitIndex { get; init; }
    public long ClientElapsedMs { get; init; }
    public RunTimingSource TimingSource { get; init; } = RunTimingSource.RunnerStopwatch;
    public long? LiveSplitRealTimeMs { get; init; }
    public long? LiveSplitGameTimeMs { get; init; }
    public string? SourceEventId { get; init; }
    public DateTimeOffset? SourceOccurredAtUtc { get; init; }
}

public sealed record RunnerServerStateResponse
{
    public required int StateApiVersion { get; init; }
    public required string RunId { get; init; }
    public required string AttemptId { get; init; }
    public required string RunnerId { get; init; }
    public required RunStatus Status { get; init; }
    public required int LastCompletedSplitIndex { get; init; }
    public long? FinishedAtMs { get; init; }
    public required long DisplayElapsedMs { get; init; }
    public long ClientElapsedMs { get; init; }
    public long ServerElapsedMs { get; init; }
    public long TimerDeltaMs { get; init; }
    public required IReadOnlyList<CompletedSplitResponse> CompletedSplits { get; init; }
    public required long StateVersion { get; init; }
    public required bool AdminControlMode { get; init; }
}

public sealed record ServerEventResponse
{
    public required bool Accepted { get; init; }
    public bool AlreadyProcessed { get; init; }
    public string? RunnerId { get; init; }
    public string? RejectReason { get; init; }
    public required RunStatus Status { get; init; }
    public required int LastCompletedSplitIndex { get; init; }
    public long? FinishedAtMs { get; init; }
    public long StateVersion { get; init; }
    public bool AdminControlMode { get; init; }
}

public sealed record DisplayStateResponse
{
    public required string RunId { get; init; }
    public required string AttemptId { get; init; }
    public required string Game { get; init; }
    public required string Category { get; init; }
    public required TimingMode TimingMode { get; init; }
    public required RunStatus Status { get; init; }

    public string? RunnerClientId { get; init; }
    public required bool RunnerConnected { get; init; }
    public DateTimeOffset? LastRunnerHeartbeatAtUtc { get; init; }

    public required long DisplayElapsedMs { get; init; }
    public required string DisplayElapsed { get; init; }
    public required long ClientElapsedMs { get; init; }
    public required string ClientElapsed { get; init; }
    public required long ServerElapsedMs { get; init; }
    public required string ServerElapsed { get; init; }
    public required long TimerDeltaMs { get; init; }
    public required string TimerDelta { get; init; }

    public required int LastCompletedSplitIndex { get; init;}
    public string? CurrentSplitName { get; init; }

    public long? FinishedAtMs { get; init; }
    public string? FinishedAt { get; init; }

    public required IReadOnlyList<RunnerDisplayStateResponse> Runners { get; init; }
}

public sealed record RunnerDisplayStateResponse
{
    public required string RunnerId { get; init; }
    public string? ClientId { get; init; }
    public required bool Connected { get; init; }
    public DateTimeOffset? LastHeartbeatAtUtc { get; init; }
    public required bool CameraOnline { get; init; }
    public required string CameraStatus { get; init; }
    public DateTimeOffset? CameraLastHeartbeatAtUtc { get; init; }
    public required int CameraViewerCount { get; init; }

    public required RunStatus Status { get; init; }
    public required long DisplayElapsedMs { get; init; }
    public required string DisplayElapsed { get; init; }
    public required long ClientElapsedMs { get; init; }
    public required string ClientElapsed { get; init; }
    public required long ServerElapsedMs { get; init; }
    public required string ServerElapsed { get; init; }
    public required long TimerDeltaMs { get; init; }
    public required string TimerDelta { get; init; }

    public required int LastCompletedSplitIndex { get; init;}
    public string? CurrentSplitName { get; init; }
    public required IReadOnlyList<CompletedSplitResponse> CompletedSplits { get; init; }
    public required long StateVersion { get; init; }
    public required bool AdminControlMode { get; init; }

    public bool InputLocked { get; init; }
    public string? InputLockReason { get; init; }
    public string? InputLockSource { get; init; }
    public string? InputLockSourceEventId { get; init; }
    public DateTimeOffset? InputLockedAtUtc { get; init; }

    public long? FinishedAtMs { get; init; }
    public string? FinishedAt { get; init; }
    public long? ServerFinishedAtMs { get; init; }
    public string? ServerFinishedAt { get; init; }
}

public sealed record CompletedSplitResponse
{
    public required int SplitIndex { get; init; }
    public required string Name { get; init; }
    public required long ClientElapsedMs { get; init; }
    public required string ClientElapsed { get; init; }
    public required long ServerElapsedMs { get; init; }
    public required string ServerElapsed { get; init; }
    public required long DeltaMs { get; init; }
    public required string Delta { get; init; }
    public required DateTimeOffset ServerReceivedAtUtc { get; init; }
}

public sealed record ServerRunnerStateSnapshot
{
    public required string RunId { get; init; }
    public required string AttemptId { get; init; }
    public required string RunnerId { get; init; }
    public required long StateVersion { get; init; }
    public required RunStatus Status { get; init; }
    public required int LastCompletedSplitIndex { get; init; }
    public long? FinishedAtMs { get; init; }
    public required long LastAcceptedClientElapsedMs { get; init; }
    public DateTimeOffset? LastAcceptedServerReceivedAtUtc { get; init; }
    public long LastAcceptedServerElapsedMs { get; init; }
    public DateTimeOffset? ServerStartedAtUtc { get; init; }
    public DateTimeOffset? ServerFinishedAtUtc { get; init; }
    public required bool AdminControlMode { get; init; }
    public bool InputLocked { get; init; }
    public string? InputLockReason { get; init; }
    public string? InputLockSource { get; init; }
    public string? InputLockSourceEventId { get; init; }
    public DateTimeOffset? InputLockedAtUtc { get; init; }
    public required string AdminAction { get; init; }
    public required IReadOnlyList<CompletedSplitRuntimeState> CompletedSplits { get; init; }
    public required DateTimeOffset SavedAtUtc { get; init; }
}

public sealed record AdminAuditLogEntry
{
    public required string RunId { get; init; }
    public required string AttemptId { get; init; }
    public required string RunnerId { get; init; }
    public required string Action { get; init; }
    public required bool Accepted { get; init; }
    public string? RejectReason { get; init; }
    public required RunStatus StatusAfter { get; init; }
    public required int LastCompletedSplitIndexAfter { get; init; }
    public long? FinishedAtMsAfter { get; init; }
    public required long StateVersionAfter { get; init; }
    public required bool AdminControlModeAfter { get; init; }
    public required DateTimeOffset LoggedAtUtc { get; init; }
}

public sealed record ServerRunLogEntry
{
    public required string RunId { get; init; }
    public string? AttemptId { get; init; }
    public string? RunnerId { get; init; }
    public required string Game { get; init; }
    public required string Category { get; init; }
    public required TimingMode TimingMode { get; init; }

    public required string ClientEventId { get; init; }
    public required RunEventKind Kind { get; init; }
    public int? SplitIndex { get; init; }
    public required long ClientElapsedMs { get; init; }

    public RunTimingSource TimingSource { get; init; } = RunTimingSource.RunnerStopwatch;
    public long? LiveSplitRealTimeMs { get; init; }
    public long? LiveSplitGameTimeMs { get; init; }
    public string? SourceEventId { get; init; }
    public DateTimeOffset? SourceOccurredAtUtc { get; init; }

    public required bool Accepted { get; init; }
    public string? RejectReason { get; init; }

    public required RunStatus ServerStatusAfterEvent { get; init; }
    public long? ServerFinishedAtMsAfterEvent { get; init; }
    public long? ServerElapsedMsAfterEvent { get; init; }

    public required DateTimeOffset ServerReceivedAtUtc { get; init; }
}

public sealed record HeartbeatRequest
{
    public string? RunnerId { get; init; }
    public string? ClientId { get; init; }
    public required string AttemptId { get; init; }
}

public sealed record HeartbeatResponse
{
    public required bool Accepted { get; init; }
    public string? RunnerId { get; init; }
    public string? RejectReason { get; init; }
    public string? ActiveClientId { get; init; }
    public required RunStatus Status { get; init; }
    public required DateTimeOffset ServerReceivedAtUtc { get; init; }
}

public sealed record AttemptResponse
{
    public required string RunId { get; init; }
    public required string AttemptId { get; init; }
    public required RunStatus Status { get; init; }
    public required int LastCompletedSplitIndex { get; init; }
    public long? FinishedAtMs { get; init; }
    public required IReadOnlyList<RunnerAttemptResponse> Runners { get; init; }
}

public sealed record RunnerAttemptResponse
{
    public required string RunnerId { get; init; }
    public required RunStatus Status { get; init; }
    public required int LastCompletedSplitIndex { get; init; }
    public long? FinishedAtMs { get; init; }
}



