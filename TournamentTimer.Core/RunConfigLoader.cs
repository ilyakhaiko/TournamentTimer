using System.Text.Json;
using System.Text.Json.Serialization;

namespace TournamentTimer.Core;

public static class RunConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static RunConfig LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Run config file not found.", filePath);
        }

        var json = File.ReadAllText(filePath);

        var config = JsonSerializer.Deserialize<RunConfig>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize run config.");

        Validate(config);

        return config;
    }

    private static void Validate(RunConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.RunId))
            throw new InvalidOperationException("RunId is required.");

        if (string.IsNullOrWhiteSpace(config.Game))
            throw new InvalidOperationException("Game is required.");

        if (string.IsNullOrWhiteSpace(config.Category))
            throw new InvalidOperationException("Category is required.");

        if (config.Splits.Count == 0)
            throw new InvalidOperationException("At least one split is required.");

        for (var i = 0; i < config.Splits.Count; i++)
        {
            if (config.Splits[i].Index != i)
            {
                throw new InvalidOperationException(
                    $"Split index mismatch. Expected {i}, got {config.Splits[i].Index}.");
            }

            if (string.IsNullOrWhiteSpace(config.Splits[i].Name))
            {
                throw new InvalidOperationException($"Split {i} name is required.");
            }
        }

        if (config.MinimumMsBetweenSplits < 0)
            throw new InvalidOperationException("MinimumMsBetweenSplits cannot be negative.");
    }
}