using System.IO;
using System.Text.Json;

namespace TournamentTimer.Runner.Wpf;

public sealed record RunnerClientSettings
{
    private const string AppDirectoryName = "TournamentTimer";
    private const string SettingsFileName = "runner-settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string ServerUrl { get; init; } = "http://localhost:5177";
    public string RunId { get; init; } = "local-test-run";
    public string RunnerId { get; init; } = "runner-1";
    public string RunKey { get; init; } = "";
    public string HotkeyText { get; init; } = "F8";
    public bool GlobalHotkeysEnabled { get; init; } = false;

    public static string SettingsDirectoryPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppDirectoryName);

    public static string SettingsFilePath =>
        Path.Combine(SettingsDirectoryPath, SettingsFileName);

    public static RunnerClientSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                return new RunnerClientSettings();
            }

            var json = File.ReadAllText(SettingsFilePath);
            var settings = JsonSerializer.Deserialize<RunnerClientSettings>(json, JsonOptions);

            return settings is null
                ? new RunnerClientSettings()
                : Normalize(settings);
        }
        catch
        {
            return new RunnerClientSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectoryPath);

        var normalized = Normalize(this);
        var json = JsonSerializer.Serialize(normalized, JsonOptions);

        File.WriteAllText(SettingsFilePath, json);
    }

    private static RunnerClientSettings Normalize(RunnerClientSettings settings)
    {
        return new RunnerClientSettings
        {
            ServerUrl = string.IsNullOrWhiteSpace(settings.ServerUrl)
                ? "http://localhost:5177"
                : settings.ServerUrl.Trim(),

            RunId = string.IsNullOrWhiteSpace(settings.RunId)
                ? "local-test-run"
                : settings.RunId.Trim(),

            RunnerId = string.IsNullOrWhiteSpace(settings.RunnerId)
                ? "runner-1"
                : settings.RunnerId.Trim(),

            RunKey = string.IsNullOrWhiteSpace(settings.RunKey)
                ? ""
                : settings.RunKey.Trim(),

            HotkeyText = string.IsNullOrWhiteSpace(settings.HotkeyText)
                ? "F8"
                : settings.HotkeyText.Trim(),

            GlobalHotkeysEnabled = settings.GlobalHotkeysEnabled
        };
    }
}
