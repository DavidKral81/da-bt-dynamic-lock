using System.Runtime.InteropServices;

namespace DaBtDynamicLock.App;

/// <summary>
/// Saves a picture of one of our windows.
///
/// This exists because measuring is not looking. Widths, paddings and control
/// counts all came out fine in this project's history while the actual screen
/// showed a cramped Czech label and grey-on-grey text - both found by eye, not
/// by a number. So the look gets checked from a picture, and the tool that
/// takes it is part of the project rather than something rebuilt each time.
///
/// PrintWindow with PW_RENDERFULLCONTENT, which was measured against a real
/// WinUI 3 window before any of this was written: it comes out with the
/// content, and it works even while the screen is locked, where an ordinary
/// screen grab catches the lock screen instead.
/// </summary>
internal static class Screenshot
{
    /// <summary>Writes a 24-bit BMP of the window. Returns what went wrong, or null.</summary>
    public static string? Save(nint window, string path)
    {
        // The size comes from GetWindowRect, not from the client area:
        // PrintWindow draws the frame too, and measuring the inside once cut
        // 31 px off the bottom of every picture and looked like a fault in the
        // app rather than in the tool.
        if (!Native.GetWindowRect(window, out Native.RECT rect))
            return $"GetWindowRect failed ({Marshal.GetLastWin32Error()})";

        int w = rect.Width, h = rect.Height;
        if (w <= 0 || h <= 0)
            return $"the window measures {w}x{h} - nothing to save";

        nint screenDc = Native.GetDC(0);
        nint memDc = Native.CreateCompatibleDC(screenDc);

        var header = new Native.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,          // negative: top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = Native.BI_RGB,
        };

        nint bitmap = Native.CreateDIBSection(memDc, ref header, Native.DIB_RGB_COLORS,
            out nint bits, 0, 0);
        try
        {
            if (bitmap == 0 || bits == 0)
                return $"CreateDIBSection failed ({Marshal.GetLastWin32Error()})";

            nint previous = Native.SelectObject(memDc, bitmap);
            bool drew = Native.PrintWindow(window, memDc, Native.PW_RENDERFULLCONTENT);
            Native.SelectObject(memDc, previous);
            if (!drew)
                return $"PrintWindow failed ({Marshal.GetLastWin32Error()})";

            var pixels = new byte[w * h * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            WriteBmp(path, pixels, w, h);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"the picture could not be written ({e.Message})";
        }
        finally
        {
            if (bitmap != 0) Native.DeleteObject(bitmap);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(0, screenDc);
        }
    }

    /// <summary>
    /// A plain 24-bit BMP. No image library is pulled in for this: one file
    /// header, one info header and the rows bottom-up is the whole format, and
    /// any picture viewer opens it.
    /// </summary>
    private static void WriteBmp(string path, byte[] bgra, int w, int h)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        int stride = (w * 3 + 3) & ~3;          // rows are padded to 4 bytes
        int size = 54 + stride * h;

        using var file = new BinaryWriter(File.Create(path));
        file.Write((byte)'B'); file.Write((byte)'M');
        file.Write(size);
        file.Write(0);
        file.Write(54);                          // where the pixels start
        file.Write(40);                          // info header size
        file.Write(w);
        file.Write(h);                           // positive: rows bottom-up
        file.Write((short)1);
        file.Write((short)24);
        file.Write(0); file.Write(stride * h);
        file.Write(2835); file.Write(2835);      // 72 dpi
        file.Write(0); file.Write(0);

        var row = new byte[stride];
        for (int y = h - 1; y >= 0; y--)
        {
            for (int x = 0; x < w; x++)
            {
                int from = (y * w + x) * 4;
                row[x * 3 + 0] = bgra[from + 0];
                row[x * 3 + 1] = bgra[from + 1];
                row[x * 3 + 2] = bgra[from + 2];
            }
            file.Write(row);
        }
    }
}
