using System;

namespace DaBtDynamicLock.App;

/// <summary>
/// Draws the tray icon at runtime: a filled circle in the state colour with a
/// white padlock on it. The colour changes with the state, so the icon is built
/// from pixels and swapped while the app runs - Pillow does the same job in the
/// Python version.
///
/// Drawn with 4x supersampling. The Tk checkmarks taught this project that a
/// control mark drawn too small or without smoothing looks cheap, and a tray
/// icon is 16-24 px on most screens.
/// </summary>
internal static class IconArt
{
    public static readonly (byte R, byte G, byte B) Ok = (34, 160, 80);
    public static readonly (byte R, byte G, byte B) Countdown = (230, 140, 20);
    public static readonly (byte R, byte G, byte B) Off = (120, 120, 120);

    private const int Super = 4;

    /// <summary>BGRA pixels, top-down, straight (non-premultiplied) alpha.</summary>
    public static byte[] Render(int size, (byte R, byte G, byte B) colour)
    {
        var pixels = new byte[size * size * 4];

        // Everything below is in 64x64 units, the same numbers the Python
        // version uses, scaled to the requested size.
        double u = size / 64.0;
        double cx = 32 * u, cy = 32 * u, radius = 26 * u;

        double bodyL = 24 * u, bodyR = 40 * u, bodyT = 30 * u, bodyB = 46 * u;
        double shackleCx = 32 * u, shackleCy = 28 * u;
        double shackleRx = 8 * u, shackleRy = 10 * u, shackleHalf = 2.5 * u;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int circleHits = 0, lockHits = 0;

                for (int sy = 0; sy < Super; sy++)
                {
                    for (int sx = 0; sx < Super; sx++)
                    {
                        double px = x + (sx + 0.5) / Super;
                        double py = y + (sy + 0.5) / Super;

                        double dx = px - cx, dy = py - cy;
                        bool inCircle = dx * dx + dy * dy <= radius * radius;
                        if (!inCircle) continue;
                        circleHits++;

                        bool inBody = px >= bodyL && px <= bodyR && py >= bodyT && py <= bodyB;

                        // Upper half of an elliptical ring - the shackle.
                        bool inShackle = false;
                        if (py <= shackleCy)
                        {
                            double ex = (px - shackleCx), ey = (py - shackleCy);
                            double outer = (ex * ex) / ((shackleRx + shackleHalf) * (shackleRx + shackleHalf))
                                         + (ey * ey) / ((shackleRy + shackleHalf) * (shackleRy + shackleHalf));
                            double inner = (ex * ex) / ((shackleRx - shackleHalf) * (shackleRx - shackleHalf))
                                         + (ey * ey) / ((shackleRy - shackleHalf) * (shackleRy - shackleHalf));
                            inShackle = outer <= 1.0 && inner >= 1.0;
                        }

                        if (inBody || inShackle) lockHits++;
                    }
                }

                int total = Super * Super;
                if (circleHits == 0) continue;

                double coverage = circleHits / (double)total;
                double lockShare = lockHits / (double)circleHits;

                byte r = (byte)Math.Round(colour.R + (255 - colour.R) * lockShare);
                byte g = (byte)Math.Round(colour.G + (255 - colour.G) * lockShare);
                byte b = (byte)Math.Round(colour.B + (255 - colour.B) * lockShare);

                int i = (y * size + x) * 4;
                pixels[i + 0] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = (byte)Math.Round(coverage * 255);
            }
        }

        return pixels;
    }
}
