using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

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

sealed class TrayContext : IDisposable
{
    const double MiB = 1024.0 * 1024, GiB = MiB * 1024, TiB = GiB * 1024;

    readonly Settings _s = Settings.Load();
    readonly TrayIcon _tray;
    readonly ContextMenuStrip _menu = new();
    readonly HoverPopup _popup = new();
    readonly DispatcherTimer _timer;
    readonly DispatcherTimer _flashTimer;
    // Runs only while the mouse hovers the tray icon: confirms the cursor is still over it.
    readonly DispatcherTimer _hoverTimer;
    IntPtr _hIcon;
    string _text = "?";
    string _tip = "";
    Color _back = Color.Gray, _fore = Color.White;
    bool _isRed, _flashDark;

    public TrayContext()
    {
        _menu.Items.Add("Exit", null, (_, _) => System.Windows.Application.Current.Shutdown());

        _tray = new TrayIcon();
        _tray.PopupOpen += OnPopupOpen;
        _tray.PopupClose += EndHover;
        _tray.ContextMenuRequested += ShowMenu;

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _hoverTimer.Tick += (_, _) => { if (!CursorOverIcon()) EndHover(); };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(250, _s.UpdateIntervalSeconds * 1000)) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(50, _s.FlashIntervalSeconds * 1000)) };
        _flashTimer.Tick += (_, _) => { _flashDark = !_flashDark; if (_isRed) Redraw(); };
        if (_s.FlashIntervalSeconds > 0) _flashTimer.Start();
        Refresh();
    }

    void OnPopupOpen()
    {
        // Trust the shell's hover notification: its hot area can be larger than the icon rect
        // that Shell_NotifyIconGetRect reports, so don't second-guess it with a bounds check here.
        // If the rect is unavailable, anchor on the cursor instead.
        var rect = _tray.GetRect() ?? CursorRect();
        _popup.ShowNear(rect);
        _hoverTimer.Start();
    }

    static RECT CursorRect()
    {
        PInvoke.GetCursorPos(out var p);
        return new RECT { left = p.X - 8, top = p.Y - 8, right = p.X + 8, bottom = p.Y + 8 };
    }

    void EndHover()
    {
        _hoverTimer.Stop();
        _popup.HidePopup();
    }

    // Safety net for a missed NIN_POPUPCLOSE. The shell's hover area extends past the reported icon
    // rect (e.g. the highlighted cell in the overflow flyout), so test against a generously inflated
    // rect; NIN_POPUPCLOSE remains the primary exit signal. If the rect is unavailable, assume inside.
    bool CursorOverIcon()
    {
        if (_tray.GetRect() is not { } r) return true;
        int margin = Math.Max(r.Width, r.Height);
        return PInvoke.GetCursorPos(out var p)
            && p.X >= r.left - margin && p.X < r.right + margin
            && p.Y >= r.top - margin && p.Y < r.bottom + margin;
    }

    void ShowMenu(Point screenPoint)
    {
        EndHover();
        // The owner window must be foreground or the menu won't dismiss when clicking elsewhere.
        PInvoke.SetForegroundWindow(new HWND(_tray.Handle));
        _menu.Show(screenPoint);
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

        _tip = double.IsNaN(free) ? $"{_s.Drive} unavailable" : $"{_s.Drive} {free / GiB:0.00} GiB free";
        _tray.SetTip(_tip);
        _popup.Text = _tip + "\n\n(Top page-file users will be listed here.)";
    }

    void Redraw()
    {
        var back = _isRed && _flashDark ? Color.FromArgb(90, 0, 0) : _back;
        var old = _hIcon;
        using (var bmp = Render(_text, back, _fore, _s.HorizontalPaddingPercent, _s.VerticalPaddingPercent))
            _hIcon = bmp.GetHicon();
        _tray.SetIcon(_hIcon);
        if (old != IntPtr.Zero) PInvoke.DestroyIcon(new HICON(old));
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

    public void Dispose()
    {
        _timer.Stop();
        _flashTimer.Stop();
        _hoverTimer.Stop();
        _popup.Close();
        _tray.Dispose();
        _menu.Dispose();
        if (_hIcon != IntPtr.Zero) PInvoke.DestroyIcon(new HICON(_hIcon));
    }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize(); // sets PerMonitorV2 before WPF starts
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        using var tray = new TrayContext();
        app.Run();
    }
}
