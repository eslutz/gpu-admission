using System.ComponentModel;
using System.Text.Json.Nodes;

sealed class SetupWindow : AppWindow
{
    readonly ComboBox adapters = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly Label result = new() { Dock = DockStyle.Top, Height = 82, AutoEllipsis = true };
    readonly UiButton verify = new() { Text = "Verify readings", AutoSize = true, Enabled = false };
    readonly UiButton save = new() { Text = "Save in Hold", Enabled = false, Primary = true };
    readonly UiButton scan = new() { Text = "Refresh adapters", AutoSize = true };
    GpuAdapter? verified;
    public GpuAdapter? Selection { get; private set; }

    public SetupWindow(Icon image, Appearance theme, string? existingGpu = null, string? savedLuid = null) : base(theme)
    {
        var repair = existingGpu is not null;
        Text = repair ? "Repair GPU setup" : "Set up GPU Admission";
        Icon = image;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(610, 270);
        MinimumSize = Size;
        Padding = new Padding(20);
        var intro = new Label { Dock = DockStyle.Top, Height = 58,
            Text = repair ? "Verify the adapter already assigned to this profile. Workers and their settings stay in place." :
                "Choose the GPU this PC will share. Verify its Windows readings before saving. New GPU work will stay paused." };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, WrapContents = false };
        buttons.Controls.AddRange([scan, verify, save, new UiButton { Text = "Cancel", DialogResult = DialogResult.Cancel }]);
        Controls.Add(result);
        Controls.Add(adapters);
        Controls.Add(intro);
        Controls.Add(buttons);
        adapters.SelectedIndexChanged += (_, _) => { verified = null; save.Enabled = false; verify.Enabled = adapters.SelectedItem is GpuAdapter; };
        async Task RefreshAdapters()
        {
            adapters.Enabled = scan.Enabled = verify.Enabled = save.Enabled = false;
            result.Text = "Checking Windows adapters and NVIDIA telemetry…";
            try
            {
                var devices = await GpuDiscovery.Detect();
                if (IsDisposed) return;
                adapters.Items.Clear();
                var candidates = devices.Where(a => a.Pci is not null &&
                    devices.Count(d => a.Pci == d.Pci) == 1 &&
                    (existingGpu is null || a.NvidiaUuid == existingGpu || GpuDiscovery.Key(a) == existingGpu)).ToArray();
                adapters.Items.AddRange(candidates);
                if (candidates.Length == 1) adapters.SelectedIndex = 0;
                else if (candidates.Count(a => a.CanUseNvidia) == 1)
                    adapters.SelectedItem = candidates.Single(a => a.CanUseNvidia);
                result.Text = candidates.Length == 0 ? "No matching, identifiable hardware adapter was found." :
                    candidates is [{ } match] && savedLuid is not null && match.Luid != savedLuid ?
                        "The Windows adapter ID changed. Verify the current readings to repair the saved binding." :
                        "Verify the selected adapter. If counters are missing, run a small task on that GPU and try again.";
            }
            catch (Exception e) when (e is IOException or Win32Exception or System.Runtime.InteropServices.COMException)
            { if (!IsDisposed) result.Text = e.Message; }
            finally
            {
                if (!IsDisposed) { adapters.Enabled = scan.Enabled = true; verify.Enabled = adapters.SelectedItem is GpuAdapter; }
            }
        }
        Shown += async (_, _) => await RefreshAdapters();
        scan.Click += async (_, _) => await RefreshAdapters();
        verify.Click += async (_, _) =>
        {
            if (adapters.SelectedItem is not GpuAdapter selected) return;
            verified = null;
            scan.Enabled = adapters.Enabled = verify.Enabled = save.Enabled = false;
            result.Text = "Checking counters for this exact adapter…";
            try
            {
                var load = await GpuDiscovery.VerifyEngines(selected);
                if (IsDisposed) return;
                if (!double.TryParse(load, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double loadValue) ||
                    !double.IsFinite(loadValue) || loadValue is < 0 or > 100)
                    throw new IOException("Windows returned an invalid utilization reading.");
                if (selected.CanUseNvidia)
                {
                    var memory = (await GpuDiscovery.Command("nvidia-smi", $"--id={selected.NvidiaUuid}",
                        "--query-gpu=memory.free", "--format=csv,noheader,nounits")).Trim();
                    if (!ulong.TryParse(memory, out var free)) throw new IOException("NVIDIA free memory is unavailable.");
                    result.Text = $"Verified NVIDIA adapter • {loadValue:0.#}% utilization • {free} MiB free.";
                }
                else
                {
                    var memory = await GpuDiscovery.VendorMemory(selected);
                    if (IsDisposed) return;
                    result.Text = $"Verified utilization ({loadValue:0.#}%). This adapter is available for monitoring only." +
                        (memory is null ? "" : $"\r\n{memory} (diagnostic).");
                }
                verified = selected;
                save.Enabled = true;
            }
            catch (Exception e) when (e is IOException or Win32Exception)
            {
                if (!IsDisposed) result.Text = $"{e.Message} Admission stays paused. Confirm the driver and that this adapter is active.";
            }
            finally { if (!IsDisposed) scan.Enabled = adapters.Enabled = verify.Enabled = true; }
        };
        save.Click += (_, _) => { if (verified is not null) { Selection = verified; DialogResult = DialogResult.OK; } };
    }

    public static bool FirstRun(string root, Icon icon, Appearance theme)
    {
        CheckVacant(root);
        using var wizard = new SetupWindow(icon, theme);
        if (wizard.ShowDialog() != DialogResult.OK || wizard.Selection is not { } adapter) return false;
        Provision(root, adapter);
        return true;
    }

    static void CheckVacant(string root)
    {
        var grants = Path.Combine(root, "public", "grants");
        if (File.Exists(Path.Combine(root, "bin", "config.json")) ||
            File.Exists(Path.Combine(root, "owner.json")) ||
            File.Exists(Path.Combine(root, "public", "status.json")) ||
            Directory.Exists(grants) && Directory.EnumerateFiles(grants, "*.json").Any())
            throw new IOException("An existing admission profile was found. Restore or repair its configuration before continuing.");
    }

    internal static void Provision(string root, GpuAdapter adapter)
    {
        AdmissionFiles.DirectoryFor(root, privateRoot: true);
        using var guardLock = new FileStream(Path.Combine(root, "guard.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        CheckVacant(root);
        foreach (var directory in new[] { "bin", "logs", "public/grants", "requests" })
            AdmissionFiles.DirectoryFor(Path.Combine(root, directory));
        AdmissionFiles.Write(Path.Combine(root, "modes.json"), new JsonObject
            { ["policy_mode"] = "Hold", ["consumer_modes"] = new JsonObject() });
        AdmissionFiles.Write(Path.Combine(root, "bin", "config.json"), GpuDiscovery.Configuration(root, adapter, new JsonObject()));
    }
}
