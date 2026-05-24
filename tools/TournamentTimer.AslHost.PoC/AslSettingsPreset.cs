using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

internal sealed class AslSettingsPreset
{
    private readonly Dictionary<string, PresetValue> _settings;

    private AslSettingsPreset(Dictionary<string, PresetValue> settings)
    {
        _settings = settings;
    }

    public int Count => _settings.Count;

    public static AslSettingsPreset LoadOrEmpty(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new AslSettingsPreset(new Dictionary<string, PresetValue>(StringComparer.OrdinalIgnoreCase));
        }

        path = System.IO.Path.GetFullPath(path);

        if (!System.IO.File.Exists(path))
        {
            Console.WriteLine("ASL settings preset: not found, using ASL defaults: " + path);
            return new AslSettingsPreset(new Dictionary<string, PresetValue>(StringComparer.OrdinalIgnoreCase));
        }

        var json = System.IO.File.ReadAllText(path);
        var settings = ParseSettingsObject(json);

        Console.WriteLine($"ASL settings preset: loaded {settings.Count} setting(s): {path}");
        return new AslSettingsPreset(settings);
    }

    public void ApplyTo(object aslSettings, bool verbose = false)
    {
        if (_settings.Count == 0)
        {
            Console.WriteLine("ASL settings preset: no overrides, using ASL defaults.");
            return;
        }

        if (aslSettings == null)
        {
            Console.WriteLine("ASL settings preset: ASL settings object is null, overrides were not applied.");
            return;
        }

        var orderedSettings = GetOrderedSettings(aslSettings);
        if (orderedSettings == null)
        {
            Console.WriteLine("ASL settings preset: OrderedSettings not found, overrides were not applied.");
            return;
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var applied = 0;

        foreach (var setting in orderedSettings)
        {
            if (setting == null)
            {
                continue;
            }

            var id = GetMemberValue(setting, "Id") as string;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            seenIds.Add(id);

            PresetValue presetValue;
            if (!_settings.TryGetValue(id, out presetValue))
            {
                continue;
            }

            if (TrySetSettingValue(setting, presetValue, out var appliedText, out var error))
            {
                applied++;
                if (verbose)
                {
                    Console.WriteLine($"ASL settings preset: applied {id}={appliedText}");
                }
            }
            else
            {
                Console.WriteLine($"ASL settings preset: failed to apply {id}: {error}");
            }
        }

        foreach (var id in _settings.Keys)
        {
            if (!seenIds.Contains(id))
            {
                Console.WriteLine($"ASL settings preset: warning unknown setting id={id}");
            }
        }

        Console.WriteLine($"ASL settings preset: applied {applied}/{_settings.Count} override(s).");
    }

    private static IEnumerable GetOrderedSettings(object aslSettings)
    {
        var property = aslSettings.GetType().GetProperty("OrderedSettings", BindingFlags.Instance | BindingFlags.Public);
        if (property != null)
        {
            return property.GetValue(aslSettings, null) as IEnumerable;
        }

        return aslSettings as IEnumerable;
    }

    private static object GetMemberValue(object target, string name)
    {
        var type = target.GetType();

        var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        if (property != null)
        {
            return property.GetValue(target, null);
        }

        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return field == null ? null : field.GetValue(target);
    }

    private static bool TrySetSettingValue(object setting, PresetValue presetValue, out string appliedText, out string error)
    {
        appliedText = presetValue.DisplayValue;
        error = null;

        var type = setting.GetType();
        var valueProperty = type.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
        var currentValue = valueProperty == null ? null : valueProperty.GetValue(setting, null);
        var targetType = currentValue == null
            ? (valueProperty == null ? typeof(object) : valueProperty.PropertyType)
            : currentValue.GetType();

        object converted;
        if (!presetValue.TryConvertTo(targetType, out converted, out error))
        {
            return false;
        }

        try
        {
            if (valueProperty != null && valueProperty.CanWrite)
            {
                valueProperty.SetValue(setting, converted, null);
                appliedText = Convert.ToString(converted, CultureInfo.InvariantCulture) ?? "null";
                return true;
            }

            var valueField = type.GetField("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (valueField != null)
            {
                valueField.SetValue(setting, converted);
                appliedText = Convert.ToString(converted, CultureInfo.InvariantCulture) ?? "null";
                return true;
            }

            error = "Value property/field not writable";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static Dictionary<string, PresetValue> ParseSettingsObject(string json)
    {
        var result = new Dictionary<string, PresetValue>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        var settingsMatch = Regex.Match(json, "\\\"settings\\\"\\s*:\\s*\\{", RegexOptions.IgnoreCase);
        if (!settingsMatch.Success)
        {
            return result;
        }

        var objectStart = settingsMatch.Index + settingsMatch.Length - 1;
        var objectEnd = FindMatchingBrace(json, objectStart);
        if (objectEnd <= objectStart)
        {
            return result;
        }

        var body = json.Substring(objectStart + 1, objectEnd - objectStart - 1);

        var pairRegex = new Regex(
            "\\\"(?<key>(?:\\\\.|[^\\\"])*)\\\"\\s*:\\s*(?<value>true|false|null|-?\\d+(?:\\.\\d+)?|\\\"(?:\\\\.|[^\\\"])*\\\")",
            RegexOptions.IgnoreCase);

        foreach (Match match in pairRegex.Matches(body))
        {
            var key = UnescapeJsonString(match.Groups["key"].Value);
            var valueToken = match.Groups["value"].Value.Trim();

            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            result[key] = PresetValue.Parse(valueToken);
        }

        return result;
    }

    private static int FindMatchingBrace(string text, int openBraceIndex)
    {
        var depth = 0;
        var inString = false;
        var escaping = false;

        for (var i = openBraceIndex; i < text.Length; i++)
        {
            var ch = text[i];

            if (inString)
            {
                if (escaping)
                {
                    escaping = false;
                }
                else if (ch == '\\')
                {
                    escaping = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '{')
            {
                depth++;
                continue;
            }

            if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static string UnescapeJsonString(string value)
    {
        return value
            .Replace("\\\"", "\"")
            .Replace("\\\\", "\\")
            .Replace("\\n", "\n")
            .Replace("\\r", "\r")
            .Replace("\\t", "\t");
    }

    private sealed class PresetValue
    {
        private readonly object _value;

        private PresetValue(object value, string displayValue)
        {
            _value = value;
            DisplayValue = displayValue;
        }

        public string DisplayValue { get; }

        public static PresetValue Parse(string token)
        {
            if (string.Equals(token, "true", StringComparison.OrdinalIgnoreCase))
            {
                return new PresetValue(true, "true");
            }

            if (string.Equals(token, "false", StringComparison.OrdinalIgnoreCase))
            {
                return new PresetValue(false, "false");
            }

            if (string.Equals(token, "null", StringComparison.OrdinalIgnoreCase))
            {
                return new PresetValue(null, "null");
            }

            if (token.StartsWith("\"", StringComparison.Ordinal) && token.EndsWith("\"", StringComparison.Ordinal))
            {
                var text = UnescapeJsonString(token.Substring(1, token.Length - 2));
                return new PresetValue(text, "\"" + text + "\"");
            }

            if (token.IndexOf('.') >= 0)
            {
                double doubleValue;
                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out doubleValue))
                {
                    return new PresetValue(doubleValue, token);
                }
            }
            else
            {
                int intValue;
                if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
                {
                    return new PresetValue(intValue, token);
                }
            }

            return new PresetValue(token, token);
        }

        public bool TryConvertTo(Type targetType, out object converted, out string error)
        {
            converted = null;
            error = null;

            if (_value == null)
            {
                converted = null;
                return true;
            }

            targetType = Nullable.GetUnderlyingType(targetType) ?? targetType;

            try
            {
                if (targetType == typeof(object) || targetType.IsInstanceOfType(_value))
                {
                    converted = _value;
                    return true;
                }

                if (targetType == typeof(bool))
                {
                    if (_value is bool boolValue)
                    {
                        converted = boolValue;
                        return true;
                    }

                    bool parsed;
                    if (bool.TryParse(Convert.ToString(_value, CultureInfo.InvariantCulture), out parsed))
                    {
                        converted = parsed;
                        return true;
                    }
                }

                if (targetType == typeof(string))
                {
                    converted = Convert.ToString(_value, CultureInfo.InvariantCulture);
                    return true;
                }

                if (targetType.IsEnum)
                {
                    converted = Enum.Parse(targetType, Convert.ToString(_value, CultureInfo.InvariantCulture), ignoreCase: true);
                    return true;
                }

                converted = Convert.ChangeType(_value, targetType, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
