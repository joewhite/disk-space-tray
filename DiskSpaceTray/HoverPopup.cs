using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DiskSpaceTray;

// Borderless, non-activating, click-through WPF window shown next to the tray icon while hovered.
// Positioning is done in physical pixels with SetWindowPos against the icon's monitor, so it is
// correct on mixed-DPI setups; WPF's Left/Top (DIPs) are deliberately not used.
sealed class HoverPopup : Window
{
    readonly Border _root;
    readonly TextBlock _text;
    RECT _anchor;

    public HoverPopup()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;

        _text = new TextBlock
        {
            Margin = new Thickness(10, 8, 10, 8),
            Foreground = System.Windows.SystemColors.WindowTextBrush,
        };
        _root = new Border
        {
            Background = System.Windows.SystemColors.WindowBrush,
            BorderBrush = System.Windows.SystemColors.ActiveBorderBrush,
            BorderThickness = new Thickness(1),
            Child = _text,
        };
        Content = _root;

        // Never take focus or mouse input (so it can't disturb the hover state of the tray icon).
        SourceInitialized += (_, _) =>
        {
            var hwnd = new HWND(new WindowInteropHelper(this).Handle);
            nint ex = PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            ex |= (nint)(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TRANSPARENT);
            PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, ex);
        };

        // Content size or monitor DPI changed after we positioned: place it again, once layout settles.
        SizeChanged += (_, _) => RepositionLater();
        DpiChanged += (_, _) => RepositionLater();
    }

    public string Text
    {
        get => _text.Text;
        set => _text.Text = value;
    }

    /// <summary>Shows the popup next to <paramref name="iconRect"/> (physical screen pixels).</summary>
    public void ShowNear(RECT iconRect)
    {
        _anchor = iconRect;
        var hwnd = new HWND(new WindowInteropHelper(this).EnsureHandle());
        // Park the window on the icon's monitor first so WPF adopts that monitor's DPI.
        PInvoke.SetWindowPos(hwnd, HWND.HWND_TOPMOST, iconRect.left, iconRect.top, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        Reposition();
        if (!IsVisible) Show();
        Reposition(); // now using the real, laid-out size
    }

    public void HidePopup()
    {
        if (IsVisible) Hide();
    }

    void RepositionLater()
    {
        if (IsVisible) Dispatcher.BeginInvoke(DispatcherPriority.Render, Reposition);
    }

    void Reposition()
    {
        var hwnd = new HWND(new WindowInteropHelper(this).Handle);
        if (hwnd == HWND.Null) return;

        var monitor = PInvoke.MonitorFromRect(_anchor, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        double scale = PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _).Succeeded
            ? dpiX / 96.0 : 1.0;

        int w, h;
        if (IsVisible && PInvoke.GetWindowRect(hwnd, out var cur))
        {
            (w, h) = (cur.Width, cur.Height);
        }
        else
        {
            // Not shown yet: estimate the physical size from the content's DIP size.
            _root.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            w = (int)Math.Ceiling(_root.DesiredSize.Width * scale);
            h = (int)Math.Ceiling(_root.DesiredSize.Height * scale);
        }

        var mi = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!PInvoke.GetMonitorInfo(monitor, ref mi)) return;
        var work = mi.rcWork;

        int gap = (int)Math.Round(8 * scale);
        int x = (_anchor.left + _anchor.right) / 2 - w / 2;
        // Open away from the taskbar: below an icon in the top half of the work area, else above.
        bool below = (_anchor.top + _anchor.bottom) / 2 < (work.top + work.bottom) / 2;
        int y = below ? _anchor.bottom + gap : _anchor.top - gap - h;

        x = Math.Clamp(x, work.left, Math.Max(work.left, work.right - w));
        y = Math.Clamp(y, work.top, Math.Max(work.top, work.bottom - h));

        PInvoke.SetWindowPos(hwnd, HWND.HWND_TOPMOST, x, y, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }
}
