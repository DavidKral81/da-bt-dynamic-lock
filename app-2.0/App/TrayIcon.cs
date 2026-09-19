using System;
using System.Runtime.InteropServices;

namespace DaBtDynamicLock.App;

/// <summary>
/// The icon near the clock. WinUI 3 ships no API for this at all, so it is a
/// hidden window plus Shell_NotifyIcon - the same thirty year old mechanism
/// pystray uses underneath in the Python version.
///
/// Proven by the throwaway prototype before this was written: the icon appears,
/// swaps while the app runs, and its screen rectangle can be asked for.
/// </summary>
internal sealed partial class TrayIcon : IDisposable
{
    private const uint IconId = 1;

    private readonly Native.WndProc _wndProc;   // kept alive on purpose: if the
                                                // delegate is collected, the
                                                // callback crashes at random
    private readonly nint _hwnd;
    private readonly uint _taskbarCreated;
    private nint _hIcon;
    private string _tip = string.Empty;
    private bool _added;

    public event Action? LeftClicked;
    public event Action? RightClicked;

    /// <summary>
    /// Said when the icon has to look after itself - putting itself back after
    /// the shell rebuilt the tray. Nobody is calling in at that moment, so
    /// there is no return value to report through.
    /// </summary>
    public event Action<string>? Trouble;

    public nint WindowHandle => _hwnd;

    public TrayIcon(string className)
    {
        _wndProc = WindowProc;

        var wc = new Native.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = Native.GetModuleHandleW(null),
            lpszClassName = className,
        };

        if (Native.RegisterClassExW(ref wc) == 0)
            throw new InvalidOperationException($"RegisterClassExW failed: {Marshal.GetLastWin32Error()}");

        // A plain hidden window, not HWND_MESSAGE: a message-only window cannot
        // own a popup menu, and the real app will need one.
        _hwnd = Native.CreateWindowExW(0, className, AppInfo.Name, 0,
            0, 0, 0, 0, 0, 0, Native.GetModuleHandleW(null), 0);

        if (_hwnd == 0)
            throw new InvalidOperationException($"CreateWindowExW failed: {Marshal.GetLastWin32Error()}");

        // Asked for AFTER the window exists, so the message can never arrive
        // before there is somewhere to receive it.
        _taskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");
    }

    private nint WindowProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        // The tray was rebuilt (the shell restarted, most often), so the icon
        // that was in it is gone and only this app can put it back. Nothing
        // listened for this before, and the app measured on 18.09.2026 what
        // that costs: every update afterwards talked to an icon that no longer
        // existed, failed, and left one more icon behind - 3305 of them, until
        // Windows would hand out no more handles and the app died.
        //
        // _taskbarCreated is 0 until the constructor has asked for the number,
        // and no real message is 0, so the check cannot match by accident.
        if (msg != 0 && msg == _taskbarCreated)
        {
            _added = false;                 // next Show() adds rather than modifies
            try
            {
                // Put back at once, with the icon already in hand. Waiting for
                // the next status change would leave the tray empty for as
                // long as nothing happens - and the icon is how the app is
                // reached at all.
                if (_hIcon != 0)
                    Show(_hIcon, _tip);
            }
            catch (Exception e)
            {
                // Never let this out: an exception thrown from a window
                // procedure crosses back into Windows and takes the process
                // with it. Reported instead, so the log says the icon is gone.
                Trouble?.Invoke($"The tray icon could not be put back after the tray was rebuilt ({e.Message}).");
            }
            return 0;
        }

        if (msg == Native.WM_TRAYCALLBACK)
        {
            // With NOTIFYICON_VERSION_4 the event is in the low word of lParam.
            uint evt = (uint)(lParam.ToInt64() & 0xFFFF);
            if (evt == Native.WM_LBUTTONUP || evt == Native.NIN_SELECT) LeftClicked?.Invoke();
            else if (evt == Native.WM_RBUTTONUP) RightClicked?.Invoke();
            return 0;
        }
        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>Builds a HICON out of raw BGRA pixels.</summary>
    public static nint MakeIcon(int size, (byte R, byte G, byte B) colour)
    {
        byte[] pixels = IconArt.Render(size, colour);

        var header = new Native.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size,        // negative: top-down, like the byte array
            biPlanes = 1,
            biBitCount = 32,
            biCompression = Native.BI_RGB,
        };

        nint hdc = Native.GetDC(0);
        nint colourBitmap = Native.CreateDIBSection(hdc, ref header, Native.DIB_RGB_COLORS, out nint bits, 0, 0);
        Native.ReleaseDC(0, hdc);

        if (colourBitmap == 0 || bits == 0)
            throw new InvalidOperationException($"CreateDIBSection failed: {Marshal.GetLastWin32Error()}");

        Marshal.Copy(pixels, 0, bits, pixels.Length);

        // A 32bpp icon still wants a mask bitmap; all zeroes means "show
        // everything" and the alpha channel does the real work.
        var maskBits = new byte[size * size / 8];
        nint maskBitmap;
        unsafe
        {
            fixed (byte* p = maskBits)
                maskBitmap = Native.CreateBitmap(size, size, 1, 1, (nint)p);
        }

        var info = new Native.ICONINFO
        {
            fIcon = true,
            hbmColor = colourBitmap,
            hbmMask = maskBitmap,
        };

        nint icon = Native.CreateIconIndirect(ref info);
        int err = Marshal.GetLastWin32Error();

        Native.DeleteObject(colourBitmap);
        Native.DeleteObject(maskBitmap);

        if (icon == 0)
            throw new InvalidOperationException($"CreateIconIndirect failed: {err}");

        return icon;
    }

    /// <summary>
    /// Hands Windows the icon to show. Takes ownership of it: the icon it
    /// replaces is destroyed here.
    ///
    /// ⚠ THE OLD ICON IS RELEASED IN A finally, and that is the whole point.
    /// It used to be released on the last line, after the calls that can throw
    /// - so every failed update leaked one icon. Measured on 18.09.2026 in the
    /// worst possible way: 3305 failures in under three hours, and then a
    /// process with no handles left to open a window with. Whatever happens in
    /// between, the icon being replaced has to go.
    /// </summary>
    public void Show(nint icon, string tip)
    {
        nint old = _hIcon;
        _hIcon = icon;
        _tip = tip;

        try
        {
            var data = NewData();
            data.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP;
            data.uCallbackMessage = Native.WM_TRAYCALLBACK;
            data.hIcon = icon;
            data.szTip = tip.Length > 127 ? tip[..127] : tip;

            bool ok = Native.Shell_NotifyIconW(_added ? Native.NIM_MODIFY : Native.NIM_ADD, ref data);
            if (!ok)
                throw new InvalidOperationException(
                    $"Shell_NotifyIconW({(_added ? "MODIFY" : "ADD")}) failed: {Marshal.GetLastWin32Error()}");

            if (!_added)
            {
                _added = true;
                var version = NewData();
                version.uVersion = Native.NOTIFYICON_VERSION_4;
                if (!Native.Shell_NotifyIconW(Native.NIM_SETVERSION, ref version))
                    throw new InvalidOperationException($"NIM_SETVERSION failed: {Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            // Never the icon just handed over: putting the icon back after the
            // tray is rebuilt passes the one already held, and destroying it
            // would hand Windows a dead handle to draw.
            if (old != 0 && old != icon) Native.DestroyIcon(old);
        }
    }

    /// <summary>
    /// One line of the tray menu. A separator is a null label; an item with
    /// Children opens a submenu instead of returning an id.
    /// </summary>
    internal sealed record MenuItem(int Id, string? Label, bool Ticked = false,
        bool Enabled = true, IReadOnlyList<MenuItem>? Children = null);

    /// <summary>
    /// Shows the menu at the mouse and returns the chosen Id, or 0.
    ///
    /// The menu is BUILT EVERY TIME rather than kept: its labels are
    /// translated, and a menu built once would still be in the old language
    /// after a switch. The Python version hit exactly that and had to pass its
    /// labels as functions; building on demand is the same fix, done earlier.
    /// </summary>
    public int ShowMenu(IReadOnlyList<MenuItem> items)
    {
        nint menu = Build(items);
        if (menu == 0)
            return 0;

        try
        {
            if (!Native.GetCursorPos(out Native.POINT where))
                return 0;

            // Both of these are required and neither is optional folklore:
            // without the foreground call the menu will not close when the user
            // clicks elsewhere, and without the posted message the NEXT menu
            // sometimes refuses to appear at all.
            Native.SetForegroundWindow(_hwnd);
            int chosen = Native.TrackPopupMenuEx(menu,
                Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_NONOTIFY,
                where.X, where.Y, _hwnd, 0);
            Native.PostMessageW(_hwnd, Native.WM_NULL, 0, 0);

            return chosen;
        }
        finally
        {
            // Destroying the top menu takes its submenus with it - documented,
            // and the reason Build can hand them over without keeping a list.
            Native.DestroyMenu(menu);
        }
    }

    /// <summary>
    /// Builds a menu, submenus and all. Returns 0 if Windows would not give one.
    /// </summary>
    private static nint Build(IReadOnlyList<MenuItem> items)
    {
        nint menu = Native.CreatePopupMenu();
        if (menu == 0)
            return 0;

        foreach (var item in items)
        {
            if (item.Label is null)
            {
                Native.AppendMenuW(menu, Native.MF_SEPARATOR, 0, null);
                continue;
            }

            uint flags = Native.MF_STRING
                | (item.Ticked ? Native.MF_CHECKED : 0)
                | (item.Enabled ? 0 : Native.MF_GRAYED);

            if (item.Children is { Count: > 0 } children)
            {
                nint sub = Build(children);
                if (sub == 0)
                    continue;       // no submenu is better than an empty line
                // ⚠ A submenu is passed WHERE THE ID GOES. Windows then never
                // returns an id for this line - it opens the submenu instead -
                // so the item's own Id is meaningless and must not be relied on.
                Native.AppendMenuW(menu, flags | Native.MF_POPUP, (nuint)sub, item.Label);
                continue;
            }

            Native.AppendMenuW(menu, flags, (nuint)item.Id, item.Label);
        }

        return menu;
    }

    /// <summary>
    /// Shows a notification from the icon.
    ///
    /// Through Shell_NotifyIcon rather than the modern toast API: a toast from
    /// an UNPACKAGED app needs a registered identity and a COM activator, while
    /// this works with the icon that is already there. Windows shows it as an
    /// ordinary notification either way.
    /// </summary>
    public string? Notify(string title, string text)
    {
        if (!_added)
            return "the icon is not in the tray, so nothing could be shown";

        var data = NotificationData(title, text);
        return Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref data)
            ? null
            : $"the notification could not be shown ({Marshal.GetLastWin32Error()})";
    }

    /// <summary>
    /// What gets handed to Windows for a notification. Separate from sending it
    /// so a check can look at it.
    ///
    /// ⚠ That check is not optional politeness. Windows accepts this call and
    /// reports SUCCESS even when the flags say to show nothing at all -
    /// measured, with a sabotage run that passed while displaying nothing. It
    /// is the same trap as cbSize, which Windows also never verifies.
    /// </summary>
    internal Native.NOTIFYICONDATAW NotificationData(string title, string text)
    {
        var data = NewData();
        data.uFlags = Native.NIF_INFO;
        // Truncated on purpose and to the sizes the struct really has: handing
        // Windows a longer string than the field holds is how a struct quietly
        // turns into rubbish.
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = text.Length > 255 ? text[..255] : text;
        data.dwInfoFlags = Native.NIIF_INFO;
        return data;
    }

    internal Native.NOTIFYICONDATAW NewData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<Native.NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = IconId,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    public void Dispose()
    {
        if (_added)
        {
            var data = NewData();
            Native.Shell_NotifyIconW(Native.NIM_DELETE, ref data);
            _added = false;
        }
        if (_hIcon != 0) { Native.DestroyIcon(_hIcon); _hIcon = 0; }
        if (_hwnd != 0) Native.DestroyWindow(_hwnd);
    }
}
