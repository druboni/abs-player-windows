using System.Runtime.InteropServices;

namespace AbsPlayer.App;

/// <summary>
/// The notification-area icon, so the app keeps playing after its window is closed. Click it to bring the window back;
/// right-click for Open / Play-Pause / Quit. Plain Shell_NotifyIcon on a hidden window of its own, created on the UI thread,
/// so its messages arrive there too. It also hears Windows signing out, which the hidden main window can't act on.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP_TRAY = 0x8001, WM_COMMAND = 0x0111, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205,
        WM_CONTEXTMENU = 0x007B, WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016, NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const uint NOTIFYICON_VERSION_4 = 4, NIIF_USER = 4, NIIF_LARGE_ICON = 0x20;
    private const int CmdOpen = 1, CmdToggle = 2, CmdQuit = 3;

    private readonly WndProc _proc; // Kept alive: Windows calls it for as long as the window exists.
    private readonly IntPtr _hwnd;
    private readonly IntPtr _icon;
    private readonly uint _taskbarCreated;
    private string _tip = "Homeshelf";
    private bool _added;

    public event EventHandler? OpenRequested;
    public event EventHandler? ToggleRequested;
    public event EventHandler? QuitRequested;

    /// <summary>Asked when the menu opens: whether something is loaded, and whether it's playing.</summary>
    public Func<(bool Loaded, bool Playing)> PlaybackState { get; set; } = () => (false, false);

    public TrayIcon(string iconPath)
    {
        _proc = OnMessage;
        var cls = new WNDCLASS { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), lpszClassName = "HomeshelfTray", hInstance = GetModuleHandle(null) };
        RegisterClass(ref cls);
        // A real (never shown) top-level window, not a message-only one: only these hear "TaskbarCreated" and sign-out.
        _hwnd = CreateWindowEx(0, "HomeshelfTray", "Homeshelf", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _icon = LoadImage(IntPtr.Zero, iconPath, 1 /* IMAGE_ICON */, GetSystemMetrics(49 /* SM_CXSMICON */), GetSystemMetrics(50), 0x10 /* LR_LOADFROMFILE */);
    }

    public void Show()
    {
        if (_added) return;
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
        data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
    }

    public void Hide()
    {
        if (!_added) return;
        var data = Data(0);
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _added = false;
    }

    /// <summary>The hover text, e.g. the book that's playing.</summary>
    public string Tooltip
    {
        set
        {
            _tip = value.Length > 127 ? value[..126] + "…" : value;
            if (!_added) return;
            var data = Data(NIF_TIP | NIF_SHOWTIP);
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
    }

    /// <summary>A Windows notification from the icon.</summary>
    public void Notify(string title, string text)
    {
        if (!_added) return;
        var data = Data(NIF_INFO);
        data.szInfoTitle = title;
        data.szInfo = text;
        data.dwInfoFlags = NIIF_USER | NIIF_LARGE_ICON;
        data.hBalloonIcon = _icon;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_APP_TRAY,
        hIcon = _icon,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr OnMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_TRAY)
        {
            // Version 4: the event is in the low word of lParam; the click point is in wParam.
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case WM_LBUTTONUP or NIN_SELECT or NIN_KEYSELECT:
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case WM_RBUTTONUP or WM_CONTEXTMENU:
                    ShowMenu((short)(wParam.ToInt64() & 0xFFFF), (short)((wParam.ToInt64() >> 16) & 0xFFFF));
                    break;
            }
            return IntPtr.Zero;
        }
        if (msg == _taskbarCreated && _added)
        {
            // Explorer restarted and forgot the icon.
            _added = false;
            Show();
            return IntPtr.Zero;
        }
        if (msg == WM_QUERYENDSESSION) return 1;
        if (msg == WM_ENDSESSION && wParam != IntPtr.Zero)
        {
            QuitRequested?.Invoke(this, EventArgs.Empty);
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        var (loaded, playing) = PlaybackState();
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, CmdOpen, "Open Homeshelf");
        if (loaded) AppendMenu(menu, 0, CmdToggle, playing ? "Pause" : "Play");
        AppendMenu(menu, 0x800 /* MF_SEPARATOR */, 0, null);
        AppendMenu(menu, 0, CmdQuit, "Quit");
        SetMenuDefaultItem(menu, CmdOpen, 0);
        // Without this the menu won't close when you click elsewhere.
        SetForegroundWindow(_hwnd);
        var cmd = TrackPopupMenuEx(menu, 0x100 /* TPM_RETURNCMD */ | 0x20 /* TPM_BOTTOMALIGN */, x, y, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        switch (cmd)
        {
            case CmdOpen: OpenRequested?.Invoke(this, EventArgs.Empty); break;
            case CmdToggle: ToggleRequested?.Invoke(this, EventArgs.Empty); break;
            case CmdQuit: QuitRequested?.Invoke(this, EventArgs.Empty); break;
        }
    }

    public void Dispose()
    {
        Hide();
        DestroyWindow(_hwnd);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WNDCLASS cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string? text);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(IntPtr menu, int item, int byPosition);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
