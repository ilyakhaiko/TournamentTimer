using TournamentTimer.Core;
using TournamentTimer.Runner;

var serverUrl = Environment.GetEnvironmentVariable("TOURNAMENT_TIMER_SERVER")
    ?? "http://localhost:5177";

var runnerClientId = Environment.MachineName + "-" + Guid.NewGuid().ToString("N")[..8];
var runnerId = GetRunnerId(args);

var options = new RunnerSessionOptions
{
    ServerUrl = serverUrl,
    RunId = GetRunId(args),
    ConfigPath = GetConfigFilePath(args),
    RunnerId = runnerId,
    LogPath = GetExplicitLogPathArg(args),
    ExplicitLogPath = HasExplicitLogPath(args),
    RunnerClientId = runnerClientId
};

var createResult = await RunnerSession.CreateAsync(options);

foreach (var message in createResult.Messages)
{
    Console.WriteLine(message);
}

if (!createResult.Success)
{
    foreach (var error in createResult.Errors)
    {
        Console.WriteLine(error);
    }

    return;
}

await using var session = createResult.Session!;

Console.WriteLine("TournamentTimer Runner");
Console.WriteLine("---------------------------------");
Console.WriteLine($"Attempt:         {session.AttemptId}");
Console.WriteLine($"RunnerId:        {session.RunnerId}");
Console.WriteLine($"Log mode:        {session.LogModeLabel}");
Console.WriteLine($"Local event log: {session.LogFilePath}");
Console.WriteLine($"Server sync log:  {session.SyncLogFilePath}");
Console.WriteLine($"Server:          {session.ServerUrl}");
Console.WriteLine();

if (session.State.Status != RunStatus.Ready)
{
    Console.WriteLine($"RESTORED: {session.State.Status}, base time {FormatMs(session.BaseElapsedMs)}");
    Console.WriteLine();
}

Console.WriteLine("S     = Start");
Console.WriteLine("Space = Split");
Console.WriteLine("F     = Finish manually");
Console.WriteLine("Q     = Quit");
Console.WriteLine();

while (true)
{
    PrintStatus(session);

    var key = Console.ReadKey(intercept: true);

    if (key.Key == ConsoleKey.Q)
    {
        await session.StopAsync();
        break;
    }

    RunnerSessionActionResult? actionResult = key.Key switch
    {
        ConsoleKey.S => await session.StartAsync(),
        ConsoleKey.Spacebar => await session.SplitAsync(),
        ConsoleKey.F => await session.FinishAsync(),
        _ => null
    };

    if (actionResult is null)
    {
        continue;
    }

    PrintActionResult(actionResult);

    if (actionResult.FatalServerMismatch)
    {
        await session.StopAsync();
        return;
    }
}

static void PrintActionResult(RunnerSessionActionResult result)
{
    if (!result.LocalAccepted)
    {
        Console.WriteLine();

        if (result.LocalRejectReason == "run_already_finished")
        {
            Console.WriteLine("Run already finished.");
            return;
        }

        if (result.LocalRejectReason == "no_next_split")
        {
            Console.WriteLine("No next split.");
            return;
        }

        Console.WriteLine($"REJECTED LOCAL: {result.LocalRejectReason}");
        return;
    }

    Console.WriteLine();
    Console.WriteLine($"ACCEPTED LOCAL: {result.EventName} at {FormatMs(result.ClientElapsedMs)}");
    Console.WriteLine("LOGGED LOCAL");

    var serverResponse = result.ServerResponse;

    if (serverResponse is null)
    {
        return;
    }

    if (!serverResponse.Sent)
    {
        Console.WriteLine($"SERVER SYNC FAILED: {serverResponse.TransportError}");
        return;
    }

    if (serverResponse.Accepted)
    {
        var serverLabel = serverResponse.AlreadyProcessed
            ? "ALREADY PROCESSED SERVER"
            : "ACCEPTED SERVER";

        Console.WriteLine($"{serverLabel}: status={serverResponse.Status}, finished={FormatNullableMs(serverResponse.FinishedAtMs)}");
        return;
    }

    Console.WriteLine($"REJECTED SERVER: {serverResponse.RejectReason}");
    Console.WriteLine();
    Console.WriteLine("FATAL: local/server state mismatch.");
    Console.WriteLine("The local event was accepted, but the server rejected it.");
    Console.WriteLine("Use the previous local log to resume, or reset the server from admin before starting a new run.");
}

static void PrintStatus(RunnerSession session)
{
    Console.WriteLine();
    Console.WriteLine($"Status: {session.State.Status}");

    if (session.State.Status == RunStatus.Running)
    {
        Console.WriteLine($"Time:   {FormatMs(session.CurrentElapsedMs)}");
    }

    if (session.State.Status == RunStatus.Running && session.CurrentSplitName is not null)
    {
        Console.WriteLine($"Next:   {session.CurrentSplitName}");
    }
}

static string? GetRunId(string[] args)
{
    var runIdArg = args.FirstOrDefault(arg => arg.StartsWith("--runId=", StringComparison.OrdinalIgnoreCase));

    if (runIdArg is not null)
    {
        return runIdArg["--runId=".Length..];
    }

    return Environment.GetEnvironmentVariable("TOURNAMENT_TIMER_RUN_ID");
}

static string GetRunnerId(string[] args)
{
    var runnerIdArg = args.FirstOrDefault(arg => arg.StartsWith("--runnerId=", StringComparison.OrdinalIgnoreCase));

    if (runnerIdArg is not null)
    {
        var value = runnerIdArg["--runnerId=".Length..].Trim();

        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
    }

    return Environment.GetEnvironmentVariable("TOURNAMENT_TIMER_RUNNER_ID") ?? "runner-1";
}

static string GetConfigFilePath(string[] args)
{
    var configArg = args.FirstOrDefault(arg => arg.StartsWith("--config=", StringComparison.OrdinalIgnoreCase));

    if (configArg is not null)
    {
        return Path.GetFullPath(configArg["--config=".Length..]);
    }

    return Path.GetFullPath(Path.Combine("configs", "local-test-run.json"));
}

static bool HasExplicitLogPath(string[] args)
{
    return GetExplicitLogPathArg(args) is not null;
}

static string? GetExplicitLogPathArg(string[] args)
{
    var namedLogPathArg = args.FirstOrDefault(arg =>
        arg.StartsWith("--logPath=", StringComparison.OrdinalIgnoreCase));

    if (namedLogPathArg is not null)
    {
        var value = namedLogPathArg["--logPath=".Length..];

        return string.IsNullOrWhiteSpace(value)
            ? null
            : value;
    }

    return args.FirstOrDefault(arg =>
        !arg.StartsWith("--config=", StringComparison.OrdinalIgnoreCase)
        && !arg.StartsWith("--runId=", StringComparison.OrdinalIgnoreCase)
        && !arg.StartsWith("--runnerId=", StringComparison.OrdinalIgnoreCase)
        && !arg.StartsWith("--logPath=", StringComparison.OrdinalIgnoreCase));
}

static string FormatMs(long ms)
{
    var time = TimeSpan.FromMilliseconds(ms);

    return time.Hours > 0
        ? time.ToString(@"h\:mm\:ss\.fff")
        : time.ToString(@"m\:ss\.fff");
}

static string FormatNullableMs(long? ms)
{
    return ms is null ? "null" : FormatMs(ms.Value);
}