using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\HomeNetworkGpuAdmissionTray", out bool first);
        const string showEvent = @"Local\GpuAdmissionShowStatus";
        bool showRequested = args.Contains("--status", StringComparer.Ordinal);
        if (!first)
        {
            if (showRequested && EventWaitHandle.TryOpenExisting(showEvent, out var running))
            {
                using (running) running.Set();
            }
            return 0;
        }
        using var show = new EventWaitHandle(showRequested, EventResetMode.AutoReset, showEvent);
        ApplicationConfiguration.Initialize();
        try
        {
            var portableRoot = Path.Combine(AppContext.BaseDirectory, "root.txt");
            string root = File.Exists(portableRoot)
                ? Path.GetFullPath(File.ReadAllText(portableRoot).Trim())
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GpuAdmission");
            if (!File.Exists(Path.Combine(root, "bin", "config.json")))
            {
                if (showRequested)
                    MessageBox.Show($"Create bin\\config.json under {root} first. See the setup steps in the README.",
                        "GPU Admission setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }
            using var tray = new AdmissionTray(root, show);
            Application.Run(tray);
            return tray.ExitCode;
        }
        catch (Exception ex)
        {
            Trace.TraceError("GPU Admission startup failed: {0}", ex.GetType().Name);
            return 1;
        }
    }
}

sealed class AdmissionTray : ApplicationContext
{
    readonly string root;
    readonly string run = Guid.NewGuid().ToString("N");
    readonly ChildLifetime lifetime = new();
    readonly Process guard;
    readonly Process self = Process.GetCurrentProcess();
    readonly NotifyIcon icon = new();
    readonly ContextMenuStrip menu = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    readonly ToolStripMenuItem pause;
    readonly ToolStripMenuItem resume;
    readonly ToolStripMenuItem quit;
    readonly ToolStripMenuItem cancel;
    StatusWindow? panel;
    readonly Icon appIcon = new(typeof(Program), "GpuAdmission.ico");
    bool draining;
    string? pendingMode;
    DateTimeOffset lastMeasurement;
    TimeSpan lastGuardCpu, lastTrayCpu;
    string QuitFile => Path.Combine(root, $"quit-{run}");
    public int ExitCode { get; private set; } = 1;

    public AdmissionTray(string root, EventWaitHandle show)
    {
        this.root = root;
        var start = new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File",
            Path.Combine(AppContext.BaseDirectory, "Run-Guard.ps1"), "-Config",
            Path.Combine(root, "bin", "config.json"), "-TrayRun", run })
            start.ArgumentList.Add(arg);
        guard = Process.Start(start) ?? throw new InvalidOperationException("Guard launch failed");
        try
        {
            lifetime.Attach(guard);
            guard.StandardInput.WriteLine(run);
            guard.StandardInput.Close();
        }
        catch
        {
            lifetime.Dispose();
            guard.Dispose();
            throw;
        }

        Add("Status", ShowStatus);
        pause = Add("Pause admission", () => SetMode("Hold"));
        resume = Add("Resume admission", () => SetMode("Auto"));
        Add("Open current log", () => Open(Path.Combine(root, "logs", "events.jsonl")));
        Add("Open log folder", () => Open(Path.Combine(root, "logs")));
        quit = Add("Quit", RequestQuit);
        cancel = Add("Cancel quit", CancelQuit);
        icon.Icon = appIcon;
        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) => ShowStatus();
        icon.Text = "GPU Admission · Starting";
        icon.Visible = true;
        timer.Tick += (_, _) =>
        {
            if (show.WaitOne(0)) ShowStatus();
            else RefreshStatus();
        };
        timer.Start();
    }

    ToolStripMenuItem Add(string title, Action action)
    {
        var item = new ToolStripMenuItem(title);
        item.Click += (_, _) =>
        {
            try { action(); RefreshStatus(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                MessageBox.Show(ex.Message, "GPU Admission", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        menu.Items.Add(item);
        return item;
    }

    static void Open(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

    void SetMode(string mode)
    {
        var path = Path.Combine(root, "modes.json");
        var data = Read(path) ?? throw new IOException("Admission settings are unavailable.");
        if (data["consumer_modes"] is not JsonObject)
            throw new IOException("Consumer settings are invalid.");
        data["policy_mode"] = mode;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(stream, data);
                stream.Flush(true);
            }
            File.Replace(temporary, path, null);
        }
        finally { File.Delete(temporary); }
        pendingMode = mode;
    }

    void RequestQuit()
    {
        SetMode("Hold");
        using (File.Open(QuitFile, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        draining = true;
    }

    void CancelQuit()
    {
        // Rename is the race boundary. Once the guard takes the request,
        // deletion fails and shutdown finishes.
        try
        {
            var cancelPath = QuitFile + ".cancelled";
            File.Move(QuitFile, cancelPath);
            draining = false;
            File.Delete(cancelPath);
        }
        catch (FileNotFoundException) { }
    }

    static JsonObject? Read(string path)
    {
        try
        {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return source.Length <= 16384 ? JsonNode.Parse(source) as JsonObject : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { return null; }
    }

    static string Value(JsonNode? node, string key) =>
        (node as JsonObject)?[key]?.ToString() ?? "unavailable";

    void RefreshStatus()
    {
        if (guard.HasExited)
        {
            ExitCode = draining && guard.ExitCode == 0 ? 0 : 1;
            if (ExitCode != 0)
            {
                icon.ShowBalloonTip(2000, "GPU Admission", "The guard exited unexpectedly.", ToolTipIcon.Error);
                Trace.TraceError("GPU guard exited with code {0}", guard.ExitCode);
            }
            ExitThread();
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var status = Read(Path.Combine(root, "public", "status.json"));
        var diagnostics = Read(Path.Combine(root, "tray-status.json"));
        bool currentRun = Value(diagnostics, "run") == run;
        bool fresh = currentRun && long.TryParse(Value(status, "observed_at"), out long observed)
            && now.ToUnixTimeSeconds() - observed is >= 0 and <= 15;
        string mode = fresh ? Value(status, "policy_mode") : "Unavailable";
        if (mode == pendingMode) pendingMode = null;
        string state = draining ? "Draining" : pendingMode == "Hold" ? "Pausing" :
            pendingMode == "Auto" ? "Resuming" : mode;
        icon.Text = $"GPU Admission · {state}";
        pause.Enabled = !draining && mode != "Hold" && pendingMode != "Hold";
        resume.Enabled = !draining && fresh && mode == "Hold" && pendingMode is null;
        quit.Enabled = !draining;
        cancel.Enabled = draining && File.Exists(QuitFile);
        if (panel is null) return;

        var guardCpu = guard.TotalProcessorTime;
        var trayCpu = self.TotalProcessorTime;
        double seconds = (now - lastMeasurement).TotalSeconds;
        guard.Refresh();
        self.Refresh();
        string Usage(Process process, TimeSpan used) =>
            $"{(seconds > 0 ? (used.TotalSeconds / seconds * 100).ToString("F1") + "% of one CPU" : "sampling")}, {process.WorkingSet64 / 1048576.0:F1} MiB working set";
        string code = fresh ? Value(status, "reason") : "status unavailable";
        string explanation = code switch
        {
            "gpu-unavailable" => "GPU telemetry unavailable. New GPU work is on hold.",
            "owner-uncertain" => "Waiting for the current GPU owner to clear.",
            "load-cooldown" => "Waiting for GPU load to settle.",
            "no-eligible-request" => "No eligible GPU work is waiting.",
            "managed-job" => "GPU capacity is assigned to a workload.",
            _ => code.Replace('-', ' ')
        };
        string Reading(string key, string unit) =>
            fresh && Value(diagnostics, key) is var value && value != "unavailable" ? value + unit : "Unavailable";
        var consumers = fresh && status?["consumer_modes"] is JsonObject modes
            ? modes.Select(pair => $"{ConsumerName(pair.Key)}   ·   {pair.Value}").ToArray() : [];
        string last = currentRun && DateTimeOffset.TryParse(Value(diagnostics, "last_success_utc"), out var timestamp)
            ? timestamp.ToLocalTime().ToString("g") : "Unavailable";
        panel.UpdateReadings(state, explanation,
            $"Utilization   {Reading("gpu_utilization_percent", "%")}\r\nFree memory   {Reading("free_vram_mib", " MiB")}",
            (consumers.Length == 0 ? "Status unavailable" : string.Join("\r\n", consumers)) +
                (Value(diagnostics, "owner_phase") is "active" or "reserved" ? $"\r\nOwner   {Value(diagnostics, "owner_phase")}" : ""),
            $"Guard   {Usage(guard, guardCpu - lastGuardCpu)}\r\nTray     {Usage(self, trayCpu - lastTrayCpu)}",
            $"Last GPU sample   {last}\r\nLogging   {(fresh ? Value(diagnostics, "logging") : "unavailable")}",
            pause.Enabled, resume.Enabled, quit.Enabled, cancel.Enabled);
        lastGuardCpu = guardCpu;
        lastTrayCpu = trayCpu;
        lastMeasurement = now;
    }

    static string ConsumerName(string id) => id switch
    {
        "tdarr" => "Tdarr",
        "telecom-transcription" => "Telecom transcription",
        _ => id.Replace('-', ' ')
    };

    void ShowStatus()
    {
        if (panel is not null) { panel.Activate(); return; }
        panel = new StatusWindow(appIcon, pause.PerformClick, resume.PerformClick,
            quit.PerformClick, cancel.PerformClick, () => Open(Path.Combine(root, "logs")));
        panel.FormClosed += (_, _) => panel = null;
        lastMeasurement = DateTimeOffset.UtcNow;
        lastGuardCpu = guard.TotalProcessorTime;
        lastTrayCpu = self.TotalProcessorTime;
        RefreshStatus();
        panel?.Show();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            panel?.Dispose();
            icon.Dispose();
            appIcon.Dispose();
            menu.Dispose();
            lifetime.Dispose();
            guard.Dispose();
            self.Dispose();
        }
        base.Dispose(disposing);
    }
}
