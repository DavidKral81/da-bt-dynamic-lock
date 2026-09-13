using System.IO.Compression;
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
    /// <summary>Writes a PNG of the window. Returns what went wrong, or null.</summary>
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
            WritePng(path, pixels, w, h);
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
    /// A PNG, written by hand. No image library is pulled in for this - the
    /// format is three chunks, and .NET already carries the compression
    /// (<see cref="ZLibStream"/>) and needs nothing but a CRC beside it.
    ///
    /// PNG rather than the BMP this used to write: a window of this size came
    /// out at 3 MB, and sixteen of those landed in a folder that is mirrored to
    /// cloud storage on every run. The same pictures compress to a few tens of
    /// kB, and freshly written 3 MB files were being held open long enough that
    /// a second run failed on "used by another process".
    /// </summary>
    private static void WritePng(string path, byte[] bgra, int w, int h)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Each row is preceded by its filter byte; 0 means "stored as is",
        // which leaves the compressor to do all the work and keeps this short.
        var raw = new byte[h * (1 + w * 3)];
        int to = 0;
        for (int y = 0; y < h; y++)
        {
            raw[to++] = 0;
            for (int x = 0; x < w; x++)
            {
                int from = (y * w + x) * 4;     // the bitmap is BGRA, PNG is RGB
                raw[to++] = bgra[from + 2];
                raw[to++] = bgra[from + 1];
                raw[to++] = bgra[from + 0];
            }
        }

        var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Fastest, true))
            deflate.Write(raw, 0, raw.Length);

        using var file = new BinaryWriter(File.Create(path));
        file.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });

        var head = new byte[13];
        WriteBig(head, 0, w);
        WriteBig(head, 4, h);
        head[8] = 8;        // bits per channel
        head[9] = 2;        // truecolour, no alpha
        Chunk(file, "IHDR", head);
        Chunk(file, "IDAT", compressed.ToArray());
        Chunk(file, "IEND", Array.Empty<byte>());
    }

    private static void Chunk(BinaryWriter file, string type, byte[] data)
    {
        var name = new[] { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
        var length = new byte[4];
        WriteBig(length, 0, data.Length);
        file.Write(length);
        file.Write(name);
        file.Write(data);

        // The check covers the type and the data, not the length.
        uint crc = Crc(Crc(0xFFFFFFFF, name), data) ^ 0xFFFFFFFF;
        var tail = new byte[4];
        WriteBig(tail, 0, (int)crc);
        file.Write(tail);
    }

    /// <summary>PNG counts in network byte order, .NET writes little-endian.</summary>
    private static void WriteBig(byte[] into, int at, int value)
    {
        into[at + 0] = (byte)(value >> 24);
        into[at + 1] = (byte)(value >> 16);
        into[at + 2] = (byte)(value >> 8);
        into[at + 3] = (byte)value;
    }

    private static uint Crc(uint running, byte[] data)
    {
        foreach (byte b in data)
            running = CrcTable[(running ^ b) & 0xFF] ^ (running >> 8);
        return running;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
