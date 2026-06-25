using System;
using System.Runtime.InteropServices;

namespace BHServe.App.Services;

/// <summary>
/// Minimal system-tray icon via raw Shell_NotifyIcon (no third-party package — the
/// WinUI XAML compiler choked on H.NotifyIcon's markup). Hosts a message-only window
/// on the UI thread so the WinUI message pump delivers the click callbacks.
/// Left-click / double-click → <see cref="OpenRequested"/>; right-click → a popup
/// menu (Open / Quit) raising <see cref="OpenRequested"/> / <see cref="QuitRequested"/>.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    public event Action? OpenRequested;
    public event Action? QuitRequested;

    private const int WM_APP = 0x8000;
    private const int WM_TRAY = WM_APP + 1;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_COMMAND = 0x0111;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10;
    private const uint TPM_RIGHTBUTTON = 0x0002, TPM_RETURNCMD = 0x0100;
    private const int IDI_APPLICATION = 32512;
    private const int CMD_OPEN = 1, CMD_QUIT = 2;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly WndProcDelegate _wndProc;   // kept alive so it isn't GC'd
    private readonly IntPtr _hwnd;
    private readonly string _className = "BHServeTrayWnd";
    private readonly string _tip;
    private bool _added;

    public TrayIcon(string tooltip, string? iconPath = null)
    {
        _tip = tooltip;
        _wndProc = WndProc;
        var wc = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = _className,
        };
        RegisterClass(ref wc);
        _hwnd = CreateWindowEx(0, _className, "BHServeTray", 0, 0, 0, 0, 0,
                               HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        var data = NewData(tooltip);
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_TRAY;
        // Use the app icon (16x16 for the tray) if we have it; else a stock icon.
        var loaded = iconPath is not null && File.Exists(iconPath)
            ? LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE)
            : IntPtr.Zero;
        data.hIcon = loaded != IntPtr.Zero ? loaded : LoadIcon(IntPtr.Zero, IDI_APPLICATION);
        data.szTip = tooltip;
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private NOTIFYICONDATA NewData(string tip) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        szTip = tip,
        szInfo = "", szInfoTitle = "",   // ByValTStr fields must be non-null
    };

    /// <summary>Show a balloon/toast from the tray icon (used once to reveal where the app went
    /// when it minimizes to tray — Windows 11 tucks new tray icons into the hidden-icons overflow).</summary>
    public void ShowBalloon(string title, string text)
    {
        if (!_added) return;
        var d = NewData(_tip);
        d.uFlags = NIF_INFO;
        d.szInfoTitle = title;
        d.szInfo = text;
        d.dwInfoFlags = 0;   // NIIF_NONE
        try { Shell_NotifyIcon(NIM_MODIFY, ref d); } catch { }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAY)
        {
            var ev = (int)lParam & 0xFFFF;
            if (ev is WM_LBUTTONUP or WM_LBUTTONDBLCLK) OpenRequested?.Invoke();
            else if (ev == WM_RBUTTONUP) ShowMenu();
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, CMD_OPEN, "Open BHServe");
        AppendMenu(menu, 0x800, 0, null);          // MF_SEPARATOR
        AppendMenu(menu, 0, CMD_QUIT, "Quit");
        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd);                 // so the menu dismisses on click-away
        var cmd = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        if (cmd == CMD_OPEN) OpenRequested?.Invoke();
        else if (cmd == CMD_QUIT) QuitRequested?.Invoke();
    }

    public void Dispose()
    {
        if (_added) { var d = NewData(""); Shell_NotifyIcon(NIM_DELETE, ref d); _added = false; }
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        UnregisterClass(_className, GetModuleHandle(null));
    }

    // ── interop ──────────────────────────────────────────────────────────────
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASS
    {
        public uint style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra;
        public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize; public IntPtr hWnd; public int uID; public uint uFlags;
        public int uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState; public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true)] private static extern ushort RegisterClass(ref WNDCLASS wc);
    [DllImport("user32.dll")] private static extern bool UnregisterClass(string cls, IntPtr hInst);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(
        uint exStyle, string cls, string name, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr LoadIcon(IntPtr inst, int name);
    private const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x0010;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr inst, string name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string? item);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int res, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
