readonly record struct Reading(string Label, string Value);

sealed record DashboardStatus(string State, string Reason, string Explanation, IReadOnlyList<Reading> Gpu,
    IReadOnlyList<Reading> Workers, IReadOnlyList<Reading> Diagnostics,
    bool CanPause, bool CanResume, bool CanQuit, bool CanCancel, bool CanRepair);

sealed class StatusWindow : AppWindow
{
    readonly Panel header, headerRule, statusRule, columnRule, footerRule;
    readonly Panel overview, diagnostics;
    readonly Control workers;
    readonly ChoiceButton overviewTab, workersTab;
    readonly Dictionary<AppearanceMode, ChoiceButton> appearanceOptions = [];
    readonly Label appearanceLabel, state, reason;
    readonly StatusBadge badge = new() { Size = new Size(32, 32), Anchor = AnchorStyles.Left };
    readonly ReadingRows gpu = new(3), workerSummary = new(4, rightAlign: true), diagnosticRows = new(5);
    readonly UiButton admission, repair, quit, cancel, diagnosticsButton;
    readonly ToolTip details = new();
    readonly CheckBox startup;
    Control? current;
    bool updatingSelection;
    bool offerResume;

    public StatusWindow(Appearance theme, Icon appIcon, Action pauseAction, Action resumeAction,
        Action quitAction, Action cancelAction, Action openLogs, Action repairAction, Control workers) : base(theme)
    {
        this.workers = workers;
        Text = "GPU Admission";
        Icon = appIcon;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 475);
        MinimumSize = new Size(664, 482);

        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 98));

        header = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(22, 0, 18, 0) };
        headerRule = new Panel { Dock = DockStyle.Bottom, Height = 1 };
        var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        overviewTab = new ChoiceButton { Text = "Overview", Name = "OverviewTab", Navigation = true, Size = new Size(106, 54) };
        workersTab = new ChoiceButton { Text = "Workers", Name = "WorkersTab", Navigation = true, Size = new Size(100, 54) };
        overviewTab.CheckedChanged += (_, _) => { if (!updatingSelection && overviewTab.Checked) ShowOverview(); };
        workersTab.CheckedChanged += (_, _) => { if (!updatingSelection && workersTab.Checked) ShowWorkers(); };
        var navigation = ButtonRow();
        navigation.Margin = new Padding(0, 9, 0, 0);
        navigation.Controls.AddRange([overviewTab, workersTab]);
        toolbar.Controls.Add(navigation, 0, 0);
        var picker = ButtonRow();
        picker.AccessibleName = "Appearance";
        picker.Anchor = AnchorStyles.Right;
        appearanceLabel = new Label
        {
            Text = "Appearance", AutoSize = true, Margin = new Padding(0, 8, 12, 0),
            UseMnemonic = false
        };
        picker.Controls.Add(appearanceLabel);
        foreach (var mode in new[] { AppearanceMode.System, AppearanceMode.Light, AppearanceMode.Dark })
        {
            var option = new ChoiceButton { Text = mode.ToString(), Name = $"{mode}Appearance", Size = new Size(71, 35) };
            option.Margin = new Padding(1, 0, 0, 0);
            if (mode == AppearanceMode.System) option.AccessibleDescription = "Follow the Windows app theme.";
            option.CheckedChanged += (_, _) =>
            {
                if (!option.Checked || updatingSelection) return;
                try { Theme.Set(mode); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    SyncSelection();
                    MessageBox.Show(this, e.Message, "Could not save appearance", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            appearanceOptions.Add(mode, option);
            picker.Controls.Add(option);
        }
        toolbar.Controls.Add(picker, 1, 0);
        header.Controls.Add(toolbar);
        header.Controls.Add(headerRule);
        shell.Controls.Add(header, 0, 0);

        var pages = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        overview = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22, 0, 22, 0) };
        var splitLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        splitLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 106));
        splitLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
        splitLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var status = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty,
            Padding = new Padding(7, 0, 0, 0)
        };
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        status.Controls.Add(badge, 0, 0);
        var message = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 19, 16, 17)
        };
        message.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        message.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        state = TextLabel("Starting GPU Admission", UiStyle.Heading);
        state.Name = "AdmissionStatus";
        reason = TextLabel("Waiting for the guard", UiStyle.Body);
        reason.Name = "HoldReason";
        reason.AutoEllipsis = true;
        message.Controls.Add(state, 0, 0);
        message.Controls.Add(reason, 0, 1);
        status.Controls.Add(message, 1, 0);
        repair = ActionButton("Repair GPU setup", "RepairGpu", repairAction, 183, primary: true);
        repair.Anchor = AnchorStyles.Right;
        repair.Visible = false;
        status.Controls.Add(repair, 2, 0);
        splitLayout.Controls.Add(status, 0, 0);
        statusRule = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        splitLayout.Controls.Add(statusRule, 0, 1);

        var parts = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty,
            Padding = new Padding(8, 17, 10, 16)
        };
        parts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 49));
        parts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
        parts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 51));
        parts.Controls.Add(Section("GPU readings", gpu, new Padding(0, 2, 35, 0)), 0, 0);
        columnRule = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        parts.Controls.Add(columnRule, 1, 0);
        parts.Controls.Add(Section("Workers", workerSummary, new Padding(29, 2, 0, 0)), 2, 0);
        splitLayout.Controls.Add(parts, 0, 2);
        overview.Controls.Add(splitLayout);
        pages.Controls.Add(overview);

        diagnostics = new Panel { Dock = DockStyle.Fill, Padding = new Padding(28, 18, 28, 8), Visible = false };
        var diagnosticsPanel = Section("Diagnostics", diagnosticRows, Padding.Empty);
        diagnosticsPanel.RowCount = 3;
        diagnosticsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        startup = new CheckBox
        {
            Name = "StartAtSignIn", Text = "Start GPU Admission when I sign in",
            Dock = DockStyle.Fill, Checked = StartupPreference.Enabled
        };
        startup.CheckedChanged += (_, _) =>
        {
            if (!startup.Enabled) return;
            try { StartupPreference.Set(startup.Checked); }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
            {
                startup.Enabled = false;
                startup.Checked = StartupPreference.Enabled;
                MessageBox.Show(this, e.Message, "Could not change startup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        diagnosticsPanel.Controls.Add(startup, 0, 2);
        diagnostics.Controls.Add(diagnosticsPanel);
        pages.Controls.Add(diagnostics);
        workers.Visible = false;
        pages.Controls.Add(workers);
        shell.Controls.Add(pages, 0, 1);

        var footer = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(22, 0, 18, 0) };
        footerRule = new Panel { Dock = DockStyle.Top, Height = 1 };
        var buttonBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        buttonBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttonBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttonBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var left = ButtonRow();
        left.Anchor = AnchorStyles.Left;
        left.Controls.Add(ActionButton("Open logs", "OpenLogs", openLogs, 120));
        diagnosticsButton = ActionButton("Diagnostics", "Diagnostics", () =>
            ShowPage(ReferenceEquals(current, diagnostics) ? overview : diagnostics), 132);
        left.Controls.Add(diagnosticsButton);
        buttonBar.Controls.Add(left, 0, 0);

        var right = ButtonRow();
        right.Anchor = AnchorStyles.Right;
        admission = ActionButton("Resume", "AdmissionAction", () =>
        {
            if (offerResume) resumeAction();
            else pauseAction();
        }, 116, primary: true);
        admission.Enabled = false;
        cancel = ActionButton("Cancel quit", "CancelQuit", cancelAction, 116);
        cancel.Visible = false;
        quit = ActionButton("Quit", "Quit", quitAction, 94);
        quit.Margin = Padding.Empty;
        right.Controls.AddRange([admission, cancel, quit]);
        buttonBar.Controls.Add(right, 2, 0);
        footer.Controls.Add(buttonBar);
        footer.Controls.Add(footerRule);
        shell.Controls.Add(footer, 0, 2);
        Controls.Add(shell);
        ShowOverview();
    }

    public void ShowWorkers() => ShowPage(workers);
    public void ShowOverview() => ShowPage(overview);

    void ShowPage(Control page)
    {
        if (current == page) return;
        if (current is not null) current.Visible = false;
        current = page;
        current.Visible = true;
        current.BringToFront();
        diagnosticsButton.Text = current == diagnostics ? "Overview" : "Diagnostics";
        SyncSelection();
    }

    void SyncSelection()
    {
        updatingSelection = true;
        try
        {
            overviewTab.Checked = current == overview;
            workersTab.Checked = current == workers;
            appearanceOptions[Theme.Mode].Checked = true;
        }
        finally { updatingSelection = false; }
    }

    protected override void RefreshColors()
    {
        base.RefreshColors();
        var colors = Theme.Colors;
        header.BackColor = colors.Chrome;
        headerRule.BackColor = statusRule.BackColor = columnRule.BackColor = footerRule.BackColor = colors.Divider;
        appearanceLabel.ForeColor = reason.ForeColor = colors.Muted;
        appearanceLabel.BackColor = colors.Chrome;
        badge.Colors = colors;
        gpu.MuteValues(colors);
        workerSummary.MuteValues(colors);
        diagnosticRows.MuteValues(colors);
        SyncSelection();
    }

    public void UpdateReadings(DashboardStatus report)
    {
        bool blocked = report.Reason is "gpu-unavailable" or "monitor-only" or "owner-uncertain" or "load-cooldown";
        state.Text = report.State switch
        {
            "Hold" => "Admission paused",
            "Auto" => blocked ? "Admission on hold" : "Admission enabled",
            "Pausing" => "Pausing admission",
            "Resuming" => "Resuming admission",
            "Draining" => "Finishing current work",
            _ => "Status unavailable"
        };
        if (reason.Text != report.Explanation)
        {
            reason.Text = report.Explanation;
            details.SetToolTip(reason, report.Explanation);
        }
        bool paused = report.State != "Auto" || blocked;
        if (badge.Paused != paused)
        {
            badge.Paused = paused;
            badge.Invalidate();
        }
        gpu.SetReadings(report.Gpu);
        workerSummary.SetReadings(report.Workers.Count == 0 ? [new("No worker status", "")] : report.Workers);
        diagnosticRows.SetReadings(report.Diagnostics);
        offerResume = !report.CanPause || report.State is "Hold" or "Resuming";
        admission.Text = offerResume ? "Resume" : "Pause";
        admission.Enabled = offerResume ? report.CanResume : report.CanPause;
        admission.AccessibleDescription = admission.Enabled ? "" : report.Explanation;
        admission.Visible = !report.CanCancel;
        repair.Visible = report.CanRepair;
        repair.Enabled = report.CanRepair;
        cancel.Visible = report.CanCancel;
        quit.Enabled = report.CanQuit;
    }

    static FlowLayoutPanel ButtonRow() => new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
        Margin = Padding.Empty
    };

    static UiButton ActionButton(string text, string name, Action action, int width, bool primary = false)
    {
        var button = new UiButton
        {
            Text = text, Name = name, Primary = primary, MinimumSize = new Size(width, 40),
            Margin = new Padding(0, 0, 12, 0)
        };
        button.Click += (_, _) => action();
        return button;
    }

    static Label TextLabel(string text, Font font) => new()
    {
        Text = text, Font = font, Margin = Padding.Empty, UseMnemonic = false, Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft
    };

    static TableLayoutPanel Section(string title, Control rows, Padding padding)
    {
        var section = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = padding
        };
        section.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        section.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        section.Controls.Add(TextLabel(title, UiStyle.Section), 0, 0);
        section.Controls.Add(rows, 0, 1);
        return section;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) details.Dispose();
        base.Dispose(disposing);
    }

    sealed class ReadingRows : TableLayoutPanel
    {
        readonly Label[] names, values;

        public ReadingRows(int capacity, bool rightAlign = false)
        {
            Dock = DockStyle.Fill;
            Margin = Padding.Empty;
            AutoScroll = true;
            ColumnCount = 2;
            RowCount = capacity;
            int labelWidth = rightAlign ? 77 : capacity == 5 ? 36 : 58;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, labelWidth));
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100 - labelWidth));
            names = new Label[capacity];
            values = new Label[capacity];
            for (int i = 0; i < capacity; i++)
            {
                RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
                names[i] = TextLabel("", UiStyle.Body);
                names[i].AutoEllipsis = true;
                values[i] = TextLabel("", UiStyle.Body);
                values[i].AutoEllipsis = true;
                if (rightAlign) values[i].TextAlign = ContentAlignment.MiddleRight;
                Controls.Add(names[i], 0, i);
                Controls.Add(values[i], 1, i);
            }
        }

        public void SetReadings(IReadOnlyList<Reading> rows)
        {
            for (int i = 0; i < names.Length; i++)
            {
                names[i].Text = i < rows.Count ? rows[i].Label : "";
                values[i].Text = i < rows.Count ? rows[i].Value : "";
            }
        }

        public void MuteValues(Palette colors)
        {
            foreach (var value in values) value.ForeColor = colors.Muted;
        }
    }
}
