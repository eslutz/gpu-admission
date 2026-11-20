using System.Text.Json;
using System.Text.Json.Nodes;

sealed class WorkersPanel : UserControl
{
    readonly string root;
    readonly Appearance theme;
    readonly ListView list = new() { Name = "WorkerList", AccessibleName = "Workers", Dock = DockStyle.Fill,
        View = View.Details, FullRowSelect = true, MultiSelect = false, ShowItemToolTips = true };
    readonly Label details = new() { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(5) };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    readonly Func<JsonObject?> liveDiagnostics;
    string ConfigPath => Path.Combine(root, "bin", "config.json");
    string ModesPath => Path.Combine(root, "modes.json");
    string? Selected => list.SelectedItems.Count == 1 ? list.SelectedItems[0].Text : null;

    public WorkersPanel(string root, Appearance theme, Func<JsonObject?> diagnostics, Action<Func<bool>> apply)
    {
        this.root = root;
        this.theme = theme;
        liveDiagnostics = diagnostics;
        Dock = DockStyle.Fill;
        Padding = new Padding(22, 18, 22, 0);
        foreach (var (title, width) in new[] { ("Worker", 176), ("Type", 78), ("Policy", 64), ("Activity", 156), ("Last seen", 126) })
            list.Columns.Add(title, width);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        Add("Add worker", () =>
        {
            using var editor = new AddWorkerWindow(theme);
            if (editor.ShowDialog(this) != DialogResult.OK || editor.Worker is not { } worker) return Task.CompletedTask;
            var id = editor.WorkerId;
            apply(() =>
            {
                var cfg = AdmissionFiles.Read(ConfigPath) ?? throw new IOException("Configuration could not be read.");
                var modes = AdmissionFiles.Read(ModesPath) ?? throw new IOException("Modes could not be read.");
                if (cfg["consumers"] is not JsonObject registered || modes["consumer_modes"] is not JsonObject policies)
                    throw new IOException("Worker configuration is invalid.");
                if (registered.ContainsKey(id)) throw new IOException($"Worker '{id}' already exists.");
                if (registered.Count >= 64) throw new IOException("This profile already has 64 workers.");
                if (AdmissionFiles.Value(worker, "kind") == "docker" &&
                    registered.Any(p => AdmissionFiles.Value(p.Value, "container") == AdmissionFiles.Value(worker, "container")))
                    throw new IOException("That Docker workload is already registered.");
                AdmissionFiles.DirectoryFor(Path.Combine(root, "requests", id));
                policies[id] = "Hold";
                modes["policy_mode"] = "Hold";
                registered[id] = worker;
                // Unregistered entries in modes have no effect. Commit the settings in this order.
                AdmissionFiles.Write(ModesPath, modes);
                AdmissionFiles.Write(ConfigPath, cfg);
                return true;
            });
            return Task.CompletedTask;
        });
        Add("Connection details", () =>
        {
            if (Selected is { } id && AdmissionFiles.Read(ConfigPath)?["consumers"]?[id] is JsonObject worker)
                ShowConnection(id, worker);
            return Task.CompletedTask;
        });
        Add("Enable worker", () => ChangePolicy("Auto"));
        Add("Hold worker", () => ChangePolicy("Hold"));
        Add("Test Windows", () =>
        {
            if (Selected is { } id && AdmissionFiles.Read(ConfigPath)?["consumers"]?[id] is JsonObject worker &&
                AdmissionFiles.Value(worker, "kind") == "windows")
                details.Text = NativeWorker.Probe(root, id) == 0
                    ? "Connection verified: the worker read live status and wrote to its request folder."
                    : "Test needs a fresh guard heartbeat and a registered Windows worker.";
            return Task.CompletedTask;
        });
        Controls.Add(list);
        Controls.Add(details);
        Controls.Add(actions);
        details.Text = "Register a worker, then use Connection details to set up its integration.";
        timer.Tick += (_, _) => RefreshWorkers();
        VisibleChanged += (_, _) =>
        {
            timer.Enabled = Visible;
            if (Visible) RefreshWorkers();
        };
        RefreshWorkers();
        timer.Enabled = Visible;

        void Add(string text, Func<Task> click)
        {
            var button = new UiButton { Text = text, Primary = text == "Add worker" };
            button.Click += async (_, _) =>
            {
                try { await click(); if (!IsDisposed) RefreshWorkers(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
                { if (!IsDisposed) MessageBox.Show(this, e.Message, "GPU Admission", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            actions.Controls.Add(button);
        }
    }

    Task ChangePolicy(string policy)
    {
        if (Selected is not { } id) return Task.CompletedTask;
        var modes = AdmissionFiles.Read(ModesPath);
        if (modes?["consumer_modes"] is not JsonObject policies) throw new IOException("Could not read worker modes.");
        if (policy == "Auto" && AdmissionFiles.Value(policies, id) != "Auto")
        {
            var status = AdmissionFiles.Read(Path.Combine(root, "public", "status.json"));
            if (AdmissionFiles.Value(modes, "policy_mode") != "Hold" || liveDiagnostics() is null ||
                !AdmissionFiles.Fresh(status) || AdmissionFiles.Value(status, "policy_mode") != "Hold")
                throw new IOException("Pause admission and wait for the guard to acknowledge Hold before enabling a worker.");
            if (AdmissionFiles.Value(AdmissionFiles.Read(ConfigPath)?["consumers"]?[id], "kind") is not "" &&
                !ConnectionVerified(id, status))
                throw new IOException("Test the worker connection first. Use Test Windows or follow the Docker instructions under Connection details.");
        }
        policies[id] = policy;
        AdmissionFiles.Write(ModesPath, modes);
        return Task.CompletedTask;
    }

    JsonObject? Presence(string id) =>
        AdmissionFiles.Read(Path.Combine(root, "requests", id, "presence", "heartbeat.json"));
    static bool IdentifiesWorker(JsonObject? record, string id) =>
        AdmissionFiles.Value(record, "schema") == "gpu-admission-presence/v1" && AdmissionFiles.Value(record, "consumer") == id;
    static bool SameGeneration(JsonObject? presence, JsonObject? status) =>
        presence is not null && status is not null && AdmissionFiles.Same(presence, status, "generation");
    bool ConnectionVerified(string id, JsonObject? status)
    {
        var receipt = Presence(id);
        return IdentifiesWorker(receipt, id) && SameGeneration(receipt, status);
    }

    void RefreshWorkers()
    {
        if (AdmissionFiles.Read(ConfigPath)?["consumers"] is not JsonObject registered) return;
        var diagnostics = liveDiagnostics();
        var status = AdmissionFiles.Read(Path.Combine(root, "public/status.json"));
        bool fresh = diagnostics is not null && AdmissionFiles.Fresh(status);
        var modes = AdmissionFiles.Read(ModesPath)?["consumer_modes"];
        var selected = Selected;
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var pair in registered.OrderBy(x => x.Key))
        {
            var id = pair.Key;
            if (!AdmissionFiles.WorkerId().IsMatch(id)) continue;
            var presence = Presence(id);
            bool recorded = IdentifiesWorker(presence, id);
            bool isPresent = fresh && recorded && AdmissionFiles.Fresh(presence) && SameGeneration(presence, status);
            bool assigned = fresh && AdmissionFiles.Value(status, "consumer") == id;
            bool waiting = fresh && diagnostics?["waiting"] is JsonArray ids && ids.Any(v => v?.ToString() == id);
            bool uncertain = fresh && AdmissionFiles.Value(status, "reason") == "owner-uncertain" &&
                AdmissionFiles.Value(diagnostics, "owner_consumer") == id;
            var activity = uncertain ? "Recovery required" :
                assigned && isPresent && AdmissionFiles.Value(presence, "phase") == "running" &&
                    AdmissionFiles.Value(presence, "session_id") == AdmissionFiles.Value(status, "request_id") ? "Running" :
                assigned ? "Granted" : waiting ? "Waiting" : isPresent ? "Connected / idle" : "Registered";
            string time = recorded && double.TryParse(AdmissionFiles.Value(presence, "observed_at"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var unix) &&
                unix is >= 0 and < 253402300799
                ? DateTimeOffset.FromUnixTimeSeconds((long)unix).ToLocalTime().ToString("g") : "—";
            var item = new ListViewItem(new[] { id, AdmissionFiles.Value(pair.Value, "kind") == "windows" ? "Windows" : "Docker",
                AdmissionFiles.Value(modes, id) == "Auto" ? "Auto" : "Hold", activity, time }) { ToolTipText = id };
            list.Items.Add(item);
            if (id == selected) item.Selected = true;
        }
        list.EndUpdate();
    }

    void ShowConnection(string id, JsonObject worker)
    {
        string body;
        if (AdmissionFiles.Value(worker, "kind") == "windows")
        {
            var exe = Application.ExecutablePath;
            var powershellPath = exe.Replace("'", "''");
            body = $"Task Scheduler (your Windows account)\r\nProgram/script: {exe}\r\n" +
                $"Add arguments: --run-worker {id}\r\n\r\nPowerShell script\r\n" +
                $"$worker = Start-Process -FilePath '{powershellPath}' -ArgumentList '--run-worker','{id}' -Wait -PassThru\r\n" +
                "$worker.ExitCode\r\n\r\n" +
                "Test Windows checks the file handshake in Hold. Enable this worker in Hold, then Resume admission.";
        }
        else
        {
            string read = JsonSerializer.Serialize(Path.Combine(root, "public").Replace('\\', '/'));
            string write = JsonSerializer.Serialize(Path.Combine(root, "requests", id).Replace('\\', '/'));
            body = $"Add these mounts to the selected service. Install the worker client in that service and use it around GPU jobs.\r\n" +
                $"After applying the mounts, test from inside the container:\r\npython3 gpu_admission_client.py probe /admission/read /admission/request {id}\r\n\r\n" +
                $"volumes:\r\n  - type: bind\r\n    source: {read}\r\n    target: /admission/read\r\n    read_only: true\r\n    bind:\r\n      create_host_path: false\r\n" +
                $"  - type: bind\r\n    source: {write}\r\n    target: /admission/request\r\n    bind:\r\n      create_host_path: false";
        }
        using var dialog = new AppWindow(theme) { Text = $"Connect {id}", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(700, 410) };
        var text = new TextBox { Text = body, Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill };
        var copy = new UiButton { Text = "Copy", Dock = DockStyle.Bottom, Height = 40 };
        copy.Click += (_, _) => Clipboard.SetText(body);
        dialog.Controls.Add(text);
        dialog.Controls.Add(copy);
        dialog.ShowDialog(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}

sealed class AddWorkerWindow : AppWindow
{
    sealed record DockerContainer(string Name) { public override string ToString() => Name; }
    public JsonObject? Worker { get; private set; }
    public string WorkerId { get; private set; } = "";
    public AddWorkerWindow(Appearance theme) : base(theme)
    {
        Text = "Add worker";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(590, 330);
        var id = new TextBox { Dock = DockStyle.Fill };
        var kind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        kind.Items.AddRange(["Windows executable", "Docker container"]);
        kind.SelectedIndex = 0;
        var exe = new TextBox { Dock = DockStyle.Fill };
        var args = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, PlaceholderText = "One argument per line" };
        var docker = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Visible = false };
        var memory = new NumericUpDown { Maximum = 262144, Increment = 256 };
        var browse = new UiButton { Text = "Browse…", MinimumSize = new Size(75, 29), Padding = Padding.Empty };
        var save = new UiButton { Text = "Save in Hold", Primary = true };
        var note = new Label { Dock = DockStyle.Fill, AutoEllipsis = true };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 7, Padding = new Padding(12) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        for (int i = 0; i < 6; i++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 3 ? 66 : i == 5 ? 46 : 36));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        void Row(string label, Control control, int row)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            grid.Controls.Add(control, 1, row);
        }
        var workload = new Panel { Dock = DockStyle.Fill };
        workload.Controls.Add(exe);
        workload.Controls.Add(docker);
        Row("Worker ID", id, 0); Row("Type", kind, 1); Row("Workload", workload, 2);
        grid.Controls.Add(browse, 2, 2);
        Row("Arguments", args, 3); Row("Min. free MiB", memory, 4);
        grid.Controls.Add(save, 1, 5);
        grid.Controls.Add(new UiButton { Text = "Cancel", DialogResult = DialogResult.Cancel }, 2, 5);
        grid.Controls.Add(note, 0, 6); grid.SetColumnSpan(note, 3);
        Controls.Add(grid);
        browse.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Filter = "Windows executables (*.exe)|*.exe", CheckFileExists = true };
            if (picker.ShowDialog(this) == DialogResult.OK) exe.Text = picker.FileName;
        };
        kind.SelectedIndexChanged += async (_, _) =>
        {
            bool containers = kind.SelectedIndex == 1;
            exe.Visible = args.Enabled = browse.Visible = !containers;
            if (!containers) { docker.Visible = false; note.Text = ""; save.Enabled = true; return; }
            docker.Visible = true;
            save.Enabled = false;
            try
            {
                var endpoint = await GpuDiscovery.Command("docker", "context", "inspect", "--format", "{{.Endpoints.docker.Host}}");
                var effective = Environment.GetEnvironmentVariable("DOCKER_HOST");
                if (!string.IsNullOrWhiteSpace(effective)) endpoint = effective;
                if (endpoint.Trim() is not ("npipe:////./pipe/docker_engine" or "npipe:////./pipe/dockerDesktopLinuxEngine"))
                    throw new IOException("Choose a local Docker Desktop context first.");
                var rows = await GpuDiscovery.Command("docker", "ps", "-a", "--no-trunc", "--format", "{{.ID}}|{{.Names}}");
                if (IsDisposed || kind.SelectedIndex != 1) return;
                docker.Items.Clear();
                foreach (var line in rows.Split('\n', StringSplitOptions.TrimEntries))
                {
                    var row = line.Split('|');
                    if (row.Length == 2 && System.Text.RegularExpressions.Regex.IsMatch(row[0], "^[0-9a-f]{64}$"))
                        docker.Items.Add(new DockerContainer(row[1]));
                }
                note.Text = docker.Items.Count == 0 ? "No local containers found." : "Choose the container that will actually run the GPU job.";
            }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
            { if (!IsDisposed && kind.SelectedIndex == 1) note.Text = e.Message; }
            finally { if (!IsDisposed) save.Enabled = true; }
        };
        save.Click += (_, _) =>
        {
            if (!AdmissionFiles.WorkerId().IsMatch(id.Text)) { note.Text = "Use a lowercase ID such as my-video-worker."; return; }
            var data = new JsonObject { ["minimum_free_vram_mib"] = (int)memory.Value };
            if (kind.SelectedIndex == 1)
            {
                if (docker.SelectedItem is not DockerContainer d) { note.Text = "Choose a Docker container."; return; }
                data["kind"] = "docker"; data["container"] = d.Name;
            }
            else
            {
                if (!Path.IsPathFullyQualified(exe.Text) || !File.Exists(exe.Text) ||
                    !Path.GetExtension(exe.Text).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(exe.Text).Equals("wsl.exe", StringComparison.OrdinalIgnoreCase))
                { note.Text = "Choose a native Windows .exe that runs its GPU jobs in its own process tree."; return; }
                var arguments = new JsonArray(args.Lines.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
                data["kind"] = "windows"; data["executable"] = exe.Text; data["arguments"] = arguments;
            }
            Worker = data; WorkerId = id.Text; DialogResult = DialogResult.OK;
        };
    }
}
