using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using LiveSplit.ASL;
using LiveSplit.Model;
using LiveSplit.Options;
using LiveSplit.UI;
using LiveSplit.UI.Components;

internal sealed class HeadlessAslHost
{
    private readonly AslHostOptions _options;

    public HeadlessAslHost(AslHostOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public void Run()
    {
        _options.ValidateFiles();

        // LiveSplit ASL/asl-help can compile generated code during startup/update/shutdown.
        // The compiler sometimes resolves metadata references by file name only,
        // so keep the process working directory on the executable folder where
        // LiveSplit.Core.dll, System.Memory.dll, System.Text.Json.dll, etc. are copied.
        Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;

        PrepareRunComponents();

        Console.WriteLine("TournamentTimer ASL Host PoC");
        Console.WriteLine("----------------------------");
        Console.WriteLine("Assets: " + _options.AssetsDir);
        Console.WriteLine("ASL:    " + _options.AutosplitterPath);
        Console.WriteLine("LSS:    " + _options.SplitsPath);
        Console.WriteLine("Preset: " + _options.SettingsPath);
        Console.WriteLine("Bridge: " + _options.BridgeUrl);
        Console.WriteLine("Mode:   " + (_options.ConfigureMode ? "configure" : (_options.DebugMode ? "debug" : "live")));
        Console.WriteLine("Trace:  " + (_options.TraceMode ? "on" : "off"));
        Console.WriteLine("Keys:   " + (_options.ManualKeys ? "on" : "off"));
        Console.WriteLine();

        var run = LiveSplitRunLoader.LoadFromLssSimple(_options.SplitsPath);
        Console.WriteLine($"Run loaded OK: {run.GameName} / {run.CategoryName}");
        Console.WriteLine($"Segments: {run.Count}");
        Console.WriteLine();

        using (var form = new HeadlessTimerForm())
        {
            var script = ParseAutosplitter(_options.AutosplitterPath);
            var layout = new HeadlessLayout(script, _options.AutosplitterPath);
            var state = CreateHeadlessState(run, form, layout);

            // Some helper libraries, including Components/asl-help, expect the real
            // LiveSplit TimerForm to be present in Application.OpenForms["TimerForm"]
            // and to expose CurrentState. Keep a hidden compatibility form alive.
            form.CurrentState = state;
            form.Show();
            form.Hide();

            var timer = new TimerModel
            {
                CurrentState = state
            };

            state.RegisterTimerModel(timer);

            var aslSettings = script.RunStartup(state);
            var settingsPreset = AslSettingsPreset.LoadOrEmpty(_options.SettingsPath);
            settingsPreset.ApplyTo(aslSettings, _options.TraceMode);

            Console.WriteLine("ASL startup OK.");
            var aslSettingsCount = aslSettings == null ? 0 : aslSettings.OrderedSettings.Count;
            Console.WriteLine($"ASL settings: {aslSettingsCount} setting(s).");

            if (_options.TraceMode && aslSettings != null)
            {
                foreach (var item in aslSettings.OrderedSettings)
                {
                    DumpSetting(item);
                }
            }

            if (_options.ConfigureMode)
            {
                Console.WriteLine();
                Console.WriteLine("Configure mode: opening ASL settings editor.");
                Console.WriteLine("Save will write: " + _options.SettingsPath);

                using (var configureForm = new AslSettingsConfigureForm(aslSettings, _options.SettingsPath))
                {
                    Application.Run(configureForm);
                }

                try
                {
                    script.RunShutdown(state);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[asl] shutdown error: " + ex.Message);
                }

                Console.WriteLine();
                Console.WriteLine("Configure mode done.");
                return;
            }

            Console.WriteLine();
            Console.WriteLine("Starting detector loop.");

            if (_options.DebugMode)
            {
                Console.WriteLine("Debug mode: ticks 3/5/7 simulate Start/Split/Split.");
            }
            else
            {
                Console.WriteLine("Live mode: waiting for real ASL events from the game process.");
            }

            if (_options.TraceMode)
            {
                Console.WriteLine($"Trace mode: printing ASL internals every {_options.TraceEveryTicks} tick(s).");
            }

            if (_options.ManualKeys)
            {
                Console.WriteLine("Manual keys: S=Start, Space=Split, Q=quit.");
            }

            Console.WriteLine("Press Ctrl+C to stop.");
            Console.WriteLine();

            var detector = new StateChangeDetector(
                run,
                new RunnerBridgeEventSink(_options.BridgeUrl));

            var stopRequested = false;
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stopRequested = true;
                Console.WriteLine();
                Console.WriteLine("Stop requested.");
            };

            var tick = 0;
            var lastStatusKey = string.Empty;
            var lastAslUpdateErrorKey = string.Empty;
            var lastAslUpdateErrorLoggedAtUtc = DateTimeOffset.MinValue;
            var architectureMismatchHintPrinted = false;

            try
            {
                while (!stopRequested)
                {
                    tick++;

                    var before = detector.Capture(state);

                    try
                    {
                        script.Update(state);
                    }
                    catch (Exception ex)
                    {
                        LogAslUpdateError(
                            ex,
                            ref lastAslUpdateErrorKey,
                            ref lastAslUpdateErrorLoggedAtUtc,
                            ref architectureMismatchHintPrinted);
                    }

                    if (_options.DebugMode && tick == 3)
                    {
                        Console.WriteLine("[debug] timer.Start()");
                        timer.Start();
                    }

                    if (_options.DebugMode && tick == 5)
                    {
                        Console.WriteLine("[debug] timer.Split()");
                        timer.Split();
                    }

                    if (_options.DebugMode && tick == 7)
                    {
                        Console.WriteLine("[debug] timer.Split()");
                        timer.Split();
                    }

                    if (_options.ManualKeys)
                    {
                        HandleManualKeys(timer, ref stopRequested);
                    }

                    var after = detector.Capture(state);
                    detector.DetectAndSend(before, after);

                    var statusKey = BuildStatusKey(state, script);
                    var statusChanged = !string.Equals(statusKey, lastStatusKey, StringComparison.Ordinal);
                    var statusHeartbeat = _options.StatusEveryTicks > 0 &&
                        tick % _options.StatusEveryTicks == 0 &&
                        state.CurrentPhase == TimerPhase.NotRunning;

                    if (_options.DebugMode || statusChanged || statusHeartbeat)
                    {
                        Console.WriteLine(FormatStatusLine(
                            statusChanged ? "state" : "status",
                            tick,
                            state,
                            script));

                        lastStatusKey = statusKey;
                    }

                    if (_options.TraceMode && (tick <= 5 || tick % _options.TraceEveryTicks == 0))
                    {
                        TraceDumper.DumpAslScript(script, state, tick);
                    }

                    if (_options.DebugMode && tick >= 12)
                    {
                        break;
                    }

                    Thread.Sleep(_options.TickMs);
                }
            }
            finally
            {
                try
                {
                    script.RunShutdown(state);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[asl] shutdown error: " + ex.Message);
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("Done.");
    }

    private static void LogAslUpdateError(
        Exception ex,
        ref string lastErrorKey,
        ref DateTimeOffset lastLoggedAtUtc,
        ref bool architectureMismatchHintPrinted)
    {
        var message = ex.Message;
        var errorKey = NormalizeErrorKey(message);
        var now = DateTimeOffset.UtcNow;
        var sameAsPrevious = string.Equals(errorKey, lastErrorKey, StringComparison.Ordinal);
        var shouldLog = !sameAsPrevious || now - lastLoggedAtUtc >= TimeSpan.FromSeconds(10);

        if (!shouldLog)
        {
            return;
        }

        Console.WriteLine("[asl] update error: " + message);

        if (sameAsPrevious)
        {
            Console.WriteLine("[asl] repeated identical update errors are throttled.");
        }

        if (!architectureMismatchHintPrinted && IsLikelyArchitectureMismatch(message))
        {
            Console.WriteLine("[asl] hint: Possible architecture mismatch. Try x64 ASL Host for x64 games, or x86 ASL Host for x86 games.");
            architectureMismatchHintPrinted = true;
        }

        lastErrorKey = errorKey;
        lastLoggedAtUtc = now;
    }

    private static string NormalizeErrorKey(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var normalized = message.Replace("\r\n", "\n").Trim();

        return normalized.Length <= 500
            ? normalized
            : normalized.Substring(0, 500);
    }

    private static bool IsLikelyArchitectureMismatch(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var lower = message.ToLowerInvariant();

        return lower.Contains("readprocessmemory")
            || lower.Contains("writeprocessmemory")
            || lower.Contains("only part of a readprocessmemory")
            || lower.Contains("badimageformatexception")
            || lower.Contains("incorrect format")
            || lower.Contains("wrong format")
            || lower.Contains("different processor")
            || lower.Contains("processor architecture");
    }

    private static void HandleManualKeys(TimerModel timer, ref bool stopRequested)
    {
        while (Console.KeyAvailable)
        {
            var key = Console.ReadKey(intercept: true).Key;

            if (key == ConsoleKey.Q)
            {
                Console.WriteLine("[manual] quit");
                stopRequested = true;
                return;
            }

            if (key == ConsoleKey.S)
            {
                Console.WriteLine("[manual] timer.Start()");
                timer.Start();
                continue;
            }

            if (key == ConsoleKey.Spacebar)
            {
                Console.WriteLine("[manual] timer.Split()");
                timer.Split();
                continue;
            }
        }
    }


    private void PrepareRunComponents()
    {
        var sourceComponentsDir = Path.Combine(_options.AssetsDir, "Components");

        if (!Directory.Exists(sourceComponentsDir))
        {
            return;
        }

        var runtimeComponentsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Components");
        var sourceFullPath = NormalizeDirectoryPath(sourceComponentsDir);
        var runtimeFullPath = NormalizeDirectoryPath(runtimeComponentsDir);

        if (string.Equals(sourceFullPath, runtimeFullPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Directory.CreateDirectory(runtimeComponentsDir);

        var copied = 0;

        foreach (var sourceFile in Directory.GetFiles(sourceComponentsDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = sourceFile.Substring(sourceFullPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var destinationFile = Path.Combine(runtimeComponentsDir, relativePath);
            var destinationDirectory = Path.GetDirectoryName(destinationFile);

            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            File.Copy(sourceFile, destinationFile, overwrite: true);
            copied++;
        }

        if (copied > 0)
        {
            Console.WriteLine($"Run components: copied {copied} file(s) from run assets to ASL Host runtime.");
        }
    }

    private static string NormalizeDirectoryPath(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
    }

    private static string BuildStatusKey(LiveSplitState state, ASLScript script)
    {
        return string.Join("|",
            state.CurrentPhase,
            state.CurrentSplitIndex,
            state.CurrentSplit?.Name ?? "-",
            script.State == null ? "no-asl-state" : "asl-state");
    }

    private static string FormatStatusLine(
        string prefix,
        int tick,
        LiveSplitState state,
        ASLScript script)
    {
        return $"[{prefix}] tick={tick:00}: phase={state.CurrentPhase}, currentSplitIndex={state.CurrentSplitIndex}, currentSplit={state.CurrentSplit?.Name ?? "-"}, aslState={(script.State == null ? "null" : "exists")}";
    }


    private static ASLScript ParseAutosplitter(string autosplitterPath)
    {
        var originalCurrentDirectory = Environment.CurrentDirectory;

        try
        {
            Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;
            return ASLParser.Parse(File.ReadAllText(autosplitterPath));
        }
        finally
        {
            Environment.CurrentDirectory = originalCurrentDirectory;
        }
    }
    private static LiveSplitState CreateHeadlessState(IRun run, Form form, ILayout layout)
    {
        var layoutSettings = new LiveSplit.Options.LayoutSettings();
        var settings = new Settings();

        return new LiveSplitState(
            run,
            form,
            layout: layout,
            layoutSettings: layoutSettings,
            settings: settings);
    }

    private static void DumpSetting(object setting)
    {
        var type = setting.GetType();

        string GetProp(string name)
        {
            var prop = type.GetProperty(name);
            if (prop == null) return "";
            var value = prop.GetValue(setting, null);
            return value == null ? "" : value.ToString();
        }

        Console.WriteLine($"- id={GetProp("Id")}, value={GetProp("Value")}, parent={GetProp("Parent")}");
    }
}

public sealed class HeadlessTimerForm : Form
{
    public HeadlessTimerForm()
    {
        Name = "TimerForm";
        Text = "TimerForm";
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Size = new Size(1, 1);
        Opacity = 0;
    }

    public LiveSplitState CurrentState { get; set; }
}

public sealed class HeadlessLayout : ILayout
{
    public HeadlessLayout(ASLScript script, string scriptPath)
    {
        Settings = new LiveSplit.Options.LayoutSettings();
        LayoutComponents = new List<ILayoutComponent>
        {
            new LayoutComponent("Headless ASL", new ASLComponent(script, scriptPath))
        };
    }

    public LayoutMode Mode { get; set; }
    public int VerticalWidth { get; set; } = 300;
    public int VerticalHeight { get; set; } = 600;
    public int HorizontalWidth { get; set; } = 600;
    public int HorizontalHeight { get; set; } = 80;
    public int X { get; set; }
    public int Y { get; set; }
    public bool HasChanged { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public LiveSplit.Options.LayoutSettings Settings { get; set; }
    public IList<ILayoutComponent> LayoutComponents { get; set; }
    public IEnumerable<IComponent> Components => LayoutComponents.Select(component => component.Component);

    public object Clone()
    {
        return this;
    }
}

// Keep this class name exactly ASLComponent: asl-help searches layout components
// by GetType().Name == "ASLComponent" and then reads Script and the private
// _settings.ScriptPath field via reflection.
public sealed class ASLComponent : IComponent
{
    private readonly HeadlessAslComponentSettings _settings;

    public ASLComponent(ASLScript script, string scriptPath)
    {
        Script = script ?? throw new ArgumentNullException(nameof(script));
        _settings = new HeadlessAslComponentSettings(scriptPath);
    }

    public ASLScript Script { get; }

    public string ComponentName => "ASL Script";
    public float HorizontalWidth => 0;
    public float MinimumHeight => 0;
    public float VerticalHeight => 0;
    public float MinimumWidth => 0;
    public float PaddingTop => 0;
    public float PaddingBottom => 0;
    public float PaddingLeft => 0;
    public float PaddingRight => 0;
    public IDictionary<string, Action> ContextMenuControls { get; } = new Dictionary<string, Action>();

    public void DrawHorizontal(Graphics g, LiveSplitState state, float height, Region clipRegion)
    {
    }

    public void DrawVertical(Graphics g, LiveSplitState state, float width, Region clipRegion)
    {
    }

    public Control GetSettingsControl(LayoutMode mode)
    {
        return new Control();
    }

    public XmlNode GetSettings(XmlDocument document)
    {
        return document.CreateElement("Settings");
    }

    public void SetSettings(XmlNode settings)
    {
    }

    public void Update(IInvalidator invalidator, LiveSplitState state, float width, float height, LayoutMode mode)
    {
    }

    public void Dispose()
    {
    }
}

public sealed class HeadlessAslComponentSettings
{
    public HeadlessAslComponentSettings(string scriptPath)
    {
        ScriptPath = scriptPath;
    }

    public string ScriptPath { get; }
}

