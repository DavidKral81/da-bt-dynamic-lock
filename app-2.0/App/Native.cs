using System;
using System.Runtime.InteropServices;

namespace DaBtDynamicLock.App;

/// <summary>
/// Raw Win32 the interface needs. WinUI 3 has no tray API at all, so the icon,
/// its screen rectangle and the panel placement all come from here.
///
/// Every function that returns or takes a handle is typed as nint. The
/// Python app had the same class of bug (ctypes reading a HWND as a 32bit
/// int) - here the type system prevents it.
/// </summary>
internal static class Native
{
    // ---- window class / message loop -------------------------------------

    public const int WM_DESTROY = 0x0002;
    public const int WM_APP = 0x8000;
    public const int WM_TRAYCALLBACK = WM_APP + 1;

    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONUP = 0x0205;
    public const int NIN_SELECT = WM_APP + 0;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint GetModuleHandleW(string? lpModuleName);

    // ---- tray icon --------------------------------------------------------

    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;
    public const uint NIM_SETVERSION = 0x00000004;

    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;
    public const uint NIF_STATE = 0x00000008;
    public const uint NIF_SHOWTIP = 0x00000080;

    public const uint NOTIFYICON_VERSION_4 = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;   // union with uTimeout
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [StructLayout(LayoutKind.Sequential)]
    public struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public Guid guidItem;
    }

    /// <summary>Where the tray icon actually sits, so the panel can point at it.</summary>
    [DllImport("shell32.dll")]
    public static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    // ---- message box ------------------------------------------------------
    //
    // The one place this app shows a modal dialog: when a second copy will not
    // start. There is no tray icon to notify from yet at that point.

    public const uint MB_OK = 0x00000000;
    public const uint MB_ICONINFORMATION = 0x00000040;
    public const uint MB_SETFOREGROUND = 0x00010000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    // ---- balloon / notification ------------------------------------------

    public const uint NIF_INFO = 0x00000010;
    public const uint NIIF_INFO = 0x00000001;

    // ---- popup menu -------------------------------------------------------
    //
    // Win32 rather than a XAML MenuFlyout: a flyout needs a window to live in,
    // and this menu belongs to an icon, not to a window. The hidden window the
    // tray icon already owns is a plain one (not HWND_MESSAGE) for exactly this
    // reason - a message-only window cannot own a popup menu.

    public const uint MF_STRING = 0x00000000;
    public const uint MF_SEPARATOR = 0x00000800;
    public const uint MF_CHECKED = 0x00000008;
    public const uint MF_GRAYED = 0x00000001;

    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_NONOTIFY = 0x0080;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenuW(nint hMenu, uint uFlags, nuint uIDNewItem,
        string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int TrackPopupMenuEx(nint hMenu, uint uFlags, int x, int y,
        nint hwnd, nint lptpm);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    /// <summary>
    /// Needed after a tray menu closes. Without it the menu stays on screen
    /// when the user clicks elsewhere - a documented quirk of menus owned by a
    /// window that is not in the foreground.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern nint PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    public const uint WM_NULL = 0x0000;

    // ---- icons ------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(nint hIcon);

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    public const uint BI_RGB = 0;
    public const uint DIB_RGB_COLORS = 0;

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern nint CreateDIBSection(
        nint hdc, ref BITMAPINFOHEADER pbmi, uint usage,
        out nint ppvBits, nint hSection, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern nint CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, nint lpBits);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(nint hObject);

    // ---- screen capture (how the look gets checked, see tests) -----------

    public const uint PW_RENDERFULLCONTENT = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PrintWindow(nint hwnd, nint hdcBlt, uint nFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern nint SelectObject(nint hdc, nint h);

    // ---- window geometry / state -----------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public override string ToString() => $"{Left},{Top} {Width}x{Height}";
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLongW(nint hWnd, int nIndex);

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOPMOST = 0x00000008;

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(nint hWnd);

    // ---- monitor the tray lives on ---------------------------------------

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    public static extern nint MonitorFromRect(ref RECT lprc, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEXW
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEXW lpmi);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
