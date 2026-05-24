using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace TournamentTimer.AslHost.Launcher;

public partial class MainWindow : Window
{
    private const string DefaultBridgeUrl = "http://127.0.0.1:52991/api/local/livesplit/events";
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20h1 = 19;

    private Process? _aslHostProcess;
    private string? _runningArch;
    private bool _restartAfterExit;
    private bool _stopRequestedByLauncher;

    public MainWindow()
    {
        InitializeComponent();

        SourceInitialized += MainWindow_SourceInitialized;
        StartButton.Click += (_, _) => StartAslHost();
        StopButton.Click += async (_, _) => await StopAslHostFromButtonAsync();
        RestartButton.Click += async (_, _) => await RestartAslHostAsync();

        Closing += (_, _) => StopAslHostForExit();

        SetStopped();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        EnableDarkWindowChrome();
    }

    private void EnableDarkWindowChrome()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var enabled = 1;

            var result = DwmSetWindowAttribute(
                hwnd,
                DwmwaUseImmersiveDarkMode,
                ref enabled,
                sizeof(int));

            if (result != 0)
            {
                _ = DwmSetWindowAttribute(
                    hwnd,
                    DwmwaUseImmersiveDarkModeBefore20h1,
                    ref enabled,
                    sizeof(int));
            }
        }
        catch
        {
            // Cosmetic only.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);

    private void StartAslHost()
    {
        if (_aslHostProcess is not null && !_aslHostProcess.HasExited)
        {
            AppendLog("ASL Host is already running.");
            return;
        }

        try
        {
            var runId = RunIdTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(runId))
            {
                AppendLog("ERROR: RunId is required.");
                return;
            }

            var arch = X86RadioButton.IsChecked == true ? "x86" : "x64";
            var repoRoot = FindRepoRoot();
            var aslHostRoot = Path.Combine(repoRoot, "artifacts", "asl-host");
            var hostDir = Path.Combine(aslHostRoot, arch);
            var exePath = Path.Combine(hostDir, "TournamentTimer.AslHost.PoC.exe");
            var candidateAssetsDirs = GetCandidateAssetsDirs(repoRoot, runId);
            var assetsDir = ResolveAssetsDir(candidateAssetsDirs);

            AppendLog($"RunId:  {runId}");
            AppendLog($"Arch:   {arch}");
            AppendLog($"Host:   {exePath}");
            AppendLog($"Assets: {assetsDir}");
            AppendLog($"Bridge: {DefaultBridgeUrl}");

            if (!File.Exists(exePath))
            {
                AppendLog("ERROR: ASL Host exe not found. Build ASL Host first.");
                SetError();
                return;
            }

            var hasAutosplitter = File.Exists(Path.Combine(assetsDir, "autosplitter.asl"));
            var hasSplits = File.Exists(Path.Combine(assetsDir, "splits.lss"));

            if (!hasAutosplitter || !hasSplits)
            {
                AppendLog("ERROR: required ASL assets not found for this RunId.");

                if (!hasAutosplitter)
                {
                    AppendLog("Missing: autosplitter.asl");
                }

                if (!hasSplits)
                {
                    AppendLog("Missing: splits.lss");
                }

                AppendAssetSearchHelp(candidateAssetsDirs);
                SetError();
                return;
            }

            var arguments = new[]
            {
                $"--assetsDir={assetsDir}",
                $"--bridgeUrl={DefaultBridgeUrl}",
                "--tickMs=300",
                "--statusEvery=10"
            };

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = hostDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    Dispatcher.BeginInvoke(() => AppendLog(e.Data));
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    Dispatcher.BeginInvoke(() => AppendLog("ERR: " + e.Data));
                }
            };

            process.Exited += (_, _) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    var exitCode = SafeGetExitCode(process);
                    var shouldRestart = _restartAfterExit;
                    var stoppedByLauncher = _stopRequestedByLauncher;

                    if (shouldRestart)
                    {
                        AppendLog("ASL Host stopped for restart.");
                    }
                    else if (stoppedByLauncher)
                    {
                        AppendLog("ASL Host stopped by launcher.");
                    }
                    else
                    {
                        AppendLog($"ASL Host exited. ExitCode={exitCode}");
                    }

                    _aslHostProcess = null;
                    _runningArch = null;
                    _restartAfterExit = false;
                    _stopRequestedByLauncher = false;

                    SetStopped();
                    process.Dispose();

                    if (shouldRestart)
                    {
                        AppendLog("Restart: starting ASL Host again...");
                        StartAslHost();
                    }
                });
            };

            if (!process.Start())
            {
                AppendLog("ERROR: failed to start ASL Host process.");
                process.Dispose();
                SetError();
                return;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _aslHostProcess = process;
            _runningArch = arch;

            SetRunning(arch);
            AppendLog($"ASL Host started: {arch}");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            SetError();
        }
    }

    private async Task StopAslHostFromButtonAsync()
    {
        var process = _aslHostProcess;

        if (process is null || process.HasExited)
        {
            AppendLog("ASL Host is not running.");
            _aslHostProcess = null;
            _runningArch = null;
            SetStopped();
            return;
        }

        StartButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        RestartButton.IsEnabled = false;

        AppendLog("Stopping ASL Host...");
        _stopRequestedByLauncher = true;

        Exception? stopError = null;

        await Task.Run(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                stopError = ex;
            }
        });

        if (stopError is not null)
        {
            _stopRequestedByLauncher = false;
            AppendLog($"ERROR: failed to stop ASL Host: {stopError.Message}");
            SetError();
            return;
        }

        AppendLog("Stop signal sent.");
    }

    private async Task RestartAslHostAsync()
    {
        var process = _aslHostProcess;

        if (process is null || process.HasExited)
        {
            AppendLog("ASL Host is not running. Starting instead.");
            StartAslHost();
            return;
        }

        AppendLog("Restarting ASL Host...");
        _restartAfterExit = true;

        await StopAslHostFromButtonAsync();
    }

    private void StopAslHostForExit()
    {
        var process = _aslHostProcess;

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                AppendLog("Stopping ASL Host on launcher exit...");
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch
        {
            // Ignore shutdown cleanup errors.
        }
        finally
        {
            _aslHostProcess = null;
            _runningArch = null;
        }
    }

    private static int? SafeGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return null;
        }
    }

    private static string[] GetCandidateAssetsDirs(string repoRoot, string runId)
    {
        return
        [
            Path.Combine(repoRoot, "server-runs", "assets", runId),
            Path.Combine(repoRoot, "TournamentTimer.Server", "server-runs", "assets", runId)
        ];
    }

    private static string ResolveAssetsDir(IEnumerable<string> candidateAssetsDirs)
    {
        var candidates = candidateAssetsDirs
            .Select(Path.GetFullPath)
            .ToArray();

        foreach (var assetsDir in candidates)
        {
            if (HasRequiredAssets(assetsDir))
            {
                return assetsDir;
            }
        }

        return candidates.Length == 0
            ? Path.GetFullPath(".")
            : candidates[^1];
    }

    private void AppendAssetSearchHelp(IEnumerable<string> candidateAssetsDirs)
    {
        AppendLog("Checked asset folders:");

        foreach (var assetsDir in candidateAssetsDirs.Select(Path.GetFullPath))
        {
            AppendLog("  - " + assetsDir);
        }

        AppendLog("Install assets with ASL Catalog, or copy autosplitter.asl and splits.lss into one of these folders.");
    }

    private static bool HasRequiredAssets(string assetsDir)
    {
        return File.Exists(Path.Combine(assetsDir, "autosplitter.asl")) &&
               File.Exists(Path.Combine(assetsDir, "splits.lss"));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var hasArtifacts = Directory.Exists(Path.Combine(directory.FullName, "artifacts"));
            var hasScripts = Directory.Exists(Path.Combine(directory.FullName, "scripts"));
            var hasTools = Directory.Exists(Path.Combine(directory.FullName, "tools"));
            var hasRunnerUi = Directory.Exists(Path.Combine(directory.FullName, "runner-ui"));
            var hasLauncher = Directory.Exists(Path.Combine(directory.FullName, "asl-host-launcher"));

            if (hasArtifacts && (hasScripts || hasTools || hasRunnerUi || hasLauncher))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("TournamentTimer root not found.");
    }

    private void SetStopped()
    {
        StatusTextBlock.Text = "Stopped";
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        RestartButton.IsEnabled = false;
        X64RadioButton.IsEnabled = true;
        X86RadioButton.IsEnabled = true;
        RunIdTextBox.IsEnabled = true;
    }

    private void SetRunning(string arch)
    {
        StatusTextBlock.Text = $"Running {arch}";
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        RestartButton.IsEnabled = true;
        X64RadioButton.IsEnabled = false;
        X86RadioButton.IsEnabled = false;
        RunIdTextBox.IsEnabled = false;
    }

    private void SetError()
    {
        StatusTextBlock.Text = "Error";
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        RestartButton.IsEnabled = false;
        X64RadioButton.IsEnabled = true;
        X86RadioButton.IsEnabled = true;
        RunIdTextBox.IsEnabled = true;
    }

    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";

        var wasAtBottom =
            LogTextBox.ExtentHeight <= LogTextBox.ViewportHeight ||
            LogTextBox.VerticalOffset + LogTextBox.ViewportHeight >= LogTextBox.ExtentHeight - 8;

        LogTextBox.AppendText(line + Environment.NewLine);

        if (wasAtBottom)
        {
            LogTextBox.ScrollToEnd();
        }
    }
}