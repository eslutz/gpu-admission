using System.Drawing.Drawing2D;

sealed class StatusWindow : Form
{
    static readonly Color Background = Color.FromArgb(16, 22, 32);
    static readonly Color Card = Color.FromArgb(25, 35, 49);
    static readonly Color Ink = Color.FromArgb(240, 246, 252);
    static readonly Color Muted = Color.FromArgb(175, 193, 211);
    static readonly Color Cyan = Color.FromArgb(111, 230, 223);
    static readonly Color Amber = Color.FromArgb(255, 204, 124);
    readonly Font heading = new("Segoe UI", 21, FontStyle.Bold);
    readonly Font strong = new("Segoe UI", 11, FontStyle.Bold);
    readonly Font body = new("Segoe UI", 10);
    readonly Label state, reason, gpu, workloads, resources, history;
    readonly Button pause, resume, quit, cancel;

    public StatusWindow(Icon appIcon, Action pauseAction, Action resumeAction,
        Action quitAction, Action cancelAction, Action openLogs)
    {
        Text = "GPU Admission";
        Icon = appIcon;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(640, 510);
        MinimumSize = new Size(610, 500);
        BackColor = Background;
        ForeColor = Ink;
        Font = body;

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22,16,22,16),
            ColumnCount = 2, RowCount = 5 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 126));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 51));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        Controls.Add(grid);

        var hero = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        hero.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
        hero.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        hero.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        hero.Controls.Add(TextLabel("GPU Admission", heading, Ink), 0, 0);
        state = TextLabel("STARTING", strong, Amber);
        reason = TextLabel("Waiting for the guard", body, Muted);
        hero.Controls.Add(state, 0, 1);
        hero.Controls.Add(reason, 0, 2);
        grid.Controls.Add(hero, 0, 0);
        grid.SetColumnSpan(hero, 2);

        gpu = AddCard(grid, 0, 1, "GPU");
        workloads = AddCard(grid, 1, 1, "Workloads");
        resources = AddCard(grid, 0, 2, "Process usage");
        history = AddCard(grid, 1, 2, "Diagnostics");
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false,
            Padding = new Padding(0, 8, 0, 0), Margin = Padding.Empty };
        grid.Controls.Add(actions, 0, 3);
        grid.SetColumnSpan(actions, 2);
        pause = AddButton(actions, "Pause", pauseAction);
        resume = AddButton(actions, "Resume", resumeAction);
        quit = AddButton(actions, "Quit", quitAction);
        cancel = AddButton(actions, "Cancel quit", cancelAction);
        AddButton(actions, "Open logs", openLogs);
        var startup = new CheckBox { Text = "Start GPU Admission when I sign in",
            ForeColor = Ink, Dock = DockStyle.Fill, Checked = StartupPreference.Enabled };
        startup.CheckedChanged += (_, _) =>
        {
            if (!startup.Enabled) return;
            try { StartupPreference.Set(startup.Checked); }
            catch (Exception error) when (error is UnauthorizedAccessException or
                System.Security.SecurityException or IOException or InvalidOperationException)
            {
                startup.Enabled = false;
                MessageBox.Show(this, error.Message, "Could not change startup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                startup.Checked = StartupPreference.Enabled;
            }
        };
        grid.Controls.Add(startup, 0, 4);
        grid.SetColumnSpan(startup, 2);
    }

    static Label TextLabel(string text, Font font, Color color) => new()
    {
        Text = text, Font = font, ForeColor = color, Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty, UseMnemonic = false
    };

    Label AddCard(TableLayoutPanel grid, int column, int row, string label)
    {
        var section = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Card,
            ColumnCount = 1, RowCount = 2, Padding = new Padding(15, 9, 15, 9),
            Margin = new Padding(column == 0 ? 0 : 6, 0, column == 0 ? 6 : 0, 10) };
        section.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        section.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        section.Controls.Add(TextLabel(label, strong, Cyan), 0, 0);
        var value = TextLabel("—", body, Ink);
        section.Controls.Add(value, 0, 1);
        grid.Controls.Add(section, column, row);
        return value;
    }

    Button AddButton(FlowLayoutPanel parent, string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(88, 33),
            FlatStyle = FlatStyle.Flat, BackColor = Card, ForeColor = Ink,
            Margin = new Padding(0, 0, 8, 0), UseVisualStyleBackColor = false };
        button.FlatAppearance.BorderColor = Color.FromArgb(64, 79, 98);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(41, 63, 79);
        button.Click += (_, _) => action();
        parent.Controls.Add(button);
        return button;
    }

    public void UpdateReadings(string mode, string explanation, string gpuText, string workloadText,
        string usageText, string diagnosticText, bool canPause, bool canResume, bool canQuit, bool canCancel)
    {
        state.Text = (mode == "Hold" ? "Paused" : mode).ToUpperInvariant();
        state.ForeColor = mode == "Auto" ? Cyan : Amber;
        reason.Text = explanation;
        gpu.Text = gpuText;
        workloads.Text = workloadText;
        resources.Text = usageText;
        history.Text = diagnosticText;
        pause.Enabled = canPause;
        resume.Enabled = canResume;
        quit.Enabled = canQuit;
        cancel.Visible = canCancel;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { heading.Dispose(); strong.Dispose(); body.Dispose(); }
    }
}
