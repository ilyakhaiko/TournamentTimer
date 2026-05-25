using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using TournamentTimer.Core;
using TournamentTimer.Runner;

namespace TournamentTimer.Runner.Wpf;

public partial class MainWindow : Window
{
    private static readonly Brush CurrentSplitBackground = new SolidColorBrush(Color.FromRgb(42, 48, 64));
    private static readonly Brush CompletedSplitForeground = new SolidColorBrush(Color.FromRgb(140, 255, 179));
    private static readonly Brush CurrentSplitForeground = new SolidColorBrush(Color.FromRgb(244, 247, 255));
    private static readonly Brush PendingSplitForeground = new SolidColorBrush(Color.FromRgb(170, 179, 197));
    private static readonly Brush MutedForeground = new SolidColorBrush(Color.FromRgb(170, 179, 197));
    private static readonly Brush StatusOkForeground = new SolidColorBrush(Color.FromRgb(140, 255, 179));
    private static readonly Brush StatusBadForeground = new SolidColorBrush(Color.FromRgb(255, 159, 159));
    private static readonly Brush StatusReadyForeground = new SolidColorBrush(Color.FromRgb(159, 192, 255));
    private static readonly Brush StatusWarningForeground = new SolidColorBrush(Color.FromRgb(255, 211, 138));

    private const int GlobalHotkeyId = 9001;
    private const uint WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20h1 = 19;


    private readonly DispatcherTimer _uiTimer;
    private LocalBridgeServer? _localBridgeServer;
    private RunnerSession? _session;
    private bool _actionInProgress;
    private string _lastSyncStatus = "Idle";
    private RunnerClientSettings _settings;
    private string _lastSplitListRenderKey = "";
    private DateTimeOffset _nextServerStatePollAtUtc = DateTimeOffset.MinValue;
    private bool _serverStatePollInProgress;
    private bool _liveSplitInputDisabled;
    private string? _liveSplitInputDisabledReason;
    private HwndSource? _hotkeySource;
    private HotkeyGesture _hotkeyGesture = new(Key.F8, ModifierKeys.None);
    private bool _globalHotkeyRegistered;
    private bool _isCapturingHotkey;
    private bool _cameraRunning;
    private bool _cameraWebViewInitialized;
    private bool _cameraWheelForwardingInitialized;

    public MainWindow()
    {
        InitializeComponent();

        _settings = RunnerClientSettings.Load();

        ServerUrlTextBox.Text = GetLaunchValue(
            args: Environment.GetCommandLineArgs(),
            argName: "server",
            envName: "TOURNAMENT_TIMER_SERVER",
            fallback: _settings.ServerUrl);

        RunIdTextBox.Text = GetLaunchValue(
            args: Environment.GetCommandLineArgs(),
            argName: "runId",
            envName: "TOURNAMENT_TIMER_RUN_ID",
            fallback: _settings.RunId);

        RunnerIdTextBox.Text = GetLaunchValue(
            args: Environment.GetCommandLineArgs(),
            argName: "runnerId",
            envName: "TOURNAMENT_TIMER_RUNNER_ID",
            fallback: _settings.RunnerId);

        RunKeyPasswordBox.Password = GetLaunchValue(
            args: Environment.GetCommandLineArgs(),
            argName: "runKey",
            envName: "TOURNAMENT_TIMER_RUN_KEY",
            fallback: _settings.RunKey);

        if (TryParseHotkey(_settings.HotkeyText, out var savedHotkey, out _))
        {
            _hotkeyGesture = savedHotkey;
        }

        HotkeyTextBox.Text = _hotkeyGesture.DisplayText;
        GlobalHotkeyCheckBox.IsChecked = _settings.GlobalHotkeysEnabled;

        PreviewKeyDown += MainWindow_PreviewKeyDown;
        SourceInitialized += MainWindow_SourceInitialized;

        _uiTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };

        _uiTimer.Tick += (_, _) => UpdateUi();
        _uiTimer.Start();

        RenderSplitList(force: true);
        UpdateUi();

        _localBridgeServer = new LocalBridgeServer(
            port: 52991,
            handleEventAsync: HandleLocalBridgeEventAsync,
            log: message => Dispatcher.Invoke(() => AppendLog(message)));

        try
        {
            _ = _localBridgeServer.StartAsync();
        }
        catch (Exception ex)
        {
            var bridgeInUse =
                ex is System.Net.Sockets.SocketException socketException &&
                socketException.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse;

            if (bridgeInUse ||
                ex.Message.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog("Local bridge already in use on this PC. Manual Start/Split still works; LiveSplit bridge is available only in the first runner window.");
            }
            else
            {
                AppendLog($"Local bridge failed to start: {ex.Message}");
            }
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_actionInProgress)
        {
            return;
        }

        if (_session is not null)
        {
            AppendLog("Already connected. Use Disconnect first.");
            return;
        }

        var serverUrl = ServerUrlTextBox.Text.Trim();
        var runId = RunIdTextBox.Text.Trim();
        var runnerId = RunnerIdTextBox.Text.Trim();
        var runKey = RunKeyPasswordBox.Password.Trim();

        if (string.IsNullOrWhiteSpace(serverUrl) ||
            string.IsNullOrWhiteSpace(runId) ||
            string.IsNullOrWhiteSpace(runnerId))
        {
            AppendLog("Server URL, RunId and RunnerId are required.");
            return;
        }

        _settings = new RunnerClientSettings
        {
            ServerUrl = serverUrl,
            RunId = runId,
            RunnerId = runnerId,
            RunKey = runKey,
            HotkeyText = _hotkeyGesture.DisplayText,
            GlobalHotkeysEnabled = GlobalHotkeyCheckBox.IsChecked == true
        };

        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            AppendLog($"SETTINGS SAVE FAILED: {ex.Message}");
        }

        _actionInProgress = true;
        _lastSyncStatus = "Connecting...";
        UpdateUi();

        try
        {
            await DisconnectCurrentSessionAsync(logMessage: false);

            AppendLog($"Connecting {runnerId} to {runId}...");

            var options = new RunnerSessionOptions
            {
                ServerUrl = serverUrl,
                RunId = runId,
                ConfigPath = Path.GetFullPath(Path.Combine("configs", "local-test-run.json")),
                RunnerId = runnerId,
                RunKey = runKey,
                LogPath = null,
                ExplicitLogPath = false,
                RunnerClientId = Environment.MachineName + "-" + Guid.NewGuid().ToString("N")[..8]
            };

            var result = await RunnerSession.CreateAsync(options);

            foreach (var message in result.Messages)
            {
                AppendLog(message);
            }

            if (!result.Success)
            {
                var errorText = string.Join(
                    Environment.NewLine,
                    result.Errors.Where(error => !string.IsNullOrWhiteSpace(error)));

                if (string.IsNullOrWhiteSpace(errorText))
                {
                    errorText = "Connect failed.";
                }

                AppendLog(errorText);
                _lastSyncStatus = "Connect failed";
                return;
            }

            var session = result.Session!;

            _session = session;
            _liveSplitInputDisabled = false;
            _liveSplitInputDisabledReason = null;
            _lastSplitListRenderKey = "";
            _nextServerStatePollAtUtc = DateTimeOffset.MinValue;
            _lastSyncStatus = session.AdminControlMode
                ? "Connected - admin control"
                : "Connected";
            AppendLog("Connected.");

            if (session.AdminControlMode)
            {
                AppendLog("Admin control is active. Runner input is disabled until a new attempt or future admin release.");
            }
        }
        catch (Exception ex)
        {
            _lastSyncStatus = "Connect error";
            AppendLog($"ERROR: {ex.Message}");
        }
        finally
        {
            _actionInProgress = false;
            UpdateUi();
        }
    }

    private async void CameraButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cameraRunning)
        {
            StopCamera();
            return;
        }

        await StartCameraAsync();
    }

    private async Task StartCameraAsync()
    {
        if (_session is null)
        {
            CameraStatusTextBlock.Text = "Connect to server first.";
            AppendLog("Camera start skipped: runner is not connected to the server.");
            return;
        }

        var serverUrl = _session.ServerUrl.Trim().TrimEnd('/');
        var runId = _session.Config.RunId;
        var runnerId = _session.RunnerId;
        var attemptId = _session.AttemptId;
        var runKey = RunKeyPasswordBox.Password.Trim();

        try
        {
            CameraButton.IsEnabled = false;
            CameraStatusTextBlock.Text = "Starting camera...";

            await CameraWebView.EnsureCoreWebView2Async();

            if (!_cameraWebViewInitialized)
            {
                CameraWebView.CoreWebView2.PermissionRequested += CameraWebView_PermissionRequested;
                CameraWebView.CoreWebView2.WebMessageReceived += CameraWebView_WebMessageReceived;
                CameraWebView.CoreWebView2.NavigationCompleted += CameraWebView_NavigationCompleted;
                _cameraWebViewInitialized = true;
            }

            await EnsureCameraWheelForwardingAsync();

            if (CameraWebView.CoreWebView2 is not null)
            {
                CameraWebView.CoreWebView2.Navigate("about:blank");
                await Task.Delay(150);
            }

            var restartToken = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var url =
                $"{serverUrl}/runner-camera.html?runId={Uri.EscapeDataString(runId)}&runnerId={Uri.EscapeDataString(runnerId)}&attemptId={Uri.EscapeDataString(attemptId)}&restart={restartToken}" +
                $"#runKey={Uri.EscapeDataString(runKey)}";

            CameraWebView.Visibility = Visibility.Visible;
            CameraWebView.Source = new Uri(url);

            _cameraRunning = true;
            CameraButton.Content = "Stop camera";
            CameraStatusTextBlock.Text = "Loaded";
            AppendLog("Camera page opened.");
        }
        catch (Exception ex)
        {
            _cameraRunning = false;
            CameraButton.Content = "Start camera";
            CameraStatusTextBlock.Text = "Camera failed.";
            AppendLog($"CAMERA ERROR: {ex.Message}");
        }
        finally
        {
            CameraButton.IsEnabled = true;
            UpdateUi();
        }
    }

    private void StopCamera()
    {
        if (!_cameraRunning)
        {
            return;
        }

        try
        {
            if (CameraWebView.CoreWebView2 is not null)
            {
                CameraWebView.CoreWebView2.Navigate("about:blank");
            }
        }
        catch
        {
            // Camera shutdown is best effort.
        }

        _cameraRunning = false;
        CameraWebView.Visibility = Visibility.Collapsed;
        CameraButton.Content = "Start camera";
        CameraStatusTextBlock.Text = "Camera stopped";
        AppendLog("Camera stopped.");
        UpdateUi();
    }

    private void CameraWebView_PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = e.PermissionKind == CoreWebView2PermissionKind.Camera
            ? CoreWebView2PermissionState.Allow
            : CoreWebView2PermissionState.Deny;
    }

    private async Task EnsureCameraWheelForwardingAsync()
    {
        if (_cameraWheelForwardingInitialized || CameraWebView.CoreWebView2 is null)
        {
            return;
        }

        await CameraWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
            @"(() => {
  if (window.__tournamentTimerWheelForwarderInstalled) {
    return;
  }

  window.__tournamentTimerWheelForwarderInstalled = true;

  window.addEventListener('wheel', event => {
    try {
      chrome.webview.postMessage({
        type: 'cameraWheel',
        deltaY: event.deltaY || 0
      });
    } catch {
    }
  }, { passive: true });
})();");

        _cameraWheelForwardingInitialized = true;
    }

    private void CameraWebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            return;
        }

        _cameraRunning = false;
        CameraButton.Content = "Start camera";
        CameraStatusTextBlock.Text = "Server unavailable. Ask admin to start it.";
        CameraWebView.Visibility = Visibility.Collapsed;
        AppendLog($"CAMERA PAGE LOAD FAILED: {e.WebErrorStatus}");
        UpdateUi();
    }

    private void CameraWebView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;

            if (!root.TryGetProperty("type", out var typeElement))
            {
                return;
            }

            var messageType = typeElement.GetString();

            if (messageType == "cameraWheel")
            {
                var deltaY = root.TryGetProperty("deltaY", out var deltaYElement) &&
                             deltaYElement.ValueKind == JsonValueKind.Number
                    ? deltaYElement.GetDouble()
                    : 0;

                ScrollPageFromCameraWheel(deltaY);
                return;
            }

            if (messageType != "cameraStatus")
            {
                return;
            }

            var state = root.TryGetProperty("state", out var stateElement)
                ? stateElement.GetString() ?? "unknown"
                : "unknown";

            var message = root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString() ?? ""
                : "";

            var viewers = root.TryGetProperty("viewers", out var viewersElement) &&
                          viewersElement.ValueKind == JsonValueKind.Number
                ? viewersElement.GetInt32()
                : 0;

            CameraStatusTextBlock.Text = state switch
            {
                "online" => $"Online · {viewers} viewer{(viewers == 1 ? "" : "s")}",
                "starting" => "Starting...",
                "stopped" => "Stopped",
                "error" => FormatCameraErrorForRunnerUi(message),
                _ => string.IsNullOrWhiteSpace(message) ? state : message
            };

            if (state == "online" || state == "starting")
            {
                _cameraRunning = true;
                CameraButton.Content = "Stop camera";
            }
            else if (state == "stopped" || state == "error")
            {
                _cameraRunning = false;
                CameraButton.Content = "Start camera";
            }
        }
        catch
        {
            // Ignore malformed optional messages from the camera page.
        }
    }

    private void ScrollPageFromCameraWheel(double deltaY)
    {
        if (Math.Abs(deltaY) < 0.1)
        {
            return;
        }

        var nextOffset = PageScrollViewer.VerticalOffset + deltaY;
        nextOffset = Math.Max(0, Math.Min(PageScrollViewer.ScrollableHeight, nextOffset));

        PageScrollViewer.ScrollToVerticalOffset(nextOffset);
    }

    private static string FormatCameraErrorForRunnerUi(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Camera error";
        }

        if (message.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("notallowed", StringComparison.OrdinalIgnoreCase))
        {
            return "Camera permission blocked. Check Windows privacy settings.";
        }

        return message;
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        await RunStartOrSplitAsync("Button");
    }

    private void ChangeHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _isCapturingHotkey = true;
        HotkeyTextBox.Text = "Press key...";
        _lastSyncStatus = "Press new hotkey";
        Focus();
        UpdateUi();
    }

    private void GlobalHotkeyCheckBox_Click(object sender, RoutedEventArgs e)
    {
        SaveHotkeySettings(logSuccess: true);
    }

    private async Task RunStartOrSplitAsync(string source)
    {
        if (_session is null)
        {
            AppendLog($"{source}: runner is not connected.");
            return;
        }

        if (_session.State.Status == RunStatus.Ready)
        {
            await RunActionAsync("Start", session => session.StartAsync());
            return;
        }

        if (_session.State.Status == RunStatus.Running)
        {
            await RunActionAsync("Split", session => session.SplitAsync());
            return;
        }

        AppendLog($"{source}: ignored because run status is {_session.State.Status}.");
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_actionInProgress)
        {
            return;
        }

        await DisconnectCurrentSessionAsync(logMessage: true);
        UpdateUi();
    }

    private async Task RunActionAsync(
        string actionName,
        Func<RunnerSession, Task<RunnerSessionActionResult>> action)
    {
        if (_session is null || _actionInProgress)
        {
            return;
        }

        if (_session.AdminControlMode)
        {
            _lastSyncStatus = "Admin control: runner input disabled";
            AppendLog("Runner input ignored: admin control mode is active for this attempt.");
            UpdateUi();
            return;
        }

        if (_liveSplitInputDisabled)
        {
            _lastSyncStatus = "LiveSplit desync: admin required";
            AppendLog("Runner input ignored: LiveSplit desync was detected. Admin correction is required.");
            UpdateUi();
            return;
        }

        _actionInProgress = true;
        _lastSyncStatus = $"{actionName}: sending...";
        UpdateUi();

        try
        {
            var result = await action(_session);
            PrintActionResult(result);

            if (!result.LocalAccepted)
            {
                _lastSyncStatus = $"{actionName}: local rejected";
                return;
            }

            var serverResponse = result.ServerResponse;

            if (serverResponse is null)
            {
                _lastSyncStatus = $"{actionName}: local only";
                return;
            }

            if (!serverResponse.Sent)
            {
                _lastSyncStatus = $"{actionName}: sync failed";
                return;
            }

            if (serverResponse.Accepted)
            {
                _lastSyncStatus = serverResponse.AlreadyProcessed
                    ? $"{actionName}: already processed"
                    : $"{actionName}: synced";
                return;
            }

            if (IsWrongAttempt(serverResponse.RejectReason))
            {
                await ForceDisconnectForAttemptChangeAsync($"{actionName}: server rejected wrong attempt");
                return;
            }

            _lastSyncStatus = $"{actionName}: server rejected";
        }
        catch (Exception ex)
        {
            _lastSyncStatus = $"{actionName}: error";
            AppendLog($"ERROR: {ex.Message}");
        }
        finally
        {
            _actionInProgress = false;
            UpdateUi();
        }
    }


    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        EnableDarkWindowChrome();

        _hotkeySource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _hotkeySource?.AddHook(WndProc);
        UpdateGlobalHotkeyRegistration();
    }

    private void ChildScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        var canScrollUp = e.Delta > 0 && scrollViewer.VerticalOffset > 0;
        var canScrollDown = e.Delta < 0 && scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight;

        if (canScrollUp || canScrollDown)
        {
            return;
        }

        e.Handled = true;

        var nextOffset = PageScrollViewer.VerticalOffset - e.Delta;
        nextOffset = Math.Max(0, Math.Min(PageScrollViewer.ScrollableHeight, nextOffset));

        PageScrollViewer.ScrollToVerticalOffset(nextOffset);
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat)
        {
            return;
        }

        if (_isCapturingHotkey)
        {
            e.Handled = true;

            if (!TryCreateGestureFromKeyEvent(e, out var gesture, out var error))
            {
                AppendLog($"HOTKEY ERROR: {error}");
                HotkeyTextBox.Text = _hotkeyGesture.DisplayText;
                _isCapturingHotkey = false;
                return;
            }

            _isCapturingHotkey = false;
            SetHotkey(gesture, logSuccess: true);
            return;
        }

        if (Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        if (!IsHotkeyMatch(e, _hotkeyGesture))
        {
            return;
        }

        e.Handled = true;
        await RunStartOrSplitAsync("Hotkey");
    }

    private void SetHotkey(HotkeyGesture gesture, bool logSuccess)
    {
        _hotkeyGesture = gesture;
        HotkeyTextBox.Text = gesture.DisplayText;
        SaveHotkeySettings(logSuccess);
    }

    private void SaveHotkeySettings(bool logSuccess)
    {
        _settings = new RunnerClientSettings
        {
            ServerUrl = ServerUrlTextBox.Text.Trim(),
            RunId = RunIdTextBox.Text.Trim(),
            RunnerId = RunnerIdTextBox.Text.Trim(),
            RunKey = RunKeyPasswordBox.Password.Trim(),
            HotkeyText = _hotkeyGesture.DisplayText,
            GlobalHotkeysEnabled = GlobalHotkeyCheckBox.IsChecked == true
        };

        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            AppendLog($"SETTINGS SAVE FAILED: {ex.Message}");
        }

        UpdateGlobalHotkeyRegistration();

        if (logSuccess)
        {
            AppendLog(_settings.GlobalHotkeysEnabled
                ? $"Hotkey saved: {_hotkeyGesture.DisplayText} (global enabled)."
                : $"Hotkey saved: {_hotkeyGesture.DisplayText} (window focus only).");
        }

        UpdateUi();
    }

    private void UpdateGlobalHotkeyRegistration()
    {
        UnregisterGlobalHotkey();

        if (_settings.GlobalHotkeysEnabled == false)
        {
            return;
        }

        if (_hotkeySource is null)
        {
            return;
        }

        var modifiers = GetWin32Modifiers(_hotkeyGesture.Modifiers) | ModNoRepeat;
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(_hotkeyGesture.Key);

        if (virtualKey == 0)
        {
            AppendLog("Global hotkey registration failed: unsupported key.");
            return;
        }

        var ok = RegisterHotKey(_hotkeySource.Handle, GlobalHotkeyId, modifiers, virtualKey);

        if (!ok)
        {
            AppendLog("Global hotkey registration failed. Key may be already used by another app.");
            _globalHotkeyRegistered = false;
            return;
        }

        _globalHotkeyRegistered = true;
    }

    private void UnregisterGlobalHotkey()
    {
        if (!_globalHotkeyRegistered || _hotkeySource is null)
        {
            return;
        }

        UnregisterHotKey(_hotkeySource.Handle, GlobalHotkeyId);
        _globalHotkeyRegistered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == GlobalHotkeyId)
        {
            handled = true;
            _ = RunStartOrSplitAsync("Global hotkey");
        }

        return IntPtr.Zero;
    }

    private static bool TryCreateGestureFromKeyEvent(KeyEventArgs e, out HotkeyGesture gesture, out string? error)
    {
        gesture = new HotkeyGesture(Key.None, ModifierKeys.None);
        error = null;

        var key = GetRealKey(e);

        if (IsModifierKey(key))
        {
            error = "Modifier-only hotkey is not allowed.";
            return false;
        }

        if (key == Key.None || key == Key.System || key == Key.DeadCharProcessed)
        {
            error = "Unsupported key.";
            return false;
        }

        gesture = new HotkeyGesture(key, Keyboard.Modifiers);
        return true;
    }

    private static bool TryParseHotkey(string value, out HotkeyGesture gesture, out string? error)
    {
        gesture = new HotkeyGesture(Key.F8, ModifierKeys.None);
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var parts = value
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        if (parts.Length == 0)
        {
            return true;
        }

        var modifiers = ModifierKeys.None;

        for (var index = 0; index < parts.Length - 1; index++)
        {
            var part = parts[index];

            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Control;
            }
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Alt;
            }
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Shift;
            }
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Windows;
            }
            else
            {
                error = $"Unknown modifier: {part}";
                return false;
            }
        }

        if (!TryParseKeyName(parts[^1], out var key))
        {
            error = $"Unknown key: {parts[^1]}";
            return false;
        }

        gesture = new HotkeyGesture(key, modifiers);
        return true;
    }

    private static bool TryParseKeyName(string value, out Key key)
    {
        var normalized = value.Trim();

        var aliases = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase)
        {
            ["Esc"] = Key.Escape,
            ["Enter"] = Key.Return,
            ["Return"] = Key.Return,
            ["Space"] = Key.Space,
            ["End"] = Key.End,
            ["Home"] = Key.Home,
            ["PgUp"] = Key.PageUp,
            ["PageUp"] = Key.PageUp,
            ["PgDn"] = Key.PageDown,
            ["PageDown"] = Key.PageDown,
            ["Ins"] = Key.Insert,
            ["Insert"] = Key.Insert,
            ["Del"] = Key.Delete,
            ["Delete"] = Key.Delete,

            ["Num0"] = Key.NumPad0,
            ["Num1"] = Key.NumPad1,
            ["Num2"] = Key.NumPad2,
            ["Num3"] = Key.NumPad3,
            ["Num4"] = Key.NumPad4,
            ["Num5"] = Key.NumPad5,
            ["Num6"] = Key.NumPad6,
            ["Num7"] = Key.NumPad7,
            ["Num8"] = Key.NumPad8,
            ["Num9"] = Key.NumPad9,
            ["NumPad0"] = Key.NumPad0,
            ["NumPad1"] = Key.NumPad1,
            ["NumPad2"] = Key.NumPad2,
            ["NumPad3"] = Key.NumPad3,
            ["NumPad4"] = Key.NumPad4,
            ["NumPad5"] = Key.NumPad5,
            ["NumPad6"] = Key.NumPad6,
            ["NumPad7"] = Key.NumPad7,
            ["NumPad8"] = Key.NumPad8,
            ["NumPad9"] = Key.NumPad9,

            ["Slash"] = Key.Oem2,
            ["/"] = Key.Oem2,
            ["Backslash"] = Key.Oem5,
            ["\\"] = Key.Oem5,
            ["Backtick"] = Key.Oem3,
            ["Tilde"] = Key.Oem3,
            ["`"] = Key.Oem3,
            ["~"] = Key.Oem3,
            ["Minus"] = Key.OemMinus,
            ["-"] = Key.OemMinus,
            ["Equals"] = Key.OemPlus,
            ["="] = Key.OemPlus,
            ["Comma"] = Key.OemComma,
            [","] = Key.OemComma,
            ["Period"] = Key.OemPeriod,
            ["."] = Key.OemPeriod,
            ["Semicolon"] = Key.Oem1,
            ["Quote"] = Key.Oem7,
            ["Apostrophe"] = Key.Oem7,
            ["LeftBracket"] = Key.Oem4,
            ["["] = Key.Oem4,
            ["RightBracket"] = Key.Oem6,
            ["]"] = Key.Oem6,

            ["NumSlash"] = Key.Divide,
            ["NumDivide"] = Key.Divide,
            ["Divide"] = Key.Divide,
            ["NumMultiply"] = Key.Multiply,
            ["Multiply"] = Key.Multiply,
            ["NumMinus"] = Key.Subtract,
            ["Subtract"] = Key.Subtract,
            ["NumPlus"] = Key.Add,
            ["Add"] = Key.Add,
            ["NumDecimal"] = Key.Decimal,
            ["Decimal"] = Key.Decimal
        };

        if (aliases.TryGetValue(normalized, out key))
        {
            return true;
        }

        return Enum.TryParse(normalized, ignoreCase: true, out key) &&
               key != Key.None &&
               key != Key.System &&
               key != Key.DeadCharProcessed;
    }

    private static Key GetRealKey(KeyEventArgs e)
    {
        return e.Key == Key.System
            ? e.SystemKey
            : e.Key == Key.ImeProcessed
                ? e.ImeProcessedKey
                : e.Key;
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin;
    }

    private static bool IsHotkeyMatch(KeyEventArgs e, HotkeyGesture gesture)
    {
        return GetRealKey(e) == gesture.Key &&
               Keyboard.Modifiers == gesture.Modifiers;
    }

    private static uint GetWin32Modifiers(ModifierKeys modifiers)
    {
        uint result = 0;

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            result |= ModAlt;
        }

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            result |= ModControl;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            result |= ModShift;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            result |= ModWin;
        }

        return result;
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
            // Dark title bar is cosmetic. Ignore unsupported Windows versions.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    private static bool IsWrongAttempt(string? reason)
    {
        return string.Equals(reason, "wrong_attempt", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(reason, "server_rejected_wrong_attempt", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ForceDisconnectForAttemptChangeAsync(string source)
    {
        var oldAttemptId = _session?.AttemptId;

        await DisconnectCurrentSessionAsync(logMessage: false);

        _lastSyncStatus = "Server attempt changed - reconnect";

        AppendLog(
            string.IsNullOrWhiteSpace(oldAttemptId)
                ? $"SERVER ATTEMPT CHANGED ({source}). Runner disconnected for safety. Press Connect to join the new attempt."
                : $"SERVER ATTEMPT CHANGED ({source}). Old attempt: {oldAttemptId}. Runner disconnected for safety. Press Connect to join the new attempt.");

        UpdateUi();
    }

    private async Task DisconnectCurrentSessionAsync(bool logMessage)
    {
        if (_session is null)
        {
            return;
        }

        StopCamera();

        await _session.StopAsync();
        _session = null;
        _liveSplitInputDisabled = false;
        _liveSplitInputDisabledReason = null;
        _lastSplitListRenderKey = "";
        _nextServerStatePollAtUtc = DateTimeOffset.MinValue;
        _lastSyncStatus = "Disconnected";

        if (logMessage)
        {
            AppendLog("Disconnected.");
        }
    }

    private void PrintActionResult(RunnerSessionActionResult result)
    {
        if (!result.LocalAccepted)
        {
            AppendLog($"REJECTED LOCAL: {result.LocalRejectReason}");
            return;
        }

        AppendLog($"ACCEPTED LOCAL: {result.EventName} at {FormatMs(result.ClientElapsedMs)}");

        var serverResponse = result.ServerResponse;

        if (serverResponse is null)
        {
            return;
        }

        if (!serverResponse.Sent)
        {
            AppendLog($"SERVER SYNC FAILED: {serverResponse.TransportError}");
            return;
        }

        if (serverResponse.Accepted)
        {
            var label = serverResponse.AlreadyProcessed
                ? "ALREADY PROCESSED SERVER"
                : "ACCEPTED SERVER";

            AppendLog($"{label}: status={serverResponse.Status}, finished={FormatNullableMs(serverResponse.FinishedAtMs)}");
            return;
        }

        AppendLog($"REJECTED SERVER: {serverResponse.RejectReason}");
        AppendLog("FATAL: local/server state mismatch.");
    }

    private async Task<LocalBridgeEventResponse> HandleLocalBridgeEventAsync(LocalBridgeEventRequest request)
    {
        var task = await Dispatcher.InvokeAsync(() => HandleLocalBridgeEventOnUiThreadAsync(request));
        return await task;
    }

    private async Task<LocalBridgeEventResponse> HandleLocalBridgeEventOnUiThreadAsync(LocalBridgeEventRequest request)
    {
        var eventType = request.EventType.Trim().ToLowerInvariant();

        if (eventType == "reset")
        {
            if (_session is not null && _session.State.Status == RunStatus.Running)
            {
                return await DisableLiveSplitInputAsync(
                    reason: "livesplit_reset_during_running",
                    sourceEventId: request.SourceEventId);
            }

            AppendLog("LiveSplit reset detected. Server attempt was not reset. Use Admin > New attempt for a fresh race.");
            _lastSyncStatus = "LiveSplit reset ignored";
            UpdateUi();

            return BuildBridgeResponse(
                accepted: true,
                alreadyProcessed: false,
                message: "reset_logged_no_server_action");
        }

        if (_session is null)
        {
            return BuildBridgeResponse(
                accepted: false,
                alreadyProcessed: false,
                message: "runner_not_connected");
        }

        if (_actionInProgress)
        {
            return BuildBridgeResponse(
                accepted: false,
                alreadyProcessed: false,
                message: "runner_action_in_progress");
        }

        if (_session.AdminControlMode)
        {
            _lastSyncStatus = "LiveSplit ignored: admin control";
            AppendLog("LiveSplit input ignored: admin control mode is active for this attempt.");
            UpdateUi();

            return BuildBridgeResponse(
                accepted: false,
                alreadyProcessed: false,
                message: "admin_control_mode");
        }

        if (_liveSplitInputDisabled)
        {
            _lastSyncStatus = "LiveSplit input disabled";
            UpdateUi();

            return BuildBridgeResponse(
                accepted: false,
                alreadyProcessed: false,
                message: "livesplit_input_disabled");
        }

        if (eventType == "time")
        {
            if (_session.State.Status != RunStatus.Running)
            {
                return BuildBridgeResponse(
                    accepted: true,
                    alreadyProcessed: true,
                    message: $"time_ignored_state_{_session.State.Status}");
            }

            if (!TryBuildLiveSplitDisplayUpdate(request, out var displayUpdate, out var displayError))
            {
                return BuildBridgeResponse(
                    accepted: false,
                    alreadyProcessed: false,
                    message: displayError);
            }

            return await RunBridgeDisplayTimeAsync(displayUpdate);
        }

        if (eventType == "start")
        {
            if (_session.State.Status != RunStatus.Ready)
            {
                return BuildBridgeResponse(
                    accepted: true,
                    alreadyProcessed: true,
                    message: $"start_ignored_state_{_session.State.Status}");
            }

            if (!TryBuildLiveSplitTiming(request, forceZeroElapsed: true, out var startTiming, out var startTimingError))
            {
                return await DisableLiveSplitInputAsync(
                    reason: startTimingError,
                    sourceEventId: request.SourceEventId);
            }

            return await RunBridgeActionAsync(
                actionName: "LiveSplit Start",
                action: session => session.StartFromExternalTimingAsync(startTiming));
        }

        if (eventType == "split")
        {
            if (request.SplitIndex is null)
            {
                return await DisableLiveSplitInputAsync(
                    reason: "missing_split_index",
                    sourceEventId: request.SourceEventId);
            }

            var expectedSplitIndex = _session.State.LastCompletedSplitIndex + 1;

            if (request.SplitIndex.Value < expectedSplitIndex)
            {
                return BuildBridgeResponse(
                    accepted: true,
                    alreadyProcessed: true,
                    message: $"split_already_completed_{request.SplitIndex.Value}");
            }

            if (request.SplitIndex.Value != expectedSplitIndex)
            {
                return await DisableLiveSplitInputAsync(
                    reason: $"split_index_mismatch_expected_{expectedSplitIndex}_got_{request.SplitIndex.Value}",
                    sourceEventId: request.SourceEventId);
            }

            if (!TryBuildLiveSplitTiming(request, forceZeroElapsed: false, out var splitTiming, out var splitTimingError))
            {
                return await DisableLiveSplitInputAsync(
                    reason: splitTimingError,
                    sourceEventId: request.SourceEventId);
            }

            var previousOfficialElapsedMs = GetLastAcceptedOfficialElapsedMs(_session.State);

            if (splitTiming.OfficialElapsedMs < previousOfficialElapsedMs)
            {
                return await DisableLiveSplitInputAsync(
                    reason: $"livesplit_time_went_back_previous_{previousOfficialElapsedMs}_got_{splitTiming.OfficialElapsedMs}",
                    sourceEventId: request.SourceEventId);
            }

            return await RunBridgeActionAsync(
                actionName: "LiveSplit Split",
                action: session => session.SplitFromExternalTimingAsync(request.SplitIndex.Value, splitTiming));
        }

        return BuildBridgeResponse(
            accepted: false,
            alreadyProcessed: false,
            message: $"unknown_event_type_{eventType}");
    }


    private static bool TryBuildLiveSplitDisplayUpdate(
        LocalBridgeEventRequest request,
        out RunnerLiveDisplayUpdate update,
        out string reason)
    {
        update = null!;
        reason = "";

        var gameTimeMs = NormalizeElapsedMs(request.LiveSplitGameTimeMs);
        var realTimeMs = NormalizeElapsedMs(request.LiveSplitRealTimeMs);

        if (gameTimeMs is null)
        {
            reason = "missing_livesplit_game_time";
            return false;
        }

        update = new RunnerLiveDisplayUpdate
        {
            DisplayElapsedMs = gameTimeMs.Value,
            TimingSource = RunTimingSource.LiveSplitGameTime,
            LiveSplitRealTimeMs = realTimeMs,
            LiveSplitGameTimeMs = gameTimeMs,
            GameTimeRunning = request.GameTimeRunning ?? true,
            SourceEventId = request.SourceEventId,
            SourceOccurredAtUtc = request.OccurredAtUtc
        };

        return true;
    }

    private static bool TryBuildLiveSplitTiming(
        LocalBridgeEventRequest request,
        bool forceZeroElapsed,
        out RunnerExternalTiming timing,
        out string reason)
    {
        timing = null!;
        reason = "";

        var gameTimeMs = NormalizeElapsedMs(request.LiveSplitGameTimeMs);
        var realTimeMs = NormalizeElapsedMs(request.LiveSplitRealTimeMs);
        var preferredTimeMs = NormalizeElapsedMs(request.LiveSplitTimeMs);

        // Backward compatibility: early bridge builds sent only liveSplitTimeMs,
        // which was LiveSplit real time. New LRT runs must provide explicit game time.
        if (realTimeMs is null && gameTimeMs is null && preferredTimeMs is not null)
        {
            realTimeMs = preferredTimeMs;
        }

        RunTimingSource timingSource;
        long officialElapsedMs;

        if (forceZeroElapsed)
        {
            officialElapsedMs = 0;
            timingSource = gameTimeMs is not null
                ? RunTimingSource.LiveSplitGameTime
                : RunTimingSource.LiveSplitRealTime;

            // Start is always official zero. Do not persist stale LiveSplit game time
            // that can remain in SourceSplit/ASL metadata before the real timer start.
            realTimeMs = realTimeMs is null ? null : 0;
            gameTimeMs = gameTimeMs is null ? null : 0;
        }
        else if (gameTimeMs is not null)
        {
            var comparisonTimeMs = realTimeMs ?? preferredTimeMs;

            if (gameTimeMs.Value <= 0 && comparisonTimeMs is not null && comparisonTimeMs.Value > 1000)
            {
                reason = $"invalid_livesplit_game_time_{gameTimeMs.Value}_with_real_time_{comparisonTimeMs.Value}";
                return false;
            }

            officialElapsedMs = gameTimeMs.Value;
            timingSource = RunTimingSource.LiveSplitGameTime;
        }
        else
        {
            reason = "missing_livesplit_game_time";
            return false;
        }

        timing = new RunnerExternalTiming
        {
            OfficialElapsedMs = officialElapsedMs,
            TimingSource = timingSource,
            LiveSplitRealTimeMs = realTimeMs,
            LiveSplitGameTimeMs = gameTimeMs,
            SourceEventId = request.SourceEventId,
            SourceOccurredAtUtc = request.OccurredAtUtc
        };

        return true;
    }

    private static long GetLastAcceptedOfficialElapsedMs(RunState state)
    {
        return state.Events.Count == 0
            ? 0
            : state.Events[^1].ClientElapsedMs;
    }

    private static long? NormalizeElapsedMs(long? elapsedMs)
    {
        return elapsedMs is null || elapsedMs.Value < 0
            ? null
            : elapsedMs.Value;
    }

    private async Task<LocalBridgeEventResponse> DisableLiveSplitInputAsync(
        string reason,
        string? sourceEventId)
    {
        _liveSplitInputDisabled = true;
        _liveSplitInputDisabledReason = reason;
        _lastSyncStatus = "LiveSplit desync - admin required";

        AppendLog($"LIVE SPLIT INPUT DISABLED: {reason}. Admin correction is required.");

        if (_session is not null)
        {
            var response = await _session.ReportInputDesyncAsync(reason, sourceEventId);

            if (!response.Sent)
            {
                AppendLog($"SERVER INPUT LOCK FAILED: {response.TransportError}");
            }
            else if (response.Accepted)
            {
                _liveSplitInputDisabled = false;
                _liveSplitInputDisabledReason = null;
                _lastSplitListRenderKey = "";
                _lastSyncStatus = "Admin control enabled";
                AppendLog($"SERVER ADMIN CONTROL ENABLED: status={_session.State.Status}, lastSplit={_session.State.LastCompletedSplitIndex}.");
            }
            else
            {
                AppendLog($"SERVER INPUT LOCK REJECTED: {response.RejectReason}");
            }
        }

        UpdateUi();

        return BuildBridgeResponse(
            accepted: false,
            alreadyProcessed: false,
            message: $"livesplit_input_disabled_{reason}");
    }

    private async Task<LocalBridgeEventResponse> RunBridgeDisplayTimeAsync(RunnerLiveDisplayUpdate update)
    {
        if (_session is null)
        {
            return BuildBridgeResponse(
                accepted: false,
                alreadyProcessed: false,
                message: "runner_not_connected");
        }

        try
        {
            var result = await _session.ApplyLiveDisplayUpdateAsync(update);

            if (result.ServerResponse is not null &&
                result.ServerResponse.Sent &&
                IsWrongAttempt(result.ServerResponse.RejectReason))
            {
                await ForceDisconnectForAttemptChangeAsync("LiveSplit display update rejected wrong attempt");

                return BuildBridgeResponse(
                    accepted: false,
                    alreadyProcessed: false,
                    message: "server_attempt_changed_reconnect");
            }

            return BuildBridgeResponse(
                accepted: result.Accepted,
                alreadyProcessed: false,
                message: result.Message);
        }
        catch (Exception ex)
        {
            return BuildBridgeResponse(
                accepted: false,
                alreadyProcessed: false,
                message: $"display_time_error_{ex.Message}");
        }
    }

    private async Task<LocalBridgeEventResponse> RunBridgeActionAsync(
        string actionName,
        Func<RunnerSession, Task<RunnerSessionActionResult>> action)
    {
        if (_session is null)
        {
            return BuildBridgeResponse(
                accepted: false,
                alreadyProcessed: false,
                message: "runner_not_connected");
        }

        _actionInProgress = true;
        _lastSyncStatus = $"{actionName}: sending...";
        UpdateUi();

        try
        {
            var result = await action(_session);
            PrintActionResult(result);

            if (!result.LocalAccepted)
            {
                _lastSyncStatus = $"{actionName}: local rejected";

                return BuildBridgeResponse(
                    accepted: false,
                    alreadyProcessed: false,
                    message: result.LocalRejectReason ?? "local_rejected");
            }

            var serverResponse = result.ServerResponse;

            if (serverResponse is null)
            {
                _lastSyncStatus = $"{actionName}: local only";

                return BuildBridgeResponse(
                    accepted: true,
                    alreadyProcessed: false,
                    message: "local_only");
            }

            if (!serverResponse.Sent)
            {
                _lastSyncStatus = $"{actionName}: sync failed";

                return BuildBridgeResponse(
                    accepted: true,
                    alreadyProcessed: false,
                    message: $"sync_failed_{serverResponse.TransportError}");
            }

            if (serverResponse.Accepted)
            {
                _lastSyncStatus = serverResponse.AlreadyProcessed
                    ? $"{actionName}: already processed"
                    : $"{actionName}: synced";

                return BuildBridgeResponse(
                    accepted: true,
                    alreadyProcessed: serverResponse.AlreadyProcessed,
                    message: serverResponse.AlreadyProcessed ? "already_processed" : "synced");
            }

            if (IsWrongAttempt(serverResponse.RejectReason))
            {
                await ForceDisconnectForAttemptChangeAsync($"{actionName}: server rejected wrong attempt");

                return BuildBridgeResponse(
                    accepted: false,
                    alreadyProcessed: false,
                    message: "server_attempt_changed_reconnect");
            }

            _lastSyncStatus = $"{actionName}: server rejected";

            return BuildBridgeResponse(
                accepted: false,
                alreadyProcessed: false,
                message: $"server_rejected_{serverResponse.RejectReason}");
        }
        catch (Exception ex)
        {
            _lastSyncStatus = $"{actionName}: error";
            AppendLog($"ERROR: {ex.Message}");

            return BuildBridgeResponse(
                accepted: false,
                alreadyProcessed: false,
                message: $"error_{ex.Message}");
        }
        finally
        {
            _actionInProgress = false;
            UpdateUi();
        }
    }

    private LocalBridgeEventResponse BuildBridgeResponse(
        bool accepted,
        bool alreadyProcessed,
        string? message)
    {
        return new LocalBridgeEventResponse
        {
            Accepted = accepted,
            AlreadyProcessed = alreadyProcessed,
            Status = _session?.State.Status.ToString() ?? "Disconnected",
            LastCompletedSplitIndex = _session?.State.LastCompletedSplitIndex ?? -1,
            ClientElapsedMs = _session?.CurrentElapsedMs ?? 0,
            Message = message
        };
    }

    private void MaybePollServerState()
    {
        if (_session is null || _serverStatePollInProgress)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        if (now < _nextServerStatePollAtUtc)
        {
            return;
        }

        _nextServerStatePollAtUtc = now.AddSeconds(1);
        _serverStatePollInProgress = true;
        _ = PollServerStateAsync();
    }

    private async Task PollServerStateAsync()
    {
        try
        {
            var session = _session;

            if (session is null)
            {
                return;
            }

            var result = await session.RefreshServerStateAsync();

            if (!result.Success)
            {
                if (IsWrongAttempt(result.Message))
                {
                    await ForceDisconnectForAttemptChangeAsync("server state poll detected wrong attempt");
                }

                return;
            }

            if (!result.Applied)
            {
                return;
            }

            if (session.AdminControlMode)
            {
                _liveSplitInputDisabled = false;
                _liveSplitInputDisabledReason = null;
            }

            _lastSplitListRenderKey = "";
            _lastSyncStatus = session.AdminControlMode
                ? "Server correction applied - admin control"
                : "Server state synced";

            AppendLog(
                session.AdminControlMode
                    ? $"SERVER CORRECTION APPLIED: status={session.State.Status}, lastSplit={session.State.LastCompletedSplitIndex}. Runner input disabled."
                    : $"SERVER STATE SYNCED: status={session.State.Status}, lastSplit={session.State.LastCompletedSplitIndex}.");

            UpdateUi();
        }
        catch (Exception ex)
        {
            _lastSyncStatus = $"Server state poll failed: {ex.Message}";
        }
        finally
        {
            _serverStatePollInProgress = false;
        }
    }

    private void UpdateClientBadge()
    {
        var runnerId = _session?.RunnerId ?? RunnerIdTextBox.Text.Trim();

        ClientBadgeTextBlock.Text = string.IsNullOrWhiteSpace(runnerId)
            ? "runner client"
            : runnerId;
    }

    private void UpdateUi()
    {
        UpdateClientBadge();

        if (_session is null)
        {
            TimerTextBlock.Text = "0:00.000";
            StatusTextBlock.Text = "Disconnected";
            ApplyStatusVisual(StatusBadForeground);
            SyncStatusTextBlock.Text = _lastSyncStatus == "Idle" ? "-" : _lastSyncStatus;
            AttemptTextBlock.Text = "-";
            SplitTextBlock.Text = "Not connected";
            LogPathTextBlock.Text = "-";

            ConnectButton.Content = "Connect";
            ConnectButton.IsEnabled = !_actionInProgress;
            ActionButton.Content = "Start / Split";
            ActionButton.IsEnabled = false;
            DisconnectButton.IsEnabled = false;
            CameraButton.IsEnabled = false;
            if (!_cameraRunning)
            {
                CameraButton.Content = "Start camera";
                CameraStatusTextBlock.Text = "Connect to server first.";
            }

            RenderSplitList(force: false);
            return;
        }

        MaybePollServerState();

        TimerTextBlock.Text = FormatMs(_session.CurrentElapsedMs);
        var runnerInputLocked = _session.AdminControlMode || _liveSplitInputDisabled;

        StatusTextBlock.Text = _session.AdminControlMode
            ? $"{_session.State.Status} - Admin control"
            : _liveSplitInputDisabled
                ? $"{_session.State.Status} - LiveSplit disabled"
                : _session.State.Status.ToString();
        ApplyStatusVisual(GetStatusBrush(_session.State.Status, runnerInputLocked));
        SyncStatusTextBlock.Text = _lastSyncStatus;
        AttemptTextBlock.Text = _session.AttemptId;
        SplitTextBlock.Text = _session.AdminControlMode
            ? $"Admin control - {_session.CurrentSplitName ?? _session.State.Status.ToString()}"
            : _liveSplitInputDisabled
                ? $"Admin required - {_session.CurrentSplitName ?? _session.State.Status.ToString()}"
                : _session.CurrentSplitName ?? _session.State.Status.ToString();
        LogPathTextBlock.Text = _session.LogFilePath;

        ConnectButton.Content = "Connected";
        ConnectButton.IsEnabled = false;

        DisconnectButton.IsEnabled = !_actionInProgress;
        CameraButton.IsEnabled = !_actionInProgress;

        if (!_cameraRunning && string.Equals(CameraStatusTextBlock.Text, "Connect to server first.", StringComparison.Ordinal))
        {
            CameraStatusTextBlock.Text = "Camera stopped";
        }

        var actionAvailable =
            !_actionInProgress &&
            !runnerInputLocked &&
            (_session.State.Status == RunStatus.Ready || _session.State.Status == RunStatus.Running);

        ActionButton.Content = runnerInputLocked
            ? "Locked"
            : _session.State.Status == RunStatus.Ready
                ? "Start"
                : _session.State.Status == RunStatus.Running
                    ? "Split"
                    : "Finished";

        ActionButton.IsEnabled = actionAvailable;

        RenderSplitList(force: false);
    }


    private void ApplyStatusVisual(Brush brush)
    {
        StatusTextBlock.Foreground = brush;
        StatusDot.Fill = brush;
    }

    private static Brush GetStatusBrush(RunStatus status, bool runnerInputLocked)
    {
        if (runnerInputLocked)
        {
            return StatusWarningForeground;
        }

        return status switch
        {
            RunStatus.Ready => StatusReadyForeground,
            RunStatus.Running => StatusOkForeground,
            RunStatus.Finished => StatusOkForeground,
            _ => StatusBadForeground
        };
    }

    private void RenderSplitList(bool force)
    {
        var renderKey = _session is null
            ? "disconnected"
            : $"{_session.Config.RunId}|{_session.State.Status}|{_session.State.LastCompletedSplitIndex}|{_session.Config.Splits.Count}|{_session.ServerStateVersion}|{_session.AdminControlMode}|{_liveSplitInputDisabled}|{_session.CompletedSplits.Count}";

        if (!force && string.Equals(renderKey, _lastSplitListRenderKey, StringComparison.Ordinal))
        {
            return;
        }

        _lastSplitListRenderKey = renderKey;
        SplitListPanel.Children.Clear();

        if (_session is null)
        {
            SplitListPanel.Children.Add(new TextBlock
            {
                Text = "Connect to load splits.",
                Foreground = MutedForeground,
                FontSize = 14,
                Margin = new Thickness(4)
            });

            return;
        }

        if (_session.Config.Splits.Count == 0)
        {
            SplitListPanel.Children.Add(new TextBlock
            {
                Text = "No splits configured.",
                Foreground = MutedForeground,
                FontSize = 14,
                Margin = new Thickness(4)
            });

            return;
        }

        var lastCompletedIndex = _session.State.LastCompletedSplitIndex;
        var currentIndex = lastCompletedIndex + 1;
        var isFinished = _session.State.Status == RunStatus.Finished;

        for (var index = 0; index < _session.Config.Splits.Count; index++)
        {
            var split = _session.Config.Splits[index];

            var isCompleted = index <= lastCompletedIndex;
            var isCurrent = !isFinished && _session.State.Status == RunStatus.Running && index == currentIndex;
            _session.CompletedSplits.TryGetValue(index, out var completedSplit);

            var symbol = isCompleted
                ? "OK"
                : isCurrent
                    ? ">"
                    : " ";

            var stateText = isCompleted
                ? completedSplit?.ClientElapsed ?? "done"
                : isCurrent
                    ? "next"
                    : "pending";

            var foreground = isCompleted
                ? CompletedSplitForeground
                : isCurrent
                    ? CurrentSplitForeground
                    : PendingSplitForeground;

            var row = new Border
            {
                Background = isCurrent ? CurrentSplitBackground : Brushes.Transparent,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 0, 4)
            };

            var grid = new Grid();

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });

            var symbolText = new TextBlock
            {
                Text = symbol,
                Foreground = foreground,
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(symbolText, 0);

            var indexText = new TextBlock
            {
                Text = (index + 1).ToString(),
                Foreground = foreground,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(indexText, 1);

            var nameText = new TextBlock
            {
                Text = split.Name,
                Foreground = foreground,
                FontWeight = isCurrent ? FontWeights.Bold : FontWeights.Normal,
                FontSize = 15,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(nameText, 2);

            var stateTextBlock = new TextBlock
            {
                Text = stateText,
                Foreground = foreground,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                Opacity = 0.82,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(stateTextBlock, 3);

            grid.Children.Add(symbolText);
            grid.Children.Add(indexText);
            grid.Children.Add(nameText);
            grid.Children.Add(stateTextBlock);

            row.Child = grid;
            SplitListPanel.Children.Add(row);
        }
    }

    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";

        LogTextBox.Text = string.IsNullOrWhiteSpace(LogTextBox.Text)
            ? line
            : line + Environment.NewLine + LogTextBox.Text;
    }

    protected override async void OnClosed(EventArgs e)
    {
        _uiTimer.Stop();
        UnregisterGlobalHotkey();
        _hotkeySource?.RemoveHook(WndProc);

        if (_localBridgeServer is not null)
        {
            await _localBridgeServer.StopAsync();
        }

        await DisconnectCurrentSessionAsync(logMessage: false);

        base.OnClosed(e);
    }

    private static string GetLaunchValue(
        string[] args,
        string argName,
        string envName,
        string fallback)
    {
        var prefix = "--" + argName + "=";

        var arg = args
            .Skip(1)
            .FirstOrDefault(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        if (arg is not null)
        {
            var value = arg[prefix.Length..].Trim();

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        var envValue = Environment.GetEnvironmentVariable(envName);

        if (!string.IsNullOrWhiteSpace(envValue))
        {
            return envValue.Trim();
        }

        return fallback;
    }

    private static string FormatMs(long ms)
    {
        var time = TimeSpan.FromMilliseconds(ms);

        return time.Hours > 0
            ? time.ToString(@"h\:mm\:ss\.fff")
            : time.ToString(@"m\:ss\.fff");
    }

    private static string FormatNullableMs(long? ms)
    {
        return ms is null ? "null" : FormatMs(ms.Value);
    }
}
public sealed record HotkeyGesture(Key Key, ModifierKeys Modifiers)
{
    public string DisplayText
    {
        get
        {
            var parts = new List<string>();

            if (Modifiers.HasFlag(ModifierKeys.Control))
            {
                parts.Add("Ctrl");
            }

            if (Modifiers.HasFlag(ModifierKeys.Alt))
            {
                parts.Add("Alt");
            }

            if (Modifiers.HasFlag(ModifierKeys.Shift))
            {
                parts.Add("Shift");
            }

            if (Modifiers.HasFlag(ModifierKeys.Windows))
            {
                parts.Add("Win");
            }

            parts.Add(FriendlyKeyName(Key));

            return string.Join("+", parts);
        }
    }

    private static string FriendlyKeyName(Key key)
    {
        return key switch
        {
            Key.Oem2 => "Slash",
            Key.Oem5 => "Backslash",
            Key.Oem3 => "Backtick",
            Key.OemMinus => "Minus",
            Key.OemPlus => "Equals",
            Key.OemComma => "Comma",
            Key.OemPeriod => "Period",
            Key.Oem1 => "Semicolon",
            Key.Oem7 => "Quote",
            Key.Oem4 => "LeftBracket",
            Key.Oem6 => "RightBracket",
            Key.Divide => "NumSlash",
            Key.Multiply => "NumMultiply",
            Key.Subtract => "NumMinus",
            Key.Add => "NumPlus",
            Key.Decimal => "NumDecimal",
            _ => key.ToString()
        };
    }
}
