"""
Generates the app icon (dyn_lock.ico) + a preview (dyn_lock.png).

Everything is drawn at a large resolution and only then scaled down, so that
it stays sharp even at 16 px on the taskbar.

The result is written straight into windows/icons/, which is where the app
loads it from - a tool that saves next to itself would leave the real icons
untouched.

Run:  py tools/make_icons.py
"""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

HERE = Path(__file__).resolve().parent
OUT = HERE.parent / "windows" / "icons"
SIZES = [256, 128, 64, 48, 32, 24, 16]
Z = 1024                      # drawing resolution


def rounded_square(d, box, r, color):
    d.rounded_rectangle(box, radius=r, fill=color)


def background():
    """The backdrop with a diagonal gradient and a soft gloss on top."""
    img = Image.new("RGBA", (Z, Z), (0, 0, 0, 0))
    gradient = Image.new("RGBA", (Z, Z))
    p = gradient.load()
    start, end = (74, 130, 214), (18, 38, 76)     # #4a82d6 -> #12264c
    for y in range(Z):
        for x in range(Z):
            k = (x / Z * 0.35 + y / Z * 0.65)    # diagonal gradient
            p[x, y] = (int(start[0] + (end[0] - start[0]) * k),
                       int(start[1] + (end[1] - start[1]) * k),
                       int(start[2] + (end[2] - start[2]) * k), 255)
    mask = Image.new("L", (Z, Z), 0)
    rounded_square(ImageDraw.Draw(mask), (0, 0, Z - 1, Z - 1),
                   int(Z * 0.23), 255)
    img.paste(gradient, (0, 0), mask)

    # a soft gloss over the top third
    gloss = Image.new("RGBA", (Z, Z), (0, 0, 0, 0))
    dl = ImageDraw.Draw(gloss)
    dl.ellipse((-int(Z * 0.35), -int(Z * 0.75), int(Z * 1.35), int(Z * 0.42)),
               fill=(255, 255, 255, 34))
    img.alpha_composite(Image.composite(
        gloss, Image.new("RGBA", (Z, Z), (0, 0, 0, 0)), mask))
    return img


def padlock(img):
    """A white padlock in the middle, with a soft shadow under the body."""
    width, height = int(Z * 0.44), int(Z * 0.35)
    x0 = (Z - width) // 2
    y0 = int(Z * 0.505)
    thickness = int(Z * 0.066)
    sx, sy = int(Z * 0.332), int(Z * 0.285)

    shadow = Image.new("RGBA", (Z, Z), (0, 0, 0, 0))
    ds = ImageDraw.Draw(shadow)
    rounded_square(ds, (x0, y0 + int(Z * 0.022), x0 + width,
                        y0 + height + int(Z * 0.022)), int(Z * 0.06),
                   (8, 20, 42, 110))
    img.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(Z * 0.022)))

    d = ImageDraw.Draw(img)
    # the shackle - drawn before the body, so the body covers its ends
    d.arc((sx, sy, Z - sx, sy + int(Z * 0.44)), 180, 360,
          fill=(233, 240, 250, 255), width=thickness)
    rounded_square(d, (x0, y0, x0 + width, y0 + height), int(Z * 0.06),
                   (255, 255, 255, 255))

    # the keyhole
    r = int(Z * 0.037)
    cx, cy = Z // 2, y0 + int(height * 0.38)
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=(26, 56, 96, 255))
    d.polygon([(cx - int(r * 0.52), cy), (cx + int(r * 0.52), cy),
               (cx + int(r * 0.30), cy + int(r * 2.2)),
               (cx - int(r * 0.30), cy + int(r * 2.2))], fill=(26, 56, 96, 255))


def waves(d):
    """Arcs in the top right corner - the signal the app listens to."""
    cx, cy = int(Z * 0.775), int(Z * 0.245)
    for i, r in enumerate((int(Z * 0.075), int(Z * 0.125), int(Z * 0.175))):
        d.arc((cx - r, cy - r, cx + r, cy + r), 297, 63,
              fill=(134, 226, 168, 255 - i * 48), width=int(Z * 0.028))
    r = int(Z * 0.026)
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=(134, 226, 168, 255))


def green_circle():
    """Variant B - a green circle with a padlock, the same motif as the tray
    icon.

    The tray icon is drawn at 64 px (that is all it needs), so scaled up it
    comes out pixelated. Here the same motif is drawn large, so it stays sharp
    on the taskbar as well.
    """
    green = (34, 160, 80, 255)
    img = Image.new("RGBA", (Z, Z), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.ellipse((int(Z * .04),) * 2 + (int(Z * .96),) * 2, fill=green)

    width, height = int(Z * .40), int(Z * .32)
    x0, y0 = (Z - width) // 2, int(Z * .50)
    d.arc((int(Z * .345), int(Z * .29), Z - int(Z * .345), int(Z * .69)),
          180, 360, fill=(255, 255, 255, 255), width=int(Z * .062))
    rounded_square(d, (x0, y0, x0 + width, y0 + height), int(Z * .05),
                   (255, 255, 255, 255))

    r = int(Z * .034)
    cx, cy = Z // 2, y0 + int(height * .38)
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=green)
    d.polygon([(cx - int(r * .5), cy), (cx + int(r * .5), cy),
               (cx + int(r * .3), cy + int(r * 2.1)),
               (cx - int(r * .3), cy + int(r * 2.1))], fill=green)
    return img


def save(img, name):
    OUT.mkdir(parents=True, exist_ok=True)
    img.resize((256, 256), Image.LANCZOS).save(OUT / f"{name}.png")
    img.resize((256, 256), Image.LANCZOS).save(
        OUT / f"{name}.ico", format="ICO", sizes=[(v, v) for v in SIZES])
    print(f"  {name}.ico + {name}.png")


def main():
    blue = background()
    padlock(blue)
    waves(ImageDraw.Draw(blue))

    print(f"done ({', '.join(str(v) for v in SIZES)} px) -> {OUT}:")
    save(blue, "dyn_lock")               # variant A - blue with waves
    save(green_circle(), "dyn_lock_tray")  # variant B - green circle (IN USE)


if __name__ == "__main__":
    main()
