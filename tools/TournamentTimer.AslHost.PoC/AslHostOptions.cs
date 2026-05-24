using System;
using System.IO;
using System.Linq;

internal sealed class AslHostOptions
{
    public string AssetsDir { get; private set; }
    public string AutosplitterPath { get; private set; }
    public string SplitsPath { get; private set; }
    public string SettingsPath { get; private set; }
    public string BridgeUrl { get; private set; }
    public string RunsRoot { get; private set; }
    public bool DebugMode { get; private set; }
    public bool ConfigureMode { get; private set; }
    public bool TraceMode { get; private set; }
    public bool ManualKeys { get; private set; }
    public bool ShowHelp { get; private set; }
    public int TickMs { get; private set; }
    public int StatusEveryTicks { get; private set; }
    public int TraceEveryTicks { get; private set; }

    private AslHostOptions()
    {
        var projectRoot = GuessProjectRoot();
        AssetsDir = Path.Combine(projectRoot, "server-runs", "assets", "local-test-run");
        AutosplitterPath = Path.Combine(AssetsDir, "autosplitter.asl");
        SplitsPath = Path.Combine(AssetsDir, "splits.lss");
        SettingsPath = Path.Combine(AssetsDir, "asl-settings.json");
        RunsRoot = GuessRunsRoot(projectRoot);
        BridgeUrl = "http://127.0.0.1:52991/api/local/livesplit/events";
        DebugMode = false;
        ConfigureMode = false;
        TraceMode = false;
        ManualKeys = false;
        ShowHelp = false;
        TickMs = 300;
        StatusEveryTicks = 10;
        TraceEveryTicks = 10;
    }

    public static AslHostOptions Parse(string[] args)
    {
        var options = new AslHostOptions();

        foreach (var arg in args ?? Array.Empty<string>())
        {
            if (EqualsAny(arg, "--help", "-h", "/?"))
            {
                options.ShowHelp = true;
                continue;
            }

            if (EqualsAny(arg, "--debug"))
            {
                options.DebugMode = true;
                continue;
            }

            if (EqualsAny(arg, "--configure", "--inspect"))
            {
                options.ConfigureMode = true;
                continue;
            }

            if (EqualsAny(arg, "--trace"))
            {
                options.TraceMode = true;
                continue;
            }

            if (EqualsAny(arg, "--manualKeys"))
            {
                options.ManualKeys = true;
                continue;
            }

            var assetsDir = ReadArgValue(arg, "--assetsDir=");
            if (assetsDir != null)
            {
                options.AssetsDir = Path.GetFullPath(assetsDir);
                options.AutosplitterPath = Path.Combine(options.AssetsDir, "autosplitter.asl");
                options.SplitsPath = Path.Combine(options.AssetsDir, "splits.lss");
                options.SettingsPath = Path.Combine(options.AssetsDir, "asl-settings.json");
                continue;
            }

            var runsRoot = ReadArgValue(arg, "--runsRoot=");
            if (runsRoot != null)
            {
                options.RunsRoot = Path.GetFullPath(runsRoot);
                continue;
            }

            var bridgeUrl = ReadArgValue(arg, "--bridgeUrl=");
            if (bridgeUrl != null)
            {
                options.BridgeUrl = bridgeUrl.Trim();
                continue;
            }

            var aslPath = ReadArgValue(arg, "--aslPath=");
            if (aslPath != null)
            {
                options.AutosplitterPath = Path.GetFullPath(aslPath);
                continue;
            }

            var lssPath = ReadArgValue(arg, "--lssPath=");
            if (lssPath != null)
            {
                options.SplitsPath = Path.GetFullPath(lssPath);
                continue;
            }

            var settingsPath = ReadArgValue(arg, "--settingsPath=");
            if (settingsPath != null)
            {
                options.SettingsPath = Path.GetFullPath(settingsPath);
                continue;
            }

            var tickMs = ReadIntArgValue(arg, "--tickMs=");
            if (tickMs != null)
            {
                options.TickMs = Math.Max(50, tickMs.Value);
                continue;
            }

            var statusEvery = ReadIntArgValue(arg, "--statusEvery=");
            if (statusEvery != null)
            {
                options.StatusEveryTicks = Math.Max(1, statusEvery.Value);
                continue;
            }

            var traceEvery = ReadIntArgValue(arg, "--traceEvery=");
            if (traceEvery != null)
            {
                options.TraceEveryTicks = Math.Max(1, traceEvery.Value);
                continue;
            }

            throw new InvalidOperationException("Unknown argument: " + arg);
        }

        return options;
    }

    public void ValidateFiles()
    {
        if (!File.Exists(AutosplitterPath))
        {
            throw new FileNotFoundException("autosplitter.asl not found", AutosplitterPath);
        }

        if (!File.Exists(SplitsPath))
        {
            throw new FileNotFoundException("splits.lss not found", SplitsPath);
        }
    }

    public static void PrintHelp()
    {
        Console.WriteLine("TournamentTimer ASL Host");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  TournamentTimer.AslHost.PoC.exe [--configure] [--debug] [--trace] [--manualKeys] [--assetsDir=PATH] [--bridgeUrl=URL]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --configure          Open ASL settings editor and save asl-settings.json, then exit.");
        Console.WriteLine("  --inspect            Alias for --configure.");
        Console.WriteLine("  --debug              Simulate start/split/split at ticks 3/5/7.");
        Console.WriteLine("  --trace              Print ASL State/OldState/Vars details.");
        Console.WriteLine("  --traceEvery=N       Print trace every N ticks. Default: 10.");
        Console.WriteLine("  --manualKeys         Enable S=Start, Space=Split, Q=quit in live/debug mode.");
        Console.WriteLine("  --assetsDir=PATH     Directory with autosplitter.asl and splits.lss.");
        Console.WriteLine("  --runsRoot=PATH      Root folder for ASL run assets. Default: server-runs/assets.");
        Console.WriteLine("  --bridgeUrl=URL      Runner local bridge endpoint.");
        Console.WriteLine("  --aslPath=PATH       Explicit ASL file path.");
        Console.WriteLine("  --lssPath=PATH       Explicit LiveSplit .lss file path.");
        Console.WriteLine("  --settingsPath=PATH  Explicit ASL settings preset json path.");
        Console.WriteLine("  --tickMs=N           Detector loop delay in ms. Default: 300.");
        Console.WriteLine("  --statusEvery=N      Print status every N ticks. Default: 10.");
    }

    private static string GuessProjectRoot()
    {
        var current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "server-runs")) ||
                Directory.Exists(Path.Combine(current.FullName, "configs")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
    }


    private static string GuessRunsRoot(string projectRoot)
    {
        return Path.Combine(projectRoot, "server-runs", "assets");
    }

    private static bool EqualsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadArgValue(string arg, string prefix)
    {
        if (!arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var value = arg.Substring(prefix.Length).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int? ReadIntArgValue(string arg, string prefix)
    {
        var value = ReadArgValue(arg, prefix);
        if (value == null)
        {
            return null;
        }

        if (!int.TryParse(value, out var result))
        {
            throw new InvalidOperationException("Invalid integer argument: " + arg);
        }

        return result;
    }
}
