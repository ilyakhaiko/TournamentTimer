using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

internal sealed class AslSettingsConfigureForm : Form
{
    private static readonly Color WindowBackColor = Color.FromArgb(11, 13, 18);
    private static readonly Color CardBackColor = Color.FromArgb(21, 25, 35);
    private static readonly Color HeaderBackColor = Color.FromArgb(21, 25, 35);
    private static readonly Color TitleBarBackColor = Color.FromArgb(21, 25, 35);
    private static readonly Color RowBackColor = Color.FromArgb(15, 18, 26);
    private static readonly Color RowAltBackColor = Color.FromArgb(18, 22, 32);
    private static readonly Color BorderColor = Color.FromArgb(42, 48, 64);
    private static readonly Color InputBackColor = Color.FromArgb(14, 17, 24);
    private static readonly Color PrimaryButtonColor = Color.FromArgb(47, 107, 255);
    private static readonly Color SecondaryButtonColor = Color.FromArgb(42, 48, 64);
    private static readonly Color TextColor = Color.FromArgb(244, 244, 245);
    private static readonly Color MutedTextColor = Color.FromArgb(185, 195, 215);
    private static readonly Color DimTextColor = Color.FromArgb(150, 154, 166);

    private const int CornerRadius = 10;

    private readonly object _aslSettings;
    private readonly string _settingsPath;
    private readonly DataGridView _settingsGrid;
    private readonly Label _statusLabel;
    private readonly TextBox _pathTextBox;
    private readonly List<SettingRow> _rows = new List<SettingRow>();

    public AslSettingsConfigureForm(object aslSettings, string settingsPath)
    {
        _aslSettings = aslSettings ?? throw new ArgumentNullException(nameof(aslSettings));
        _settingsPath = Path.GetFullPath(settingsPath ?? "asl-settings.json");

        Text = "TournamentTimer ASL Settings";
        this.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath) ?? this.Icon;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 560);
        Size = new Size(980, 740);
        Font = new Font("Segoe UI", 9F);
        BackColor = WindowBackColor;
        ForeColor = TextColor;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(16),
            BackColor = WindowBackColor
        };

        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var titleLabel = new Label
        {
            AutoSize = true,
            Text = "ASL Settings",
            Font = new Font("Segoe UI", 18F, FontStyle.Bold),
            ForeColor = TextColor,
            BackColor = HeaderBackColor,
            Margin = new Padding(0, 0, 0, 2)
        };

        var subtitleLabel = new Label
        {
            AutoSize = true,
            Text = "Choose ASL setting values and save them as asl-settings.json for this RunId.",
            ForeColor = MutedTextColor,
            BackColor = HeaderBackColor,
            Margin = new Padding(1, 0, 0, 14)
        };

        var headerPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            BackColor = HeaderBackColor,
            Padding = new Padding(16, 14, 16, 14),
            Margin = new Padding(0, 0, 0, 12)
        };
        headerPanel.Controls.Add(titleLabel);
        headerPanel.Controls.Add(subtitleLabel);

        var pathCard = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(16),
            Margin = new Padding(0, 0, 0, 12),
            BackColor = CardBackColor,
            BorderColor = BorderColor,
            BorderRadius = CornerRadius
        };

        var pathLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            BackColor = CardBackColor,
            Margin = new Padding(0)
        };
        pathLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        pathLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var pathLabel = CreateLabel("Settings file", DimTextColor, FontStyle.Regular);
        pathLabel.Dock = DockStyle.Fill;
        pathLabel.TextAlign = ContentAlignment.MiddleLeft;

        _pathTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.FixedSingle,
            Text = _settingsPath,
            Margin = new Padding(0),
            BackColor = InputBackColor,
            ForeColor = TextColor
        };

        pathLayout.Controls.Add(pathLabel, 0, 0);
        pathLayout.Controls.Add(_pathTextBox, 1, 0);
        pathCard.Controls.Add(pathLayout);

        var settingsCard = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            Margin = new Padding(0, 0, 0, 12),
            BackColor = CardBackColor,
            BorderColor = BorderColor,
            BorderRadius = CornerRadius
        };

        var settingsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 1,
            BackColor = Color.FromArgb(9, 11, 16),
            Margin = new Padding(0)
        };
        settingsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _settingsGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
            BackgroundColor = CardBackColor,
            BorderStyle = BorderStyle.None,
            GridColor = BorderColor,
            EnableHeadersVisualStyles = false,
            Margin = new Padding(0)
        };

        ConfigureSettingsGridTheme(_settingsGrid);

        _settingsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Setting",
            HeaderText = "Setting",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            ReadOnly = true
        });

        _settingsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Value",
            HeaderText = "Value",
            Width = 270
        });

        _settingsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Type",
            HeaderText = "Type",
            Width = 100,
            ReadOnly = true
        });

        _settingsGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_settingsGrid.IsCurrentCellDirty)
            {
                _settingsGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };

        settingsLayout.Controls.Add(_settingsGrid, 0, 0);
        settingsCard.Controls.Add(settingsLayout);

        _statusLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 28,
            Text = "Edit settings and press Save.",
            ForeColor = MutedTextColor,
            Margin = new Padding(2, 0, 0, 8),
            TextAlign = ContentAlignment.MiddleLeft
        };

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            BackColor = WindowBackColor,
            Margin = new Padding(0)
        };

        var saveButton = CreateButton("Save asl-settings.json", ButtonVariant.Primary);
        saveButton.Click += (_, _) => SavePreset();

        var generateSplitsButton = CreateButton("Preview / Generate splits.lss", ButtonVariant.Secondary);
        generateSplitsButton.Click += (_, _) => OpenGenerateSplitsPreview();

        var cancelButton = CreateButton("Close", ButtonVariant.Secondary);
        cancelButton.Click += (_, _) => Close();

        buttonPanel.Controls.Add(saveButton);
        buttonPanel.Controls.Add(generateSplitsButton);
        buttonPanel.Controls.Add(cancelButton);

        root.Controls.Add(headerPanel, 0, 0);
        root.Controls.Add(pathCard, 0, 1);
        root.Controls.Add(settingsCard, 0, 2);
        root.Controls.Add(_statusLabel, 0, 3);
        root.Controls.Add(buttonPanel, 0, 4);

        Controls.Add(root);

        Shown += (_, _) =>
        {
            TryUseImmersiveDarkTitleBar();
            TryApplyDarkControlTheme(_pathTextBox);
            TryApplyDarkControlTheme(_settingsGrid);
        };

        LoadRows();
    }

    private static void ConfigureSettingsGridTheme(DataGridView grid)
    {
        grid.ColumnHeadersDefaultCellStyle.BackColor = CardBackColor;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = MutedTextColor;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = CardBackColor;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = MutedTextColor;

        grid.DefaultCellStyle.BackColor = RowBackColor;
        grid.DefaultCellStyle.ForeColor = TextColor;
        grid.DefaultCellStyle.SelectionBackColor = SecondaryButtonColor;
        grid.DefaultCellStyle.SelectionForeColor = TextColor;

        grid.AlternatingRowsDefaultCellStyle.BackColor = RowAltBackColor;
        grid.AlternatingRowsDefaultCellStyle.ForeColor = TextColor;

        grid.RowTemplate.Height = 28;
    }

    private void LoadRows()
    {
        _rows.Clear();
        _settingsGrid.Rows.Clear();

        var settings = GetOrderedSettings(_aslSettings)
            .Cast<object>()
            .ToList();

        if (settings.Count == 0)
        {
            _statusLabel.Text = "No settings declared by this ASL.";
            return;
        }

        foreach (var setting in settings)
        {
            var row = CreateRow(setting, _rows.Count);
            _rows.Add(row);
        }

        _statusLabel.Text = $"Loaded {_rows.Count} setting(s).";
    }

    private SettingRow CreateRow(object setting, int rowIndex)
    {
        var id = Convert.ToString(GetMemberValue(setting, "Id"), CultureInfo.InvariantCulture) ?? "";
        var parent = Convert.ToString(GetMemberValue(setting, "Parent"), CultureInfo.InvariantCulture) ?? "";
        var value = GetMemberValue(setting, "Value");

        var label = FirstNonEmpty(
            Convert.ToString(GetMemberValue(setting, "Label"), CultureInfo.InvariantCulture),
            Convert.ToString(GetMemberValue(setting, "Name"), CultureInfo.InvariantCulture),
            id);

        var tooltip = FirstNonEmpty(
            Convert.ToString(GetMemberValue(setting, "Description"), CultureInfo.InvariantCulture),
            Convert.ToString(GetMemberValue(setting, "ToolTip"), CultureInfo.InvariantCulture),
            "");

        var settingText = string.IsNullOrWhiteSpace(label) || label == id
            ? id
            : $"{label}  ({id})";

        var gridRowIndex = _settingsGrid.Rows.Add();
        var gridRow = _settingsGrid.Rows[gridRowIndex];

        gridRow.Cells[0].Value = settingText;
        gridRow.Cells[2].Value = value == null ? "null" : value.GetType().Name;

        Func<object> readValue;

        if (value is bool boolValue)
        {
            gridRow.Cells[1] = new DataGridViewCheckBoxCell
            {
                Value = boolValue
            };

            readValue = () =>
            {
                var cellValue = gridRow.Cells[1].Value;
                return cellValue is bool b && b;
            };
        }
        else
        {
            gridRow.Cells[1].Value = value == null
                ? ""
                : Convert.ToString(value, CultureInfo.InvariantCulture);

            readValue = () =>
                ConvertTextboxValue(
                    Convert.ToString(gridRow.Cells[1].Value, CultureInfo.InvariantCulture) ?? "",
                    value);
        }

        gridRow.Tag = id;

        return new SettingRow
        {
            Id = id,
            Parent = parent,
            Label = label,
            Description = tooltip,
            ReadValue = readValue
        };
    }

    private void OpenGenerateSplitsPreview()
    {
        try
        {
            var candidates = BuildSplitCandidatesFromRows();

            if (candidates.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "No usable ASL split-like settings were found.\r\n\r\nUse the placeholder splits.lss or create the split list manually.",
                    "No split candidates",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var settingsDirectory = Path.GetDirectoryName(_settingsPath);

            if (string.IsNullOrWhiteSpace(settingsDirectory))
            {
                settingsDirectory = Environment.CurrentDirectory;
            }

            var splitsPath = Path.Combine(settingsDirectory, "splits.lss");
            var gameName = FirstNonEmpty(new DirectoryInfo(settingsDirectory).Name, "Autosplitter Test");

            using (var previewForm = new AslSplitsPreviewForm(candidates, splitsPath, gameName))
            {
                if (previewForm.ShowDialog(this) == DialogResult.OK)
                {
                    _statusLabel.ForeColor = Color.FromArgb(140, 255, 179);
                    _statusLabel.Text = "Saved splits.lss: " + splitsPath;
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Generate splits failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private List<SplitCandidate> BuildSplitCandidatesFromRows()
    {
        var idsWithChildren = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in _rows)
        {
            if (!string.IsNullOrWhiteSpace(row.Parent))
            {
                idsWithChildren.Add(row.Parent.Trim());
            }
        }

        var candidates = new List<SplitCandidate>();

        foreach (var row in _rows)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.Id))
            {
                continue;
            }

            object value;

            try
            {
                value = row.ReadValue == null ? null : row.ReadValue();
            }
            catch
            {
                continue;
            }

            if (!(value is bool))
            {
                continue;
            }

            if (idsWithChildren.Contains(row.Id))
            {
                continue;
            }

            var enabled = (bool)value;
            var likelyNonSplit = LooksLikeNonSplitSetting(row);
            var likelySplit = LooksLikeSplitSetting(row);
            var includedByDefault = enabled && (likelySplit || !likelyNonSplit);
            var reason = BuildSplitCandidateReason(row, enabled, likelySplit, likelyNonSplit);

            candidates.Add(new SplitCandidate
            {
                Id = row.Id,
                Name = BuildDefaultSplitName(row),
                Parent = row.Parent ?? "",
                Reason = reason,
                Included = includedByDefault
            });
        }

        return candidates;
    }

    private static string BuildSplitCandidateReason(SettingRow row, bool enabled, bool likelySplit, bool likelyNonSplit)
    {
        var builder = new StringBuilder();

        builder.Append(enabled ? "enabled" : "disabled");

        if (!string.IsNullOrWhiteSpace(row.Parent))
        {
            builder.Append(", child of ");
            builder.Append(row.Parent);
        }

        if (likelySplit)
        {
            builder.Append(", split-like");
        }
        else if (likelyNonSplit)
        {
            builder.Append(", control/option-like");
        }
        else
        {
            builder.Append(", setting leaf");
        }

        return builder.ToString();
    }

    private static string BuildDefaultSplitName(SettingRow row)
    {
        var label = FirstNonEmpty(row.Label, row.Id);

        if (!string.Equals(label, row.Id, StringComparison.OrdinalIgnoreCase))
        {
            return CleanSplitName(label);
        }

        var normalized = row.Id ?? "";
        normalized = Regex.Replace(normalized, "^[a-zA-Z]+_split_", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, "^split_", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"[_\-.]+", " ");
        normalized = Regex.Replace(normalized, "(?<=[a-z])(?=[A-Z])", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();

        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = row.Id;
        }

        return CleanSplitName(normalized);
    }

    private static string CleanSplitName(string value)
    {
        value = Regex.Replace(value ?? "", @"\s+", " ").Trim();

        if (string.IsNullOrWhiteSpace(value))
        {
            return "Split";
        }

        return value;
    }

    private static bool LooksLikeSplitSetting(SettingRow row)
    {
        var haystack = BuildSettingSearchText(row);

        return haystack.Contains("split") ||
               haystack.Contains("chapter") ||
               haystack.Contains("level") ||
               haystack.Contains("stage") ||
               haystack.Contains("act") ||
               haystack.Contains("boss") ||
               haystack.Contains("mission") ||
               haystack.Contains("room") ||
               haystack.Contains("area") ||
               haystack.Contains("zone") ||
               haystack.Contains("episode") ||
               haystack.Contains("ending") ||
               haystack.Contains("credits") ||
               haystack.Contains("checkpoint") ||
               !string.IsNullOrWhiteSpace(row.Parent);
    }

    private static bool LooksLikeNonSplitSetting(SettingRow row)
    {
        var haystack = BuildSettingSearchText(row);

        return haystack.Contains("start") ||
               haystack.Contains("reset") ||
               haystack.Contains("debug") ||
               haystack.Contains("option") ||
               haystack.Contains("setting") ||
               haystack.Contains("settings") ||
               haystack.Contains("timer") ||
               haystack.Contains("timing") ||
               haystack.Contains("pause") ||
               haystack.Contains("resume") ||
               haystack.Contains("version") ||
               haystack.Contains("verbose") ||
               haystack.Contains("igt") ||
               haystack.Contains("game time") ||
               haystack.Contains("gametime") ||
               haystack.Contains("status");
    }

    private static string BuildSettingSearchText(SettingRow row)
    {
        return (
            (row.Id ?? "") + " " +
            (row.Label ?? "") + " " +
            (row.Parent ?? "") + " " +
            (row.Description ?? ""))
            .ToLowerInvariant();
    }

    private void ResizeRows()
    {
    }

    private void SavePreset()
    {
        try
        {
            var values = new List<KeyValuePair<string, object>>();

            foreach (var row in _rows)
            {
                if (string.IsNullOrWhiteSpace(row.Id))
                {
                    continue;
                }

                values.Add(new KeyValuePair<string, object>(row.Id, row.ReadValue()));
            }

            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_settingsPath, BuildPresetJson(values), new UTF8Encoding(false));

            _statusLabel.ForeColor = Color.FromArgb(140, 255, 179);
            _statusLabel.Text = $"Saved {values.Count} setting(s): {_settingsPath}";
            Console.WriteLine("ASL settings preset saved: " + _settingsPath);
        }
        catch (Exception ex)
        {
            _statusLabel.ForeColor = Color.FromArgb(255, 159, 159);
            _statusLabel.Text = "Save failed: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static object ConvertTextboxValue(string text, object sampleValue)
    {
        if (sampleValue is int)
        {
            int parsed;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : text;
        }

        if (sampleValue is long)
        {
            long parsed;
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : text;
        }

        if (sampleValue is float)
        {
            float parsed;
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : text;
        }

        if (sampleValue is double)
        {
            double parsed;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : text;
        }

        return text ?? "";
    }

    private static string BuildPresetJson(IReadOnlyList<KeyValuePair<string, object>> values)
    {
        var builder = new StringBuilder();
        builder.AppendLine("{");
        builder.AppendLine("  \"version\": 1,");
        builder.AppendLine("  \"settings\": {");

        for (var i = 0; i < values.Count; i++)
        {
            var pair = values[i];
            builder.Append("    \"");
            builder.Append(EscapeJson(pair.Key));
            builder.Append("\": ");
            builder.Append(FormatJsonValue(pair.Value));

            if (i < values.Count - 1)
            {
                builder.Append(',');
            }

            builder.AppendLine();
        }

        builder.AppendLine("  }");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string FormatJsonValue(object value)
    {
        if (value == null)
        {
            return "null";
        }

        if (value is bool boolValue)
        {
            return boolValue ? "true" : "false";
        }

        if (value is int || value is long || value is float || value is double || value is decimal)
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        return "\"" + EscapeJson(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "") + "\"";
    }

    private static string EscapeJson(string value)
    {
        return (value ?? "")
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
    }

    private static IEnumerable GetOrderedSettings(object aslSettings)
    {
        var property = aslSettings.GetType().GetProperty("OrderedSettings", BindingFlags.Instance | BindingFlags.Public);
        if (property != null)
        {
            return property.GetValue(aslSettings, null) as IEnumerable ?? Array.Empty<object>();
        }

        return aslSettings as IEnumerable ?? Array.Empty<object>();
    }

    private static object GetMemberValue(object target, string name)
    {
        if (target == null)
        {
            return null;
        }

        var type = target.GetType();

        var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        if (property != null)
        {
            return property.GetValue(target, null);
        }

        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return field == null ? null : field.GetValue(target);
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }

    private static Label CreateLabel(string text, Color color, FontStyle style)
    {
        return new Label
        {
            AutoSize = false,
            Text = text,
            ForeColor = color,
            Font = new Font("Segoe UI", 9F, style),
            Margin = new Padding(0)
        };
    }

    private static Label CreateHeaderLabel(string text)
    {
        return new Label
        {
            Dock = DockStyle.Fill,
            Text = text,
            ForeColor = MutedTextColor,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(10, 0, 0, 0)
        };
    }

    private static RoundedButton CreateButton(string text, ButtonVariant variant)
    {
        var normalBackColor = variant == ButtonVariant.Primary
            ? PrimaryButtonColor
            : SecondaryButtonColor;

        var textSize = TextRenderer.MeasureText(text, new Font("Segoe UI", 9F, FontStyle.Bold));

        return new RoundedButton
        {
            Text = text,
            AutoSize = false,
            Size = new Size(textSize.Width + 34, 38),
            Padding = new Padding(12, 0, 12, 0),
            Margin = new Padding(8, 0, 0, 0),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            BorderRadius = CornerRadius,
            BackColor = normalBackColor,
            NormalBackColor = normalBackColor,
            HoverBackColor = variant == ButtonVariant.Primary
                ? Color.FromArgb(63, 123, 255)
                : Color.FromArgb(54, 62, 82),
            PressedBackColor = variant == ButtonVariant.Primary
                ? Color.FromArgb(38, 86, 210)
                : Color.FromArgb(32, 37, 50)
        };
    }

    private void TryUseImmersiveDarkTitleBar()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            var useDark = 1;
            DwmSetWindowAttribute(Handle, 20, ref useDark, sizeof(int));
            DwmSetWindowAttribute(Handle, 19, ref useDark, sizeof(int));

            var captionColor = ToColorRef(TitleBarBackColor);
            DwmSetWindowAttribute(Handle, 35, ref captionColor, sizeof(int));

            var textColor = ToColorRef(TextColor);
            DwmSetWindowAttribute(Handle, 36, ref textColor, sizeof(int));
        }
        catch
        {
            // Best effort only.
        }
    }

    private static void TryApplyDarkControlTheme(Control control)
    {
        if (control == null || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            if (!control.IsHandleCreated)
            {
                var handle = control.Handle;
            }

            SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static int ToColorRef(Color color)
    {
        return color.R | (color.G << 8) | (color.B << 16);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetWindowTheme(IntPtr hwnd, string pszSubAppName, string pszSubIdList);

    private sealed class SplitCandidate
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Parent { get; set; }
        public string Reason { get; set; }
        public bool Included { get; set; }
    }

    private sealed class AslSplitsPreviewForm : Form
    {
        private readonly string _splitsPath;
        private readonly string _gameName;
        private readonly DataGridView _grid;
        private readonly Label _statusLabel;

        public AslSplitsPreviewForm(IReadOnlyList<SplitCandidate> candidates, string splitsPath, string gameName)
        {
            _splitsPath = Path.GetFullPath(splitsPath ?? "splits.lss");
            _gameName = FirstNonEmpty(gameName, "Autosplitter Test");

            Text = "Preview generated splits.lss";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(900, 560);
            Size = new Size(1040, 720);
            Font = new Font("Segoe UI", 9F);
            BackColor = WindowBackColor;
            ForeColor = TextColor;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(16),
                BackColor = WindowBackColor
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var headerPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                BackColor = HeaderBackColor,
                Padding = new Padding(16, 14, 16, 14),
                Margin = new Padding(0, 0, 0, 12)
            };

            headerPanel.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "Generate splits.lss from ASL settings",
                Font = new Font("Segoe UI", 16F, FontStyle.Bold),
                ForeColor = TextColor,
                BackColor = HeaderBackColor,
                Margin = new Padding(0, 0, 0, 2)
            });

            headerPanel.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "Best-effort preview. Uncheck non-splits, rename rows, reorder them, then save.",
                ForeColor = MutedTextColor,
                BackColor = HeaderBackColor,
                Margin = new Padding(1, 0, 0, 0)
            });

            var pathBox = new TextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Text = _splitsPath,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = InputBackColor,
                ForeColor = TextColor,
                Margin = new Padding(0, 0, 0, 12)
            };

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                RowHeadersVisible = false,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
                BackgroundColor = CardBackColor,
                BorderStyle = BorderStyle.None,
                GridColor = BorderColor,
                EnableHeadersVisualStyles = false,
                Margin = new Padding(0, 0, 0, 10)
            };

            ConfigureGridTheme(_grid);

            _grid.Columns.Add(new DataGridViewCheckBoxColumn
            {
                Name = "Include",
                HeaderText = "Use",
                Width = 58
            });

            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "SplitName",
                HeaderText = "Split name",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            });

            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Id",
                HeaderText = "ASL setting id",
                Width = 220,
                ReadOnly = true
            });

            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Reason",
                HeaderText = "Reason",
                Width = 230,
                ReadOnly = true
            });

            foreach (var candidate in candidates)
            {
                _grid.Rows.Add(candidate.Included, candidate.Name, candidate.Id, candidate.Reason);
            }

            _statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                Height = 28,
                Text = BuildStatusText(),
                ForeColor = MutedTextColor,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = HeaderBackColor,
                Margin = new Padding(2, 0, 0, 8)
            };

            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                WrapContents = false,
                BackColor = WindowBackColor,
                Margin = new Padding(0)
            };

            var saveButton = CreateButton("Save splits.lss", ButtonVariant.Primary);
            saveButton.Click += (_, _) => SaveSplits();

            var closeButton = CreateButton("Close", ButtonVariant.Secondary);
            closeButton.Click += (_, _) => Close();

            var moveDownButton = CreateButton("Move down", ButtonVariant.Secondary);
            moveDownButton.Click += (_, _) => MoveSelectedRow(1);

            var moveUpButton = CreateButton("Move up", ButtonVariant.Secondary);
            moveUpButton.Click += (_, _) => MoveSelectedRow(-1);

            var selectAllButton = CreateButton("Select all", ButtonVariant.Secondary);
            selectAllButton.Click += (_, _) => SetAllIncluded(true);

            var clearButton = CreateButton("Clear", ButtonVariant.Secondary);
            clearButton.Click += (_, _) => SetAllIncluded(false);

            buttonPanel.Controls.Add(saveButton);
            buttonPanel.Controls.Add(closeButton);
            buttonPanel.Controls.Add(moveDownButton);
            buttonPanel.Controls.Add(moveUpButton);
            buttonPanel.Controls.Add(selectAllButton);
            buttonPanel.Controls.Add(clearButton);

            root.Controls.Add(headerPanel, 0, 0);
            root.Controls.Add(pathBox, 0, 1);
            root.Controls.Add(_grid, 0, 2);
            root.Controls.Add(_statusLabel, 0, 3);
            root.Controls.Add(buttonPanel, 0, 4);

            Controls.Add(root);

            Shown += (_, _) =>
            {
                TryUseImmersiveDarkTitleBar();
                TryApplyDarkControlTheme(pathBox);
                TryApplyDarkControlTheme(_grid);
            };

            _grid.CellValueChanged += (_, _) => _statusLabel.Text = BuildStatusText();
            _grid.CurrentCellDirtyStateChanged += (_, _) =>
            {
                if (_grid.IsCurrentCellDirty)
                {
                    _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                }
            };
        }

        private void TryUseImmersiveDarkTitleBar()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            try
            {
                var useDark = 1;
                DwmSetWindowAttribute(Handle, 20, ref useDark, sizeof(int));
                DwmSetWindowAttribute(Handle, 19, ref useDark, sizeof(int));

                var captionColor = ToColorRef(TitleBarBackColor);
                DwmSetWindowAttribute(Handle, 35, ref captionColor, sizeof(int));

                var textColor = ToColorRef(TextColor);
                DwmSetWindowAttribute(Handle, 36, ref textColor, sizeof(int));
            }
            catch
            {
                // Best effort only.
            }
        }

        private void ConfigureGridTheme(DataGridView grid)
        {
            grid.ColumnHeadersDefaultCellStyle.BackColor = CardBackColor;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = MutedTextColor;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = CardBackColor;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = MutedTextColor;
            grid.DefaultCellStyle.BackColor = RowBackColor;
            grid.DefaultCellStyle.ForeColor = TextColor;
            grid.DefaultCellStyle.SelectionBackColor = SecondaryButtonColor;
            grid.DefaultCellStyle.SelectionForeColor = TextColor;
            grid.AlternatingRowsDefaultCellStyle.BackColor = RowAltBackColor;
            grid.AlternatingRowsDefaultCellStyle.ForeColor = TextColor;
            grid.RowTemplate.Height = 28;
        }

        private string BuildStatusText()
        {
            return "Selected " + CollectSelectedNames().Count + " split(s). Review names before saving.";
        }

        private void SetAllIncluded(bool included)
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                row.Cells[0].Value = included;
            }

            _statusLabel.Text = BuildStatusText();
        }

        private void MoveSelectedRow(int direction)
        {
            if (_grid.SelectedRows.Count == 0)
            {
                return;
            }

            var index = _grid.SelectedRows[0].Index;
            var targetIndex = index + direction;

            if (targetIndex < 0 || targetIndex >= _grid.Rows.Count)
            {
                return;
            }

            var row = _grid.Rows[index];
            _grid.Rows.RemoveAt(index);
            _grid.Rows.Insert(targetIndex, row);
            row.Selected = true;
            _grid.CurrentCell = row.Cells[1];
        }

        private void SaveSplits()
        {
            _grid.EndEdit();

            var splitNames = CollectSelectedNames();

            if (splitNames.Count == 0)
            {
                MessageBox.Show(this, "Select at least one split.", "No splits selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (File.Exists(_splitsPath))
            {
                var choice = MessageBox.Show(
                    this,
                    "Overwrite existing splits.lss?\r\n\r\n" + _splitsPath,
                    "Overwrite splits.lss",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);

                if (choice != DialogResult.Yes)
                {
                    return;
                }
            }

            var directory = Path.GetDirectoryName(_splitsPath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_splitsPath, BuildLiveSplitRunXml(_gameName, "Generated from ASL settings", splitNames), new UTF8Encoding(false));

            DialogResult = DialogResult.OK;
            Close();
        }

        private List<string> CollectSelectedNames()
        {
            var names = new List<string>();

            foreach (DataGridViewRow row in _grid.Rows)
            {
                var included = false;
                var includeValue = row.Cells[0].Value;

                if (includeValue is bool)
                {
                    included = (bool)includeValue;
                }

                if (!included)
                {
                    continue;
                }

                var name = Convert.ToString(row.Cells[1].Value, CultureInfo.InvariantCulture);
                name = CleanSplitName(name);

                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }

            return names;
        }

        private static string BuildLiveSplitRunXml(string gameName, string categoryName, IReadOnlyList<string> splitNames)
        {
            var segments = new StringBuilder();

            foreach (var splitName in splitNames)
            {
                segments.AppendLine("    <Segment>");
                segments.AppendLine("      <Name>" + SecurityElement.Escape(splitName ?? "Split") + "</Name>");
                segments.AppendLine("      <Icon />");
                segments.AppendLine("      <SplitTimes />");
                segments.AppendLine("      <BestSegmentTime />");
                segments.AppendLine("      <SegmentHistory />");
                segments.AppendLine("    </Segment>");
            }

            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n" +
                "<Run version=\"1.7.0\">\r\n" +
                "  <GameIcon />\r\n" +
                "  <GameName>" + SecurityElement.Escape(gameName ?? "Autosplitter Test") + "</GameName>\r\n" +
                "  <CategoryName>" + SecurityElement.Escape(categoryName ?? "Generated from ASL settings") + "</CategoryName>\r\n" +
                "  <LayoutPath />\r\n" +
                "  <Metadata>\r\n" +
                "    <Run id=\"\" />\r\n" +
                "    <Platform usesEmulator=\"False\"></Platform>\r\n" +
                "    <Region></Region>\r\n" +
                "    <Variables />\r\n" +
                "  </Metadata>\r\n" +
                "  <Offset>00:00:00</Offset>\r\n" +
                "  <AttemptCount>0</AttemptCount>\r\n" +
                "  <AttemptHistory />\r\n" +
                "  <Segments>\r\n" +
                segments +
                "  </Segments>\r\n" +
                "  <AutoSplitterSettings />\r\n" +
                "</Run>\r\n";
        }
    }

    private sealed class SettingRow
    {
        public string Id { get; set; }
        public string Parent { get; set; }
        public string Label { get; set; }
        public string Description { get; set; }
        public Func<object> ReadValue { get; set; }
    }

    private enum ButtonVariant
    {
        Primary,
        Secondary
    }

    private sealed class RoundedPanel : Panel
    {
        public int BorderRadius { get; set; } = CornerRadius;
        public Color BorderColor { get; set; } = Color.FromArgb(42, 48, 64);

        public RoundedPanel()
        {
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);

            using (var path = CreateRoundedRectanglePath(bounds, BorderRadius))
            using (var fill = new SolidBrush(BackColor))
            using (var border = new Pen(BorderColor))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }
        }
    }

    private sealed class RoundedButton : Button
    {
        public int BorderRadius { get; set; } = CornerRadius;
        public Color NormalBackColor { get; set; } = SecondaryButtonColor;
        public Color HoverBackColor { get; set; } = Color.FromArgb(54, 62, 82);
        public Color PressedBackColor { get; set; } = Color.FromArgb(32, 37, 50);

        public RoundedButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            MinimumSize = new Size(0, 38);

            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true);
        }

        protected override bool ShowFocusCues => false;

        protected override void OnMouseEnter(EventArgs e)
        {
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            Invalidate();
            base.OnMouseDown(mevent);
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            Invalidate();
            base.OnMouseUp(mevent);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            var mousePoint = PointToClient(Cursor.Position);
            var isHovered = ClientRectangle.Contains(mousePoint);
            var isPressed = isHovered && (Control.MouseButtons & MouseButtons.Left) == MouseButtons.Left;

            var fillColor = isPressed
                ? PressedBackColor
                : isHovered
                    ? HoverBackColor
                    : NormalBackColor;

            e.Graphics.Clear(Parent?.BackColor ?? WindowBackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);

            using (var path = CreateRoundedRectanglePath(bounds, BorderRadius))
            using (var fill = new SolidBrush(fillColor))
            {
                e.Graphics.FillPath(fill, path);
            }

            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                ClientRectangle,
                ForeColor,
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis);
        }
    }

    private static GraphicsPath CreateRoundedRectanglePath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Max(1, radius * 2);
        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));

        path.AddArc(arc, 180, 90);

        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);

        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);

        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);

        path.CloseFigure();
        return path;
    }
}
