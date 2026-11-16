using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Windows.Win32.UI.Shell;

namespace DiskSpaceTray;

// A notification-area icon using Shell_NotifyIcon directly with NOTIFYICON_VERSION_4, which
// (unlike WinForms' NotifyIcon) reports hover (NIN_POPUPOPEN) and hover-exit (NIN_POPUPCLOSE).
// Without NIF_SHOWTIP the shell shows no standard tooltip, leaving that to the app's own popup.
sealed unsafe class TrayIcon : NativeWindow, IDisposable
{
    const uint IconId = 1;
    const uint CallbackMessage = PInvoke.WM_APP + 1;

    // Registered before the window exists so a 0 never matches WM_NULL during creation.
    readonly uint _taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");
    IntPtr _hIcon;
    string _tip = "";
    bool _added;

    public event Action? PopupOpen;
    public event Action? PopupClose;
    /// <summary>Screen position (physical pixels) at which to show the context menu.</summary>
    public event Action<Point>? ContextMenuRequested;

    public TrayIcon()
    {
        // A hidden top-level window (not message-only) so it receives the TaskbarCreated broadcast.
        CreateHandle(new CreateParams());
    }

    /// <summary>The caller keeps ownership of the icon handle and must outlive the next SetIcon call.</summary>
    public void SetIcon(IntPtr hIcon)
    {
        _hIcon = hIcon;
        if (_added) Notify(NOTIFY_ICON_MESSAGE.NIM_MODIFY);
        else Add();
    }

    public void SetTip(string tip)
    {
        _tip = tip.Length > 127 ? tip[..127] : tip;
        if (_added) Notify(NOTIFY_ICON_MESSAGE.NIM_MODIFY);
    }

    /// <summary>The icon's current bounds in physical screen pixels, or null if the shell can't say.</summary>
    public RECT? GetRect()
    {
        var id = new NOTIFYICONIDENTIFIER { hWnd = new HWND(Handle), uID = IconId };
        id.cbSize = (uint)sizeof(NOTIFYICONIDENTIFIER);
        return PInvoke.Shell_NotifyIconGetRect(id, out var rect).Succeeded ? rect : null;
    }

    void Add()
    {
        if (_hIcon == IntPtr.Zero) return;
        _added = Notify(NOTIFY_ICON_MESSAGE.NIM_ADD);
        if (_added) Notify(NOTIFY_ICON_MESSAGE.NIM_SETVERSION);
    }

    bool Notify(NOTIFY_ICON_MESSAGE message)
    {
        var data = new NOTIFYICONDATAW
        {
            hWnd = new HWND(Handle),
            uID = IconId,
            uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP,
            uCallbackMessage = CallbackMessage,
            hIcon = new HICON(_hIcon),
        };
        data.cbSize = (uint)sizeof(NOTIFYICONDATAW);
        data.Anonymous.uVersion = PInvoke.NOTIFYICON_VERSION_4;
        _tip.AsSpan().CopyTo(data.szTip.AsSpan()); // _tip is capped at 127 chars; the rest stays NUL
        return PInvoke.Shell_NotifyIcon(message, data);
    }

    protected override void WndProc(ref Message m)
    {
        if ((uint)m.Msg == CallbackMessage)
        {
            // Version 4: LOWORD(lParam) is the event, wParam is the anchor point (signed 16-bit x/y).
            uint evt = (uint)((long)m.LParam & 0xFFFF);
            if (evt == PInvoke.NIN_POPUPOPEN) PopupOpen?.Invoke();
            else if (evt == PInvoke.NIN_POPUPCLOSE) PopupClose?.Invoke();
            else if (evt == PInvoke.WM_CONTEXTMENU)
            {
                long w = (long)m.WParam;
                ContextMenuRequested?.Invoke(new Point((short)(w & 0xFFFF), (short)((w >> 16) & 0xFFFF)));
            }
            return;
        }
        if ((uint)m.Msg == _taskbarCreated)
        {
            // Explorer restarted; the icon is gone, so add it again.
            _added = false;
            Add();
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_added) Notify(NOTIFY_ICON_MESSAGE.NIM_DELETE);
        _added = false;
        DestroyHandle();
    }
}
