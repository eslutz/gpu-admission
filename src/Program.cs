using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args is ["--run-worker", var workerId, ..] && (args.Length == 2 || args[2] == "--"))
        {
            try { return NativeWorker.Run(AdmissionFiles.Root(), workerId, args.Length > 2 ? args[3..] : []); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            { Trace.TraceError(ex.GetType().Name); return 1; }
        }
        if (args is ["--probe-worker", var probeId])
        {
            try { return NativeWorker.Probe(AdmissionFiles.Root(), probeId); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 1; }
        }
        if (args.Length > 0 && args is not ["--status"]) return 2;
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
            string root = AdmissionFiles.Root();
            var theme = new Appearance(root);
            bool firstSetup = !File.Exists(Path.Combine(root, "bin", "config.json"));
            if (firstSetup)
            {
                using var icon = new Icon(typeof(Program), "GpuAdmission.ico");
                if (!SetupWindow.FirstRun(root, icon, theme)) return 0;
                show.Set();
            }
            using var tray = new AdmissionTray(root, show, theme);
            Application.Run(tray);
            return tray.ExitCode;
        }
        catch (Exception ex)
        {
            Trace.TraceError("GPU Admission startup failed: {0}", ex.GetType().Name);
            if (showRequested) MessageBox.Show(ex.Message, "GPU Admission", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }
}

sealed class AdmissionTray : ApplicationContext
{
    readonly string root;
    readonly Appearance theme;
    string run = "";
    bool bindingUnavailable;
    ChildLifetime? lifetime;
    Process guard = null!;
    Func<bool>? afterDrain;
    readonly Process self = Process.GetCurrentProcess();
    readonly NotifyIcon icon = new();
    readonly ContextMenuStrip menu = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    readonly ToolStripMenuItem pause;
    readonly ToolStripMenuItem resume;
    readonly ToolStripMenuItem quit;
    readonly ToolStripMenuItem cancel;
    readonly ToolStripMenuItem repair;
    readonly ToolStripMenuItem openLogs;
    StatusWindow? panel;
    readonly Icon appIcon = new(typeof(Program), "GpuAdmission.ico");
    bool draining;
    string? pendingMode;
    DateTimeOffset lastMeasurement;
    TimeSpan lastGuardCpu, lastTrayCpu;
    string QuitFile => Path.Combine(root, $"quit-{run}");
    public int ExitCode { get; private set; } = 1;

    public AdmissionTray(string root, EventWaitHandle show, Appearance theme)
    {
        this.root = root;
        this.theme = theme;
        StartGuard();

        Add("Status", ShowStatus);
        Add("Workers", ShowWorkers);
        repair = Add("Repair GPU setup", Repair);
        pause = Add("Pause admission", () => SetMode("Hold"));
        resume = Add("Resume admission", () => SetMode("Auto"));
        Add("Open current log", () => Open(Path.Combine(root, "logs", "events.jsonl")));
        openLogs = Add("Open log folder", () => Open(Path.Combine(root, "logs")));
        quit = Add("Quit", RequestQuit);
        cancel = Add("Cancel quit", CancelQuit);
        menu.Opening += (_, _) =>
        {
            theme.Refresh();
            menu.BackColor = theme.Colors.Page;
            menu.ForeColor = theme.Colors.Text;
        };
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

    void StartGuard()
    {
        run = Guid.NewGuid().ToString("N");
        bindingUnavailable = false;
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
        var cfg = Read(Path.Combine(root, "bin", "config.json"));
        if (Value(cfg, "adapter_pci") != "unavailable")
        {
            // Resolve current LUID without rewriting saved configuration or changing the journal identity.
            string binding = "unbound";
            try
            {
                var devices = Task.Run(GpuDiscovery.Detect).GetAwaiter().GetResult();
                var matched = devices.Where(d => d.Pci == Value(cfg, "adapter_pci") &&
                    d.Vendor.ToString() == Value(cfg, "adapter_vendor") && d.Device.ToString() == Value(cfg, "adapter_device") &&
                    GpuDiscovery.Key(d) == Value(cfg, "gpu")).ToArray();
                if (matched.Length == 1 && devices.Count(a => a.Pci == matched[0].Pci) == 1) binding = matched[0].Luid;
            }
            catch (Exception e) when (e is IOException or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception) { }
            bindingUnavailable = binding == "unbound";
            start.ArgumentList.Add("-ResolvedLuid");
            start.ArgumentList.Add(binding);
        }
        lifetime = new ChildLifetime();
        guard = Process.Start(start) ?? throw new InvalidOperationException("Guard launch failed");
        try
        {
            lifetime.Attach(guard);
            guard.StandardInput.WriteLine(run);
            guard.StandardInput.Close();
        }
        catch
        {
            lifetime?.Dispose();
            guard.Dispose();
            throw;
        }
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
        AdmissionFiles.Write(path, data);
        pendingMode = mode;
    }

    void RequestQuit()
    {
        SetMode("Hold");
        using (File.Open(QuitFile, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        draining = true;
    }

    void QueueSettings(Func<bool> edit)
    {
        if (draining) throw new InvalidOperationException("A drain is already in progress.");
        if (MessageBox.Show("Apply settings after the current owner releases the GPU? Admission stays in Hold.",
            "Apply GPU Admission settings", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        RequestQuit();
        afterDrain = edit;
        ShowStatus();
        panel?.ShowOverview();
    }

    void ShowWorkers()
    {
        ShowStatus();
        panel?.ShowWorkers();
    }

    JsonObject? Diagnostics()
    {
        var diagnostics = Read(Path.Combine(root, "tray-status.json"));
        return Value(diagnostics, "run") == run && AdmissionFiles.Fresh(diagnostics) ? diagnostics : null;
    }

    void Repair()
    {
        var cfg = Read(Path.Combine(root, "bin", "config.json")) ?? throw new IOException("Configuration could not be read.");
        using var wizard = new SetupWindow(appIcon, theme, Value(cfg, "gpu"), AdmissionFiles.Value(cfg, "windows_engine_luid"));
        if (wizard.ShowDialog(panel) != DialogResult.OK || wizard.Selection is not { } selected) return;
        QueueSettings(() =>
        {
            var path = Path.Combine(root, "bin", "config.json");
            var existing = Read(path) ?? throw new IOException("Configuration could not be read.");
            if (Value(existing, "gpu") != GpuDiscovery.Key(selected)) throw new IOException("GPU identity changed. Keep the existing profile.");
            var detected = GpuDiscovery.Configuration(root, selected, new JsonObject());
            foreach (var key in new[] { "adapter_pci", "adapter_vendor", "adapter_device", "windows_engine_luid", "windows_physical_index" })
                existing[key] = detected[key]?.DeepClone();
            AdmissionFiles.Write(path, existing);
            return true;
        });
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
            afterDrain = null;
            File.Delete(cancelPath);
        }
        catch (FileNotFoundException) { }
    }

    static JsonObject? Read(string path) => AdmissionFiles.Read(path);

    static string Value(JsonNode? node, string key) =>
        (node as JsonObject)?[key]?.ToString() ?? "unavailable";

    void RefreshStatus()
    {
        if (guard.HasExited)
        {
            if (draining && guard.ExitCode == 0 && afterDrain is { } apply)
            {
                afterDrain = null;
                draining = false;
                guard.Dispose();
                lifetime?.Dispose();
                try { apply(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
                { MessageBox.Show(e.Message, "Could not apply GPU settings", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                try { StartGuard(); }
                catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    MessageBox.Show(e.Message, "Could not restart guard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    ExitThread();
                }
                return;
            }
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
        bool fresh = currentRun && AdmissionFiles.Fresh(status);
        bool freshReadings = currentRun && AdmissionFiles.Fresh(diagnostics);
        string mode = fresh ? Value(status, "policy_mode") : "Unavailable";
        if (mode == pendingMode) pendingMode = null;
        string state = draining ? "Draining" : pendingMode == "Hold" ? "Pausing" :
            pendingMode == "Auto" ? "Resuming" : mode;
        icon.Text = $"GPU Admission · {state}";
        pause.Enabled = !draining && mode != "Hold" && pendingMode != "Hold";
        resume.Enabled = !draining && fresh && mode == "Hold" && pendingMode is null &&
            Value(status, "reason") is not ("monitor-only" or "gpu-unavailable" or "owner-uncertain");
        quit.Enabled = repair.Enabled = !draining;
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
            "gpu-unavailable" when bindingUnavailable => "The saved GPU could not be identified. Open Repair GPU setup.",
            "gpu-unavailable" => Value(diagnostics, "probe") switch
            {
                "nvidia-query" => "NVIDIA memory or adapter identity is unavailable.",
                "windows-counters" => "Windows GPU Engine counters are unavailable.",
                "adapter-counters" => "No valid counters matched the selected adapter. Try Repair GPU setup.",
                _ => "GPU telemetry is unavailable."
            },
            "monitor-only" => "This GPU can be monitored. Admission is disabled for this profile.",
            "owner-uncertain" => "Waiting for the current GPU owner to clear.",
            "load-cooldown" or "no-eligible-request" when state == "Hold" => "New jobs will wait until you resume.",
            "load-cooldown" => "Waiting for GPU load to settle.",
            "no-eligible-request" => "No eligible GPU work is waiting.",
            "managed-job" => "GPU capacity is assigned to a workload.",
            _ => code.Replace('-', ' ')
        };
        string Metric(string key, string unit) =>
            fresh && freshReadings && Value(diagnostics, key) is var value && value != "unavailable" ? value + unit : "Unavailable";
        var workers = fresh && status?["consumer_modes"] is JsonObject modes
            ? modes.Select(pair => new Reading(ConsumerName(pair.Key), pair.Value?.ToString() ?? "Unavailable"))
                .OrderBy(row => row.Label, StringComparer.CurrentCultureIgnoreCase).ToArray() : [];
        List<Reading> summary = workers.Take(3).ToList();
        if (workers.Length > 3) summary.Add(new($"{workers.Length - 3} more in Workers", ""));
        string last = currentRun && DateTimeOffset.TryParse(Value(diagnostics, "last_success_utc"), out var timestamp)
            ? timestamp.ToLocalTime().ToString("g") : "Unavailable";
        List<Reading> gpu = [new("Utilization", Metric("gpu_utilization_percent", "%"))];
        if (code == "monitor-only")
        {
            gpu.Add(new("Dedicated used", Metric("dedicated_used_mib", " MiB")));
            gpu.Add(new("Shared used", Metric("shared_used_mib", " MiB")));
        }
        else
        {
            gpu.Add(new("Free VRAM", Metric("free_vram_mib", " MiB")));
        }
        List<Reading> troubleshooting = [
            new("Guard", Usage(guard, guardCpu - lastGuardCpu)),
            new("Tray", Usage(self, trayCpu - lastTrayCpu)),
            new("Last GPU sample", last),
            new("Logging", freshReadings ? Value(diagnostics, "logging") : "Unavailable")
        ];
        if (freshReadings && Value(diagnostics, "owner_phase") is "active" or "reserved")
            troubleshooting.Add(new("GPU owner", Value(diagnostics, "owner_phase")));
        panel.UpdateReadings(new DashboardStatus(state, code, explanation, gpu, summary, troubleshooting,
            pause.Enabled, resume.Enabled, quit.Enabled, cancel.Enabled, repair.Enabled && code == "gpu-unavailable"));
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
        panel = new StatusWindow(theme, appIcon, pause.PerformClick, resume.PerformClick,
            quit.PerformClick, cancel.PerformClick, openLogs.PerformClick, repair.PerformClick,
            new WorkersPanel(root, theme, Diagnostics, QueueSettings));
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
            lifetime?.Dispose();
            guard?.Dispose();
            self.Dispose();
        }
        base.Dispose(disposing);
    }
}
