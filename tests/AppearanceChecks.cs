using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

static class AppearanceChecks
{
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var appearance = new Appearance(directory);
        Require(appearance.Mode == AppearanceMode.System, "A new profile must follow Windows appearance.");
        appearance.Set(AppearanceMode.Dark);
        Require(new Appearance(directory).Mode == AppearanceMode.Dark, "Dark must survive reopening the app.");
        appearance.Set(AppearanceMode.Light);
        Require(new Appearance(directory).Mode == AppearanceMode.Light, "Light must survive reopening the app.");
        appearance.Set(AppearanceMode.System);
        Require(new Appearance(directory).Mode == AppearanceMode.System, "System must survive reopening the app.");

        Require(Appearance.Resolve(AppearanceMode.System, windowsDark: true, highContrast: false).Dark &&
            !Appearance.Resolve(AppearanceMode.System, windowsDark: false, highContrast: false).Dark,
            "System must follow both Windows app themes.");
        Require(Appearance.Resolve(AppearanceMode.Light, windowsDark: true, highContrast: false) == Palette.Light &&
            Appearance.Resolve(AppearanceMode.Dark, windowsDark: false, highContrast: false) == Palette.Night,
            "An explicit choice must override the Windows app theme.");
        var contrast = Appearance.Resolve(AppearanceMode.Dark, windowsDark: true, highContrast: true);
        Require(contrast.HighContrast && contrast.Page == SystemColors.Window && contrast.Text == SystemColors.WindowText,
            "Windows contrast themes must override the saved app palette.");

        foreach (var colors in new[] { Palette.Light, Palette.Night })
        {
            foreach (var (ink, fill) in new[] { (colors.Text, colors.Page), (colors.Muted, colors.Page),
                (colors.Text, colors.Chrome), (colors.Muted, colors.Chrome), (colors.Text, colors.Hover), (colors.Muted, colors.Hover),
                (colors.AccentText, colors.Accent), (colors.DisabledText, colors.Disabled), (colors.PauseText, colors.Pause) })
                Require(Ratio(ink, fill) >= 4.5, $"A text pair is below 4.5:1 in {(colors.Dark ? "Dark" : "Light")}.");
            foreach (var adjacent in new[] { colors.Page, colors.Chrome, colors.Hover })
                Require(Ratio(colors.Focus, adjacent) >= 3, "Keyboard focus must contrast with its surface.");
            Require(Ratio(colors.Border, colors.Page) >= 3 && Ratio(colors.Border, colors.Chrome) >= 3,
                "Button borders must be distinguishable from the page and header.");
            Console.WriteLine($"{(colors.Dark ? "Dark" : "Light")} disabled text: {Ratio(colors.DisabledText, colors.Disabled):0.00}:1");
        }

        File.WriteAllText(Path.Combine(directory, "appearance.json"), """{"theme":"invalid"}""");
        Require(new Appearance(directory).Mode == AppearanceMode.System, "Invalid preferences must fall back to System.");
        File.Delete(Path.Combine(directory, "appearance.json"));
        Console.WriteLine("PASS: saved appearance, Windows precedence, and contrast");
    }

    public static void Preview(string output, string iconPath)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Render(output, iconPath); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    static void Render(string output, string iconPath)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Directory.CreateDirectory(output);
        var root = Path.Combine(Path.GetTempPath(), "gpu-admission-ui-" + Guid.NewGuid().ToString("N"));
        foreach (var path in new[] { root, Path.Combine(root, "bin"), Path.Combine(root, "public") })
            Directory.CreateDirectory(path);
        AdmissionFiles.Write(Path.Combine(root, "bin", "config.json"), new JsonObject { ["consumers"] = new JsonObject
        {
            ["tdarr"] = new JsonObject { ["kind"] = "docker" },
            ["telecom-transcription"] = new JsonObject { ["kind"] = "docker" }
        } });
        AdmissionFiles.Write(Path.Combine(root, "modes.json"), new JsonObject { ["policy_mode"] = "Hold", ["consumer_modes"] = new JsonObject
        {
            ["tdarr"] = "Hold", ["telecom-transcription"] = "Hold"
        } });
        var theme = new Appearance(root);
        theme.Set(AppearanceMode.Dark);
        int paused = 0, resumed = 0, repaired = 0, logs = 0, quit = 0, canceled = 0;
        using var icon = new Icon(iconPath);
        using var window = new StatusWindow(theme, icon, () => paused++, () => resumed++, () => quit++,
            () => canceled++, () => logs++, () => repaired++, new WorkersPanel(root, theme, () => null, _ => { }));
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(-10000, -10000);
        window.ShowInTaskbar = false;
        var report = new DashboardStatus("Hold", "gpu-unavailable", "GPU telemetry is unavailable.",
            [new("Utilization", "Unavailable"), new("Free VRAM", "Unavailable")],
            [new("Tdarr", "Hold"), new("Telecom transcription", "Hold")],
            [new("Guard", "0.0% of one CPU, 82.5 MiB working set"), new("Tray", "0.0% of one CPU, 65.6 MiB working set"),
                new("Last GPU sample", "Unavailable"), new("Logging", "OK")],
            CanPause: false, CanResume: false, CanQuit: true, CanCancel: false, CanRepair: true);
        window.UpdateReadings(report);
        window.Show();
        Application.DoEvents();
        T Find<T>(string name) where T : Control => window.Controls.Find(name, true).OfType<T>().Single();
        Capture(window, Path.Combine(output, "dark.png"));
        var admission = Find<UiButton>("AdmissionAction");
        Require(!admission.Enabled && admission.Text == "Resume", "Resume must stay disabled without telemetry.");
        admission.PerformClick();
        Require(resumed == 0, "The repair layout must not resume a blocked guard.");
        Find<Button>("RepairGpu").PerformClick();
        Find<Button>("OpenLogs").PerformClick();
        Require(repaired == 1 && logs == 1, "Repair and logs must still invoke the tray actions.");
        Find<Button>("Diagnostics").PerformClick();
        Require(Find<CheckBox>("StartAtSignIn").Visible, "Diagnostics must expose the sign-in preference.");
        Capture(window, Path.Combine(output, "diagnostics.png"));
        Find<RadioButton>("WorkersTab").Checked = true;
        Require(Find<ListView>("WorkerList").Visible && !Find<CheckBox>("StartAtSignIn").Visible,
            "Worker navigation must leave Diagnostics.");
        Capture(window, Path.Combine(output, "workers.png"));
        Find<RadioButton>("OverviewTab").Checked = true;
        Find<RadioButton>("LightAppearance").Checked = true;
        Require(theme.Mode == AppearanceMode.Light && window.BackColor == Palette.Light.Page, "Light must repaint the open window.");
        Capture(window, Path.Combine(output, "light.png"));
        Find<RadioButton>("SystemAppearance").Checked = true;
        Require(theme.Mode == AppearanceMode.System && window.BackColor == theme.Colors.Page, "System selection must apply the current Windows palette.");
        Capture(window, Path.Combine(output, "system.png"));

        window.UpdateReadings(report with { Reason = "no-eligible-request", CanResume = true, CanRepair = false });
        admission.PerformClick();
        window.UpdateReadings(report with { State = "Auto", Reason = "no-eligible-request", CanPause = true, CanRepair = false });
        admission.PerformClick();
        Require(resumed == 1 && paused == 1, "The primary admission control must route both Pause and Resume.");
        window.UpdateReadings(report with { State = "Draining", CanQuit = false, CanCancel = true, CanRepair = false });
        Find<Button>("Quit").PerformClick();
        Find<Button>("CancelQuit").PerformClick();
        Require(quit == 0 && canceled == 1, "Draining must expose Cancel quit and block a second Quit.");
        window.Close();
        Console.WriteLine($"PASS: native dashboard controls, live themes, and captures at {window.DeviceDpi} DPI");
    }

    static void Capture(Form form, string target)
    {
        Application.DoEvents();
        form.Update();
        using var bitmap = new Bitmap(form.Width, form.Height);
        using var graphics = Graphics.FromImage(bitmap);
        var context = graphics.GetHdc();
        bool captured;
        try { captured = PrintWindow(form.Handle, context, 2); }
        finally { graphics.ReleaseHdc(context); }
        Require(captured, "Windows could not capture the rendered dashboard.");
        bitmap.Save(target, ImageFormat.Png);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PrintWindow(IntPtr window, IntPtr context, uint flags);

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static double Ratio(Color a, Color b)
    {
        static double Light(Color color)
        {
            static double Linear(byte component)
            {
                double value = component / 255.0;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        double first = Light(a), second = Light(b);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }
}
