using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal sealed class AutosplitterCatalogForm : Form
{
    private const int ResultDataColumnCount = 5;
    private const int ResultFillerColumnIndex = 5;
    private const int ResultMinFillerColumnWidth = 1;
    private const int CornerRadius = 10;

    private static readonly Color WindowBackground = Color.FromArgb(11, 13, 18);
    private static readonly Color CardBackground = Color.FromArgb(21, 25, 35);
    private static readonly Color HeaderBackground = Color.FromArgb(21, 25, 35);
    private static readonly Color TitleBarBackground = Color.FromArgb(21, 25, 35);
    private static readonly Color CardBackgroundAlt = Color.FromArgb(15, 18, 26);
    private static readonly Color BorderColor = Color.FromArgb(42, 48, 64);
    private static readonly Color TextForeground = Color.FromArgb(244, 244, 245);
    private static readonly Color MutedForeground = Color.FromArgb(150, 154, 166);
    private static readonly Color AccentBackground = Color.FromArgb(47, 107, 255);
    private static readonly Color SecondaryBackground = Color.FromArgb(42, 48, 64);
    private static readonly Color DangerBackground = Color.FromArgb(168, 50, 50);
    private static readonly Color DangerForeground = Color.FromArgb(255, 159, 159);
    private static readonly Color WarningForeground = Color.FromArgb(255, 190, 92);

    private readonly AslHostOptions _options;
    private readonly AutoSplitterCatalogService _service = new AutoSplitterCatalogService();

    private readonly TextBox _catalogUrlTextBox;
    private readonly TextBox _searchTextBox;
    private readonly TextBox _runIdTextBox;
    private readonly TextBox _runsRootTextBox;
    private readonly TextBox _splitsPathTextBox;
    private readonly NumericUpDown _placeholderCountBox;
    private readonly ListView _resultsListView;
    private readonly TextBox _detailsTextBox;
    private readonly TextBox _installSummaryTextBox;
    private readonly Label _statusLabel;

    private bool _initialSplitLayoutApplied;
    private bool _adjustingResultColumns;

    private AutoSplitterCatalogEntry[] _catalog = Array.Empty<AutoSplitterCatalogEntry>();
    private AutoSplitterInstallResult _lastInstallResult;

    public AutosplitterCatalogForm(AslHostOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        Text = "TournamentTimer ASL Catalog";
        this.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath) ?? this.Icon;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1180, 740);
        Size = new Size(1500, 860);
        Font = new Font("Segoe UI", 9F);
        BackColor = WindowBackground;
        ForeColor = TextForeground;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(18),
            BackColor = WindowBackground
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 172));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = CreateHeader();

        var settingsGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 4,
            RowCount = 5,
            BackColor = CardBackground
        };
        settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        _catalogUrlTextBox = CreateTextBox(AutoSplitterCatalogService.DefaultCatalogUrl);
        _searchTextBox = CreateTextBox("");
        _runIdTextBox = CreateTextBox("local-test-run");
        _runsRootTextBox = CreateTextBox(_options.RunsRoot);
        _splitsPathTextBox = CreateTextBox("");

        var browseSplitsButton = CreateButton("Browse splits.lss...", ButtonVariant.Secondary);
        browseSplitsButton.Click += (_, _) => BrowseSplits();

        _placeholderCountBox = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 999,
            Value = 10,
            Width = 90,
            BackColor = CardBackgroundAlt,
            ForeColor = TextForeground,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 2, 10, 2)
        };


        AddLabeledControl(settingsGrid, 0, "Catalog URL", _catalogUrlTextBox);
        AddLabeledControl(settingsGrid, 1, "Search game", _searchTextBox);
        AddLabeledControl(settingsGrid, 2, "RunId", _runIdTextBox);
        AddLabeledControl(settingsGrid, 3, "Runs root", _runsRootTextBox);

        var splitsLabel = CreateFormLabel("splits.lss");
        settingsGrid.Controls.Add(splitsLabel, 0, 4);
        settingsGrid.Controls.Add(_splitsPathTextBox, 1, 4);
        settingsGrid.Controls.Add(browseSplitsButton, 2, 4);

        var placeholderPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = CardBackground,
            Margin = new Padding(0)
        };
        placeholderPanel.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "placeholder splits:",
            ForeColor = MutedForeground,
            BackColor = CardBackground,
            Margin = new Padding(0, 6, 6, 0)
        });
        placeholderPanel.Controls.Add(_placeholderCountBox);
        settingsGrid.Controls.Add(placeholderPanel, 3, 4);

        var settingsCard = CreateCard(settingsGrid);

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = true,
            BackColor = CardBackground,
            Margin = new Padding(0)
        };

        var loadCatalogButton = CreateButton("Load catalog", ButtonVariant.Secondary);
        loadCatalogButton.Click += (_, _) => LoadCatalog();

        var searchButton = CreateButton("Search", ButtonVariant.Primary);
        searchButton.Click += (_, _) => SearchCatalog();

        var installButton = CreateButton("Install selected", ButtonVariant.Secondary);
        installButton.Click += (_, _) => InstallSelected(configureAfterInstall: false);

        var installConfigureButton = CreateButton("Install + Configure", ButtonVariant.Primary);
        installConfigureButton.Click += (_, _) => InstallSelected(configureAfterInstall: true);

        var configureButton = CreateButton("Open settings for RunId", ButtonVariant.Secondary);
        configureButton.Click += (_, _) => OpenConfigureForRunId();

        var openFolderButton = CreateButton("Open run folder", ButtonVariant.Secondary);
        openFolderButton.Click += (_, _) => OpenRunFolder();

        buttonPanel.Controls.Add(loadCatalogButton);
        buttonPanel.Controls.Add(searchButton);
        buttonPanel.Controls.Add(installButton);
        buttonPanel.Controls.Add(installConfigureButton);
        buttonPanel.Controls.Add(configureButton);
        buttonPanel.Controls.Add(openFolderButton);

        var actionsCard = CreateCard(buttonPanel);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            BackColor = WindowBackground
        };

        _resultsListView = new RepaintSafeListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            HideSelection = false,
            BackColor = CardBackgroundAlt,
            ForeColor = TextForeground,
            BorderStyle = BorderStyle.None,
            OwnerDraw = true
        };
        _resultsListView.Columns.Add("Game", 260);
        _resultsListView.Columns.Add("Compatibility", 150);
        _resultsListView.Columns.Add("Deps", 145);
        _resultsListView.Columns.Add("Type", 80);
        _resultsListView.Columns.Add("Description", 420);
        _resultsListView.Columns.Add("", ResultMinFillerColumnWidth);
        _resultsListView.SelectedIndexChanged += (_, _) => RenderSelectedDetails();
        _resultsListView.DoubleClick += (_, _) => InstallSelected(configureAfterInstall: true);
        _resultsListView.DrawColumnHeader += DrawListViewColumnHeader;
        _resultsListView.DrawSubItem += DrawListViewSubItem;
        _resultsListView.SizeChanged += (_, _) =>
        {
            FillResultsFillerColumn();
            QueueResultsListRepaint();
        };
        _resultsListView.ColumnWidthChanging += ResultsListView_ColumnWidthChanging;
        _resultsListView.ColumnWidthChanged += (_, _) =>
        {
            FillResultsFillerColumn();
            QueueResultsListRepaint();
        };
        _resultsListView.HandleCreated += (_, _) => TryApplyDarkControlTheme(_resultsListView);

        _detailsTextBox = CreateTextBox("");
        _detailsTextBox.Multiline = true;
        _detailsTextBox.ScrollBars = ScrollBars.Vertical;
        _detailsTextBox.ReadOnly = true;
        _detailsTextBox.WordWrap = true;
        _detailsTextBox.Font = new Font("Consolas", 9F);
        _detailsTextBox.BorderStyle = BorderStyle.None;
        _detailsTextBox.HandleCreated += (_, _) => TryApplyDarkControlTheme(_detailsTextBox);

        split.Panel1.BackColor = CardBackground;
        split.Panel2.BackColor = CardBackground;
        split.Panel1.Padding = new Padding(1);
        split.Panel2.Padding = new Padding(1);
        split.Panel1.Controls.Add(_resultsListView);
        split.Panel2.Controls.Add(_detailsTextBox);

        var splitCard = CreateCard(split);
        splitCard.AutoSize = false;

        var installSummaryPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 132,
            MinimumSize = new Size(0, 132),
            ColumnCount = 1,
            RowCount = 2,
            BackColor = CardBackground,
            Margin = new Padding(0)
        };
        installSummaryPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        installSummaryPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        installSummaryPanel.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "Install summary",
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = TextForeground,
            BackColor = CardBackground,
            Margin = new Padding(0, 0, 0, 7)
        }, 0, 0);

        _installSummaryTextBox = CreateTextBox("No install yet. Install selected ASL to show copied files and next steps here.");
        _installSummaryTextBox.Multiline = true;
        _installSummaryTextBox.ScrollBars = ScrollBars.Vertical;
        _installSummaryTextBox.ReadOnly = true;
        _installSummaryTextBox.WordWrap = true;
        _installSummaryTextBox.Font = new Font("Consolas", 9F);
        _installSummaryTextBox.BorderStyle = BorderStyle.None;
        _installSummaryTextBox.Margin = new Padding(0);
        _installSummaryTextBox.MinimumSize = new Size(0, 88);
        _installSummaryTextBox.HandleCreated += (_, _) => TryApplyDarkControlTheme(_installSummaryTextBox);
        installSummaryPanel.Controls.Add(_installSummaryTextBox, 0, 1);

        var installSummaryCard = CreateCard(installSummaryPanel);
        installSummaryCard.AutoSize = false;
        installSummaryCard.Height = 164;
        installSummaryCard.MinimumSize = new Size(0, 164);

        _statusLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = "1) Load catalog  2) Search game  3) Install + Configure  4) Test ASL Host x86/x64",
            ForeColor = MutedForeground,
            BackColor = WindowBackground,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(2, 10, 2, 0)
        };

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(settingsCard, 0, 1);
        root.Controls.Add(actionsCard, 0, 2);
        root.Controls.Add(splitCard, 0, 3);
        root.Controls.Add(installSummaryCard, 0, 4);
        root.Controls.Add(_statusLabel, 0, 5);
        Controls.Add(root);

        Shown += (_, _) =>
        {
            TryUseImmersiveDarkTitleBar();
            TryApplyDarkControlTheme(_resultsListView);
            TryApplyDarkControlTheme(_detailsTextBox);
            TryApplyDarkControlTheme(_installSummaryTextBox);

            BeginInvoke(new Action(() =>
            {
                ApplySplitLayout(split, force: true);
                ApplyResultsColumnLayout();
                QueueResultsListRepaint();
            }));
        };
        split.SizeChanged += (_, _) =>
        {
            ApplySplitLayout(split, force: false);
            ApplyResultsColumnLayout();
            QueueResultsListRepaint();
        };

        split.SplitterMoved += (_, _) => _initialSplitLayoutApplied = true;

        Resize += (_, _) => QueueResultsListRepaint();
        ClientSizeChanged += (_, _) => QueueResultsListRepaint();

        AcceptButton = searchButton;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        TryUseImmersiveDarkTitleBar();
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        QueueResultsListRepaint();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        QueueResultsListRepaint();
    }

    private void ApplySplitLayout(SplitContainer split, bool force)
    {
        const int panel1MinSize = 520;
        const int panel2MinSize = 220;

        if (split.Width <= panel1MinSize + panel2MinSize + split.SplitterWidth)
        {
            return;
        }

        split.Panel1MinSize = panel1MinSize;
        split.Panel2MinSize = panel2MinSize;

        if (_initialSplitLayoutApplied && !force)
        {
            return;
        }

        var maxDistance = split.Width - panel2MinSize - split.SplitterWidth;
        var preferredDistance = (int)(split.Width * 0.68);
        var distance = Math.Min(preferredDistance, maxDistance);
        distance = Math.Max(panel1MinSize, distance);

        if (split.SplitterDistance != distance)
        {
            split.SplitterDistance = distance;
        }

        _initialSplitLayoutApplied = true;
    }

    private void QueueResultsListRepaint()
    {
        if (_resultsListView is null)
        {
            return;
        }

        if (_resultsListView is RepaintSafeListView repaintSafeListView)
        {
            repaintSafeListView.QueueFullRepaint();
            return;
        }

        if (_resultsListView.IsHandleCreated && !_resultsListView.IsDisposed)
        {
            _resultsListView.BeginInvoke(new Action(() =>
            {
                if (!_resultsListView.IsDisposed)
                {
                    _resultsListView.Invalidate(true);
                    _resultsListView.Update();
                }
            }));
        }
    }

    private void ResultsListView_ColumnWidthChanging(object sender, ColumnWidthChangingEventArgs e)
    {
        if (_adjustingResultColumns || _resultsListView.Columns.Count <= ResultFillerColumnIndex)
        {
            return;
        }

        if (e.ColumnIndex >= ResultDataColumnCount)
        {
            e.Cancel = true;
            e.NewWidth = CalculateResultsFillerColumnWidth();
            return;
        }

        SetResultsFillerColumnWidth(CalculateResultsFillerColumnWidth(e.ColumnIndex, e.NewWidth));
    }

    private void ApplyResultsColumnLayout()
    {
        if (_adjustingResultColumns || _resultsListView.Columns.Count <= ResultFillerColumnIndex || _resultsListView.ClientSize.Width <= 0)
        {
            return;
        }

        var availableWidth = Math.Max(1, _resultsListView.ClientSize.Width - 4);
        var dataWidth = Math.Max(1, availableWidth - ResultMinFillerColumnWidth);

        var compatibilityWidth = 120;
        var depsWidth = 58;
        var typeWidth = 66;
        var gameWidth = Math.Max(145, Math.Min(220, dataWidth * 24 / 100));
        var descriptionWidth = dataWidth - gameWidth - compatibilityWidth - depsWidth - typeWidth;

        if (descriptionWidth < 220)
        {
            descriptionWidth = 220;
            gameWidth = Math.Max(120, dataWidth - compatibilityWidth - depsWidth - typeWidth - descriptionWidth);
        }

        try
        {
            _adjustingResultColumns = true;

            _resultsListView.Columns[0].Width = gameWidth;
            _resultsListView.Columns[1].Width = compatibilityWidth;
            _resultsListView.Columns[2].Width = depsWidth;
            _resultsListView.Columns[3].Width = typeWidth;
            _resultsListView.Columns[4].Width = Math.Max(120, descriptionWidth);
        }
        finally
        {
            _adjustingResultColumns = false;
        }

        FillResultsFillerColumn();
    }

    private void FillResultsFillerColumn()
    {
        if (_adjustingResultColumns || _resultsListView.Columns.Count <= ResultFillerColumnIndex || _resultsListView.ClientSize.Width <= 0)
        {
            return;
        }

        SetResultsFillerColumnWidth(CalculateResultsFillerColumnWidth());
    }

    private int CalculateResultsFillerColumnWidth(int changingColumnIndex = -1, int changingColumnWidth = 0)
    {
        var availableWidth = Math.Max(1, _resultsListView.ClientSize.Width - 4);
        var dataColumnsWidth = 0;

        for (var index = 0; index < ResultDataColumnCount; index++)
        {
            dataColumnsWidth += index == changingColumnIndex
                ? Math.Max(0, changingColumnWidth)
                : _resultsListView.Columns[index].Width;
        }

        return Math.Max(ResultMinFillerColumnWidth, availableWidth - dataColumnsWidth);
    }

    private void SetResultsFillerColumnWidth(int width)
    {
        if (_resultsListView.Columns.Count <= ResultFillerColumnIndex)
        {
            return;
        }

        try
        {
            _adjustingResultColumns = true;

            if (_resultsListView.Columns[ResultFillerColumnIndex].Width != width)
            {
                _resultsListView.Columns[ResultFillerColumnIndex].Width = width;
                _resultsListView.Invalidate();
            }
        }
        finally
        {
            _adjustingResultColumns = false;
        }
    }

    private void LoadCatalog()
    {
        try
        {
            UseWaitCursor = true;
            _statusLabel.Text = "Loading LiveSplit autosplitter catalog...";
            Application.DoEvents();

            _catalog = _service.LoadCatalog(_catalogUrlTextBox.Text.Trim()).ToArray();
            _statusLabel.Text = $"Catalog loaded: {_catalog.Length} autosplitter(s).";
            SearchCatalog();
        }
        catch (Exception ex)
        {
            ShowError("Load catalog failed", ex);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void SearchCatalog()
    {
        try
        {
            if (_catalog.Length == 0)
            {
                LoadCatalog();
                return;
            }

            var results = _service.Search(_catalog, _searchTextBox.Text.Trim(), 200);

            _resultsListView.BeginUpdate();

            try
            {
                _resultsListView.Items.Clear();

                foreach (var entry in results)
                {
                    var item = new ListViewItem(entry.PrimaryGame ?? "")
                    {
                        Tag = entry,
                        ForeColor = TextForeground
                    };
                    item.SubItems.Add(entry.CompatibilityLabel);
                    item.SubItems.Add(entry.DependencyLabel);
                    item.SubItems.Add(string.IsNullOrWhiteSpace(entry.Type) ? "—" : entry.Type);
                    item.SubItems.Add(entry.Description ?? "");
                    item.SubItems.Add("");

                    if (entry.IsUnsupported)
                    {
                        item.ForeColor = DangerForeground;
                    }
                    else if (entry.HasCompatibilityWarning)
                    {
                        item.ForeColor = WarningForeground;
                    }

                    _resultsListView.Items.Add(item);
                }
            }
            finally
            {
                _resultsListView.EndUpdate();
            }

            _statusLabel.Text = $"Found {results.Count} result(s).";
            QueueResultsListRepaint();

            if (_resultsListView.Items.Count > 0)
            {
                _resultsListView.Items[0].Selected = true;
                _resultsListView.Items[0].Focused = true;
            }
            else
            {
                _detailsTextBox.Clear();
            }
        }
        catch (Exception ex)
        {
            ShowError("Search failed", ex);
        }
    }

    private void InstallSelected(bool configureAfterInstall)
    {
        var entry = GetSelectedEntry();

        if (entry == null)
        {
            MessageBox.Show(this, "Select autosplitter first.", "No selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var runId = _runIdTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(runId))
        {
            MessageBox.Show(this, "RunId is required.", "RunId", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (entry.IsUnsupported)
        {
            var proceed = MessageBox.Show(
                this,
                entry.CompatibilityNote + "\r\n\r\nInstall assets anyway?",
                "Unsupported autosplitter",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (proceed != DialogResult.Yes)
            {
                return;
            }
        }
        else if (entry.HasCompatibilityWarning)
        {
            var proceed = MessageBox.Show(
                this,
                entry.CompatibilityNote + "\r\n\r\nInstall and configure anyway?",
                "ASL helper compatibility warning",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (proceed != DialogResult.Yes)
            {
                return;
            }
        }

        var runsRoot = _runsRootTextBox.Text.Trim();
        var destinationDir = Path.Combine(Path.GetFullPath(runsRoot), runId);
        var settingsDecision = ConfirmExistingSettingsBeforeInstall(destinationDir);

        if (settingsDecision.Cancelled)
        {
            _statusLabel.Text = "Install cancelled.";
            return;
        }

        try
        {
            UseWaitCursor = true;
            _statusLabel.Text = "Installing autosplitter assets...";
            Application.DoEvents();

            _lastInstallResult = _service.Install(new AutoSplitterInstallRequest
            {
                Entry = entry,
                RunId = runId,
                RunsRoot = runsRoot,
                SplitsPath = _splitsPathTextBox.Text.Trim(),
                PlaceholderSplitCount = (int)_placeholderCountBox.Value,
                ClearExistingSettings = false
            });

            _statusLabel.Text = "Installed. See Install summary below: " + _lastInstallResult.DestinationDir;
            _installSummaryTextBox.Text = BuildInstallDetails(entry, _lastInstallResult, settingsDecision.Message);

            if (configureAfterInstall)
            {
                OpenConfigure(_lastInstallResult.DestinationDir);
            }
        }
        catch (Exception ex)
        {
            ShowError("Install failed", ex);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void OpenConfigureForRunId()
    {
        var assetsDir = GetAssetsDirForCurrentRunId();

        if (!HasRequiredAssets(assetsDir))
        {
            MessageBox.Show(this, "autosplitter.asl and splits.lss were not found in:\r\n" + assetsDir, "Assets not found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        OpenConfigure(assetsDir);
    }

    private void OpenConfigure(string assetsDir)
    {
        StartHostProcess("--configure", assetsDir);
    }

    private void OpenRunFolder()
    {
        var folder = _lastInstallResult?.DestinationDir;

        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = GetAssetsDirForCurrentRunId();
        }

        Directory.CreateDirectory(folder);
        Process.Start("explorer.exe", folder);
    }

    private void BrowseSplits()
    {
        using (var dialog = new OpenFileDialog())
        {
            dialog.Title = "Select LiveSplit splits.lss";
            dialog.Filter = "LiveSplit splits (*.lss)|*.lss|All files (*.*)|*.*";

            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _splitsPathTextBox.Text = dialog.FileName;
            }
        }
    }

    private void RenderSelectedDetails()
    {
        var entry = GetSelectedEntry();

        if (entry == null)
        {
            _detailsTextBox.Clear();
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine(entry.PrimaryGame);
        builder.AppendLine(new string('-', Math.Max(1, entry.PrimaryGame?.Length ?? 1)));
        builder.AppendLine("Compatibility: " + entry.CompatibilityLabel);
        builder.AppendLine("Dependencies:   " + entry.DependencyLabel);
        builder.AppendLine("Type:           " + (string.IsNullOrWhiteSpace(entry.Type) ? "—" : entry.Type));
        builder.AppendLine();
        builder.AppendLine("Compatibility note:");
        builder.AppendLine(entry.CompatibilityNote);
        builder.AppendLine();

        if (entry.Games.Count > 1)
        {
            builder.AppendLine("Aliases:");
            foreach (var alias in entry.Games.Skip(1))
            {
                builder.AppendLine("- " + alias);
            }
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(entry.Description))
        {
            builder.AppendLine("Description:");
            builder.AppendLine(entry.Description);
            builder.AppendLine();
        }

        builder.AppendLine("URLs:");
        foreach (var url in entry.Urls)
        {
            builder.AppendLine("- " + url);
        }

        _detailsTextBox.Text = builder.ToString();
    }

    private SettingsInstallDecision ConfirmExistingSettingsBeforeInstall(string destinationDir)
    {
        var settingsPath = Path.Combine(destinationDir, "asl-settings.json");

        if (!File.Exists(settingsPath))
        {
            return SettingsInstallDecision.Continue("No existing asl-settings.json found. Configure will create it if the ASL exposes settings.");
        }

        var choice = MessageBox.Show(
            this,
            "Existing asl-settings.json was found for this RunId.\r\n\r\n" +
            "Yes = keep current settings\r\n" +
            "No = back up and reset settings\r\n" +
            "Cancel = cancel install\r\n\r\n" +
            settingsPath,
            "Existing ASL settings",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);

        if (choice == DialogResult.Cancel)
        {
            return SettingsInstallDecision.Cancel();
        }

        if (choice == DialogResult.Yes)
        {
            return SettingsInstallDecision.Continue("Existing asl-settings.json kept.");
        }

        var backupPath = GetUniqueSettingsBackupPath(settingsPath);
        File.Move(settingsPath, backupPath);

        return SettingsInstallDecision.Continue("Existing asl-settings.json backed up and reset: " + backupPath);
    }

    private static string GetUniqueSettingsBackupPath(string settingsPath)
    {
        var directory = Path.GetDirectoryName(settingsPath) ?? ".";
        var baseName = Path.GetFileName(settingsPath) + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupPath = Path.Combine(directory, baseName);

        for (var index = 2; File.Exists(backupPath); index++)
        {
            backupPath = Path.Combine(directory, baseName + "-" + index);
        }

        return backupPath;
    }

    private string BuildInstallDetails(AutoSplitterCatalogEntry entry, AutoSplitterInstallResult result, string settingsMessage)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Installed autosplitter assets");
        builder.AppendLine("-----------------------------");
        builder.AppendLine("Game:        " + entry.PrimaryGame);
        builder.AppendLine("Compatibility: " + entry.CompatibilityLabel);
        builder.AppendLine("RunId:       " + result.RunId);
        builder.AppendLine("Destination: " + result.DestinationDir);
        builder.AppendLine("ASL:         " + result.AutosplitterPath);
        builder.AppendLine("LSS:         " + result.SplitsPath);
        builder.AppendLine();
        builder.AppendLine("Messages:");

        foreach (var message in result.Messages)
        {
            builder.AppendLine("- " + message);
        }

        if (!string.IsNullOrWhiteSpace(settingsMessage))
        {
            builder.AppendLine("Settings:");
            builder.AppendLine("- " + settingsMessage);
            builder.AppendLine();
        }

        builder.AppendLine();
        builder.AppendLine("Next:");
        builder.AppendLine("- Press Open settings for RunId, save asl-settings.json if needed, then use Preview / Generate splits.lss.");
        builder.AppendLine("- Keep placeholder splits.lss only as a fallback if ASL settings do not expose usable split names.");
        builder.AppendLine("- Start ASL Host from the launcher with this RunId.");

        if (entry.HasCompatibilityWarning)
        {
            builder.AppendLine();
            builder.AppendLine("Warning:");
            builder.AppendLine("- Helper/dependency ASLs are best-effort. Test x86/x64 before tournament use.");
        }

        return builder.ToString();
    }

    private AutoSplitterCatalogEntry GetSelectedEntry()
    {
        return _resultsListView.SelectedItems.Count == 0
            ? null
            : _resultsListView.SelectedItems[0].Tag as AutoSplitterCatalogEntry;
    }

    private string GetAssetsDirForCurrentRunId()
    {
        return Path.Combine(Path.GetFullPath(_runsRootTextBox.Text.Trim()), _runIdTextBox.Text.Trim());
    }

    private static bool HasRequiredAssets(string assetsDir)
    {
        return File.Exists(Path.Combine(assetsDir, "autosplitter.asl")) &&
               File.Exists(Path.Combine(assetsDir, "splits.lss"));
    }

    private void StartHostProcess(string modeArg, string assetsDir)
    {
        var exePath = Application.ExecutablePath;
        var args = $"{modeArg} --assetsDir=\"{assetsDir}\" --bridgeUrl=\"{_options.BridgeUrl}\"";

        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
            UseShellExecute = true
        });
    }

    private static Control CreateHeader()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = HeaderBackground,
            Padding = new Padding(16, 14, 16, 14),
            Margin = new Padding(0, 0, 0, 14)
        };

        var title = new Label
        {
            AutoSize = true,
            Text = "ASL Catalog & Settings",
            Font = new Font("Segoe UI", 18F, FontStyle.Bold),
            ForeColor = TextForeground,
            BackColor = HeaderBackground,
            Margin = new Padding(0, 0, 0, 2)
        };

        var subtitle = new Label
        {
            AutoSize = true,
            Text = "Search LiveSplit autosplitters, install run assets, save settings, then test before tournament use.",
            Font = new Font("Segoe UI", 9.5F),
            ForeColor = MutedForeground,
            BackColor = HeaderBackground,
            Margin = new Padding(1, 0, 0, 0)
        };

        panel.Controls.Add(title, 0, 0);
        panel.Controls.Add(subtitle, 0, 1);

        return panel;
    }

    private static Panel CreateCard(Control content)
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = WindowBackground,
            ForeColor = TextForeground,
            FillColor = CardBackground,
            BorderColor = AutosplitterCatalogForm.BorderColor,
            BorderRadius = CornerRadius,
            Padding = new Padding(14),
            Margin = new Padding(0, 0, 0, 12)
        };

        content.Dock = DockStyle.Fill;
        card.Controls.Add(content);

        return card;
    }

    private static TextBox CreateTextBox(string text)
    {
        return new TextBox
        {
            Dock = DockStyle.Fill,
            Text = text,
            BackColor = CardBackgroundAlt,
            ForeColor = TextForeground,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 2, 8, 6)
        };
    }

    private static Button CreateButton(string text, ButtonVariant variant)
    {
        var normalBackColor = variant switch
        {
            ButtonVariant.Primary => AccentBackground,
            ButtonVariant.Danger => DangerBackground,
            _ => SecondaryBackground
        };

        var textSize = TextRenderer.MeasureText(text, new Font("Segoe UI", 9F, FontStyle.Bold));

        return new RoundedButton
        {
            Text = text,
            AutoSize = false,
            Size = new Size(textSize.Width + 34, 38),
            Padding = new Padding(12, 0, 12, 0),
            Margin = new Padding(0, 0, 8, 0),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            BorderRadius = CornerRadius,
            BackColor = normalBackColor,
            NormalBackColor = normalBackColor,
            HoverBackColor = variant == ButtonVariant.Danger
                ? Color.FromArgb(188, 60, 60)
                : Color.FromArgb(63, 123, 255),
            PressedBackColor = variant == ButtonVariant.Danger
                ? Color.FromArgb(132, 42, 42)
                : Color.FromArgb(38, 86, 210)
        };
    }

    private static Label CreateFormLabel(string text)
    {
        return new Label
        {
            AutoSize = true,
            Text = text,
            ForeColor = MutedForeground,
            BackColor = CardBackground,
            Margin = new Padding(0, 7, 10, 0)
        };
    }

    private static void AddLabeledControl(TableLayoutPanel grid, int row, string label, Control control)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        grid.Controls.Add(CreateFormLabel(label), 0, row);
        grid.Controls.Add(control, 1, row);
        grid.SetColumnSpan(control, 3);
    }

    private static void DrawListViewColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
    {
        using (var background = new SolidBrush(CardBackground))
        using (var border = new Pen(BorderColor))
        {
            e.Graphics.FillRectangle(background, e.Bounds);
            e.Graphics.DrawLine(border, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        }

        var listView = (ListView)sender;
        var textBounds = e.Bounds;
        textBounds.Inflate(-8, 0);

        TextRenderer.DrawText(
            e.Graphics,
            e.Header.Text,
            listView.Font,
            textBounds,
            MutedForeground,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private static void DrawListViewSubItem(object sender, DrawListViewSubItemEventArgs e)
    {
        var listView = (ListView)sender;
        var selected = e.Item.Selected;
        var backgroundColor = selected
            ? AccentBackground
            : CardBackgroundAlt;

        using (var background = new SolidBrush(backgroundColor))
        {
            e.Graphics.FillRectangle(background, e.Bounds);
        }

        var textColor = selected
            ? Color.White
            : e.Item.ForeColor;

        if (textColor.IsEmpty)
        {
            textColor = TextForeground;
        }

        var textBounds = e.Bounds;
        textBounds.Inflate(-8, 0);

        TextRenderer.DrawText(
            e.Graphics,
            e.SubItem.Text,
            listView.Font,
            textBounds,
            textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void TryUseImmersiveDarkTitleBar()
    {
        if (Handle == IntPtr.Zero)
        {
            return;
        }

        var useDarkMode = 1;

        // 20 works on current Windows 10/11 builds, 19 is an older fallback.
        TrySetDwmAttribute(Handle, 20, useDarkMode);
        TrySetDwmAttribute(Handle, 19, useDarkMode);

        // Windows 11 caption/text color fallback. Safe to ignore on older builds.
        TrySetDwmAttribute(Handle, 35, ToColorRef(TitleBarBackground));
        TrySetDwmAttribute(Handle, 36, ToColorRef(TextForeground));
    }

    private static void TrySetDwmAttribute(IntPtr handle, int attribute, int value)
    {
        try
        {
            DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
        }
        catch
        {
            // Non-critical visual enhancement.
        }
    }

    private static int ToColorRef(Color color)
    {
        return color.R | (color.G << 8) | (color.B << 16);
    }

    private static void TryApplyDarkControlTheme(Control control)
    {
        try
        {
            if (control.Handle != IntPtr.Zero)
            {
                SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
            }
        }
        catch
        {
            // Native scrollbars are cosmetic only.
        }
    }

    private void ShowError(string title, Exception ex)
    {
        _statusLabel.Text = title + ": " + ex.Message;
        MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

    private sealed class RepaintSafeListView : ListView
    {
        private const int LvmFirst = 0x1000;
        private const int LvmSetExtendedListViewStyle = LvmFirst + 54;
        private const int LvmGetExtendedListViewStyle = LvmFirst + 55;
        private const int LvsExDoubleBuffer = 0x00010000;

        private bool _repaintQueued;

        public RepaintSafeListView()
        {
            DoubleBuffered = true;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true);
        }

        public void QueueFullRepaint()
        {
            if (!IsHandleCreated || IsDisposed || Disposing || _repaintQueued)
            {
                return;
            }

            _repaintQueued = true;

            BeginInvoke(new Action(() =>
            {
                if (IsDisposed || Disposing)
                {
                    return;
                }

                _repaintQueued = false;
                Invalidate(true);
                Update();
            }));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            EnableNativeDoubleBuffer();
            QueueFullRepaint();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            QueueFullRepaint();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            QueueFullRepaint();
        }

        private void EnableNativeDoubleBuffer()
        {
            if (Handle == IntPtr.Zero)
            {
                return;
            }

            var currentStyle = SendMessage(Handle, LvmGetExtendedListViewStyle, IntPtr.Zero, IntPtr.Zero).ToInt64();
            var newStyle = new IntPtr(currentStyle | LvsExDoubleBuffer);

            SendMessage(Handle, LvmSetExtendedListViewStyle, IntPtr.Zero, newStyle);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }

    private sealed class RoundedPanel : Panel
    {
        public int BorderRadius { get; set; } = CornerRadius;
        public Color BorderColor { get; set; } = AutosplitterCatalogForm.BorderColor;
        public Color FillColor { get; set; } = CardBackground;

        public RoundedPanel()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.ResizeRedraw |
                ControlStyles.OptimizedDoubleBuffer,
                true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var background = new SolidBrush(Parent?.BackColor ?? BackColor))
            {
                e.Graphics.FillRectangle(background, ClientRectangle);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            var bounds = ClientRectangle;
            bounds.Width -= 1;
            bounds.Height -= 1;

            using (var path = CreateRoundedRectanglePath(bounds, BorderRadius))
            using (var fill = new SolidBrush(FillColor))
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
        public Color NormalBackColor { get; set; } = SecondaryBackground;
        public Color HoverBackColor { get; set; } = Color.FromArgb(63, 123, 255);
        public Color PressedBackColor { get; set; } = Color.FromArgb(38, 86, 210);

        public RoundedButton()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.ResizeRedraw |
                ControlStyles.OptimizedDoubleBuffer,
                true);

            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            MinimumSize = new Size(0, 38);
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

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            using (var background = new SolidBrush(Parent?.BackColor ?? BackColor))
            {
                pevent.Graphics.FillRectangle(background, ClientRectangle);
            }
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

            e.Graphics.Clear(Parent?.BackColor ?? Color.FromArgb(15, 18, 26));
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

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return path;
        }

        if (diameter > bounds.Width)
        {
            diameter = bounds.Width;
        }

        if (diameter > bounds.Height)
        {
            diameter = bounds.Height;
        }

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

    private sealed class SettingsInstallDecision
    {
        private SettingsInstallDecision(bool cancelled, string message)
        {
            Cancelled = cancelled;
            Message = message;
        }

        public bool Cancelled { get; }
        public string Message { get; }

        public static SettingsInstallDecision Continue(string message)
        {
            return new SettingsInstallDecision(false, message);
        }

        public static SettingsInstallDecision Cancel()
        {
            return new SettingsInstallDecision(true, string.Empty);
        }
    }

    private enum ButtonVariant
    {
        Primary,
        Secondary,
        Danger
    }
}
