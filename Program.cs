using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DiskSpaceTray;

sealed class Settings
{
    public string Drive { get; set; } = "C:\\";
    public double RedBelowGiB { get; set; } = 2;
    public double YellowBelowGiB { get; set; } = 5;
    public double UpdateIntervalSeconds { get; set; } = 5;
    // Percent of icon width reserved on each of the left and right sides.
    // Seconds between red/dark-red alternation while in the red; 0 disables flashing.
    public double FlashIntervalSeconds { get; set; } = 0.5;
    public double HorizontalPaddingPercent { get; set; } = 3;
    // Percent of icon height reserved on each of the top and bottom sides.
    public double VerticalPaddingPercent { get; set; } = 3;

    public static Settings Load()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(path))
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), opts) ?? new Settings();
            }
        }
        catch { /* fall back to defaults */ }
        return new Settings();
    }
}

sealed class TrayContext : ApplicationContext
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

    const double MiB = 1024.0 * 1024, GiB = MiB * 1024, TiB = GiB * 1024;

    readonly Settings _s = Settings.Load();
    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _timer;
    readonly System.Windows.Forms.Timer _flashTimer;
    IntPtr _hIcon;
    string _text = "?";
    Color _back = Color.Gray, _fore = Color.White;
    bool _isRed, _flashDark;

    public TrayContext()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _timer = new System.Windows.Forms.Timer { Interval = (int)Math.Max(250, _s.UpdateIntervalSeconds * 1000) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        _flashTimer = new System.Windows.Forms.Timer { Interval = (int)Math.Max(50, _s.FlashIntervalSeconds * 1000) };
        _flashTimer.Tick += (_, _) => { _flashDark = !_flashDark; if (_isRed) Redraw(); };
        if (_s.FlashIntervalSeconds > 0) _flashTimer.Start();
        Refresh();
    }

    void Refresh()
    {
        double free;
        try { free = new DriveInfo(_s.Drive).AvailableFreeSpace; }
        catch { free = double.NaN; }

        string text; Color back, fore;
        bool red = false;
        if (double.IsNaN(free))
        {
            text = "?"; back = Color.Gray; fore = Color.White;
        }
        else
        {
            text = Format(free);
            double gib = free / GiB;
            if (gib < _s.RedBelowGiB) { back = Color.FromArgb(220, 0, 0); fore = Color.White; red = true; }
            else if (gib < _s.YellowBelowGiB) { back = Color.FromArgb(255, 210, 0); fore = Color.Black; }
            else { back = Color.FromArgb(0, 130, 0); fore = Color.White; }
        }

        (_text, _back, _fore, _isRed) = (text, back, fore, red);
        if (!red) _flashDark = false;
        Redraw();

        var tip = double.IsNaN(free) ? $"{_s.Drive} unavailable" : $"{_s.Drive} {free / GiB:0.00} GiB free";
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    void Redraw()
    {
        var back = _isRed && _flashDark ? Color.FromArgb(90, 0, 0) : _back;
        var old = _hIcon;
        using (var bmp = Render(_text, back, _fore, _s.HorizontalPaddingPercent, _s.VerticalPaddingPercent))
            _hIcon = bmp.GetHicon();
        _tray.Icon = Icon.FromHandle(_hIcon);
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    // At least 2 significant digits, unit letter M (MiB), G (GiB) or T (TiB).
    static string Format(double bytes)
    {
        (double v, string u) = bytes >= TiB ? (bytes / TiB, "T")
                             : bytes >= GiB ? (bytes / GiB, "G")
                             : (bytes / MiB, "M");
        // Rounding may carry into the next unit (e.g. 1023.6M -> 1024M); promote.
        if (u == "M" && v >= 1000) (v, u) = (bytes / GiB, "G");
        else if (u == "G" && v >= 1000) (v, u) = (bytes / TiB, "T");
        string num = v < 10 ? v.ToString("0.0", CultureInfo.InvariantCulture)
                            : Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
        return num + u;
    }

    // Renders the text as a vector path scaled so its ink fills the icon:
    // full height, horizontally condensed (never stretched) to fit the width.
    static Bitmap Render(string text, Color back, Color fore, double hPadPct, double vPadPct)
    {
        var size = SystemInformation.SmallIconSize;
        int w = size.Width, h = size.Height;
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using var path = new GraphicsPath();
        using var family = new FontFamily("Segoe UI");
        path.AddString(text, family, (int)FontStyle.Bold, 100f, PointF.Empty, StringFormat.GenericTypographic);
        var ink = path.GetBounds();

        // Clamp so the drawable area always keeps at least 10% of each dimension.
        float padX = (float)(Math.Clamp(hPadPct, 0, 45) / 100 * w);
        float padY = (float)(Math.Clamp(vPadPct, 0, 45) / 100 * h);
        float sy = (h - 2 * padY) / ink.Height;
        float sx = Math.Min((w - 2 * padX) / ink.Width, sy);
        float drawW = ink.Width * sx, drawH = ink.Height * sy;

        var m = new Matrix();
        m.Translate(-ink.X, -ink.Y);
        m.Scale(sx, sy, MatrixOrder.Append);
        m.Translate((w - drawW) / 2, (h - drawH) / 2, MatrixOrder.Append);
        path.Transform(m);

        using var brush = new SolidBrush(fore);
        g.FillPath(brush, path);
        return bmp;
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _flashTimer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        base.ExitThreadCore();
    }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}
