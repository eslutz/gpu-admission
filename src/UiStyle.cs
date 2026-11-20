using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

class AppWindow : Form
{
    protected Appearance Theme { get; }
    bool themeRefreshQueued;

    public AppWindow(Appearance theme)
    {
        Theme = theme;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = UiStyle.Body;
        theme.Changed += RefreshColors;
    }

    protected virtual void RefreshColors()
    {
        var colors = Theme.Colors;
        UiStyle.Apply(this, colors);
        if (IsHandleCreated && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            int dark = colors.Dark ? 1 : 0;
            int caption = colors.HighContrast ? -1 : ColorTranslator.ToWin32(colors.Chrome);
            int text = colors.HighContrast ? -1 : ColorTranslator.ToWin32(colors.Text);
            DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
            DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
            DwmSetWindowAttribute(Handle, 36, ref text, sizeof(int));
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RefreshColors();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        Theme.Refresh();
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg is not (0x0015 or 0x001A or 0x031A) || themeRefreshQueued || !IsHandleCreated || IsDisposed)
            return;
        themeRefreshQueued = true;
        BeginInvoke(() =>
        {
            themeRefreshQueued = false;
            if (!IsDisposed) Theme.Refresh();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= RefreshColors;
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int bytes);
}

static class UiStyle
{
    static readonly string FontName = FontFamily.Families.Any(f => f.Name == "Segoe UI Variable Text")
        ? "Segoe UI Variable Text" : "Segoe UI";
    public static readonly Font Body = new(FontName, 11f);
    public static readonly Font Section = new(FontName, 15f, FontStyle.Bold);
    public static readonly Font Heading = new(FontName, 19f, FontStyle.Bold);
    public static readonly Font Emphasis = new(FontName, 11f, FontStyle.Bold);
    public static readonly Font Symbols = new(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
        ? "Segoe Fluent Icons" : "Segoe MDL2 Assets", 13f);

    public static void Apply(Control control, Palette colors)
    {
        control.ForeColor = colors.Text;
        control.BackColor = colors.Page;
        if (control is UiButton button) button.Colors = colors;
        if (control is ChoiceButton option) option.Colors = colors;
        if (control is FlowLayoutPanel or TableLayoutPanel) control.BackColor = Color.Transparent;
        foreach (Control child in control.Controls) Apply(child, colors);
        control.Invalidate();
    }

    public static void PaintButton(Graphics graphics, Rectangle bounds, int radius, Color fill, Color border)
    {
        if (bounds.Width <= 1 || bounds.Height <= 1) return;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using GraphicsPath shape = new();
        shape.AddRoundedRectangle(new Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1),
            new Size(radius, radius));
        using var brush = new SolidBrush(fill);
        graphics.FillPath(brush, shape);
        using var edge = new Pen(border);
        graphics.DrawPath(edge, shape);
    }

    public static void PaintLabel(Graphics graphics, Rectangle bounds, string text, Font font, Color color) =>
        TextRenderer.DrawText(graphics, text, font, bounds, color, TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
            TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping);

    public static void PaintFocus(Graphics graphics, Rectangle bounds, Color color, int scale)
    {
        bounds.Inflate(-4 * scale, -4 * scale);
        graphics.SmoothingMode = SmoothingMode.None;
        using var pen = new Pen(color, 2 * scale) { DashStyle = DashStyle.Dot };
        graphics.DrawRectangle(pen, bounds);
    }
}

sealed class UiButton : Button
{
    bool hovered;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Primary { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Palette Colors { get; set; } = Palette.Light;

    public UiButton()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FlatStyle = FlatStyle.Flat;
        MinimumSize = new Size(78, 36);
        Padding = new Padding(10, 0, 10, 0);
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var colors = Colors;
        var fill = !Enabled ? colors.Disabled : Primary ? colors.Accent : hovered ? colors.Hover : colors.Page;
        var ink = !Enabled ? colors.DisabledText : Primary ? colors.AccentText : colors.Text;
        var border = !Enabled && !colors.HighContrast ? fill : Enabled && Primary ? colors.Accent : colors.Border;
        int scale = Math.Max(1, (int)Math.Round(DeviceDpi / 96d));
        UiStyle.PaintButton(e.Graphics, ClientRectangle, 7 * scale, fill, border);
        UiStyle.PaintLabel(e.Graphics, ClientRectangle, Text, Primary ? UiStyle.Emphasis : Font, ink);
        if (Focused && ShowFocusCues)
            UiStyle.PaintFocus(e.Graphics, ClientRectangle, Primary ? colors.AccentText : colors.Focus, scale);
    }
}

sealed class ChoiceButton : RadioButton
{
    bool hovered;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Palette Colors { get; set; } = Palette.Light;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Navigation { get; init; }

    public ChoiceButton()
    {
        Appearance = System.Windows.Forms.Appearance.Button;
        FlatStyle = FlatStyle.Flat;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        int scale = Math.Max(1, (int)Math.Round(DeviceDpi / 96d));
        var colors = Colors;
        if (Navigation)
        {
            using var fill = new SolidBrush(hovered ? colors.Hover : colors.Chrome);
            e.Graphics.FillRectangle(fill, ClientRectangle);
            if (Checked)
            {
                using var marker = new SolidBrush(colors.Accent);
                e.Graphics.FillRectangle(marker, 2 * scale, Height - 3 * scale, Width - 4 * scale, 3 * scale);
            }
            UiStyle.PaintLabel(e.Graphics, ClientRectangle, Text, Checked ? UiStyle.Emphasis : Font,
                Checked ? colors.Text : colors.Muted);
        }
        else
        {
            var fill = Checked ? colors.Accent : hovered ? colors.Hover : colors.Chrome;
            UiStyle.PaintButton(e.Graphics, ClientRectangle, 6 * scale, fill, Checked ? fill : colors.Border);
            UiStyle.PaintLabel(e.Graphics, ClientRectangle, Text, Checked ? UiStyle.Emphasis : Font,
                Checked ? colors.AccentText : colors.Text);
        }
        if (Focused && ShowFocusCues)
            UiStyle.PaintFocus(e.Graphics, ClientRectangle, Checked && !Navigation ? colors.AccentText : colors.Focus, scale);
    }
}

sealed class StatusBadge : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Palette Colors { get; set; } = Palette.Light;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Paused { get; set; } = true;

    public StatusBadge()
    {
        TabStop = false;
        AccessibleRole = AccessibleRole.Graphic;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var fill = Paused ? Colors.Pause : Colors.Accent;
        var ink = Paused ? Colors.PauseText : Colors.AccentText;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(fill);
        e.Graphics.FillEllipse(brush, ClientRectangle);
        using var pen = new Pen(Colors.HighContrast ? Colors.Border : fill);
        e.Graphics.DrawEllipse(pen, 0, 0, Width - 1, Height - 1);
        // Segoe Fluent Icons' standard Play and Pause symbols.
        UiStyle.PaintLabel(e.Graphics, ClientRectangle, Paused ? "\uE769" : "\uE768", UiStyle.Symbols, ink);
    }
}
