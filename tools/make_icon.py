"""Generate the application icon.

Draws at 256 px and lets Pillow downsample into the multi-size .ico, so every size is a clean
resample of the same artwork rather than a hand-tuned small version.

Design: the analyser's own graph stack, reduced to its essentials — a teal bitrate histogram
with an accent playhead through it. Anything more detailed disappears below 32 px.

Usage: python make_icon.py [output.ico] [preview.png]
"""
import sys

from PIL import Image, ImageDraw

SIZE = 256
BG = (0x10, 0x15, 0x1C, 255)
BORDER = (0x2E, 0x3A, 0x4A, 255)
ACCENT = (0x5B, 0x9B, 0xFF, 255)
TEAL = (0x25, 0xC7, 0xC7, 255)
TEAL_DIM = (0x1B, 0x8A, 0x8A, 255)

# bar heights as a fraction of the plot height, left to right
BARS = [0.34, 0.62, 0.44, 0.86, 0.52, 0.72, 0.38, 0.58]


def build() -> Image.Image:
    img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    d.rounded_rectangle([4, 4, SIZE - 5, SIZE - 5], radius=52, fill=BG, outline=BORDER, width=4)

    # plot area
    left, right = 34, SIZE - 34
    top, bottom = 52, SIZE - 52
    plot_h = bottom - top

    # histogram bars
    n = len(BARS)
    gap = 12
    bar_w = (right - left - gap * (n - 1)) / n
    for i, frac in enumerate(BARS):
        x = left + i * (bar_w + gap)
        h = plot_h * frac
        colour = ACCENT if i == 3 else (TEAL if i % 2 == 0 else TEAL_DIM)
        d.rounded_rectangle([x, bottom - h, x + bar_w, bottom], radius=3, fill=colour)

    # baseline
    d.rectangle([left, bottom + 8, right, bottom + 12], fill=BORDER)

    # playhead through the middle of the bars
    px = left + (right - left) * 0.46
    d.rectangle([px - 4, top - 14, px + 4, bottom + 22], fill=(0xFF, 0xFF, 0xFF, 235))
    d.polygon([(px - 13, top - 30), (px + 13, top - 30), (px, top - 14)], fill=ACCENT)

    return img


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "app.ico"
    art = build()
    art.save(out, format="ICO",
             sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)])
    if len(sys.argv) > 2:
        art.resize((256, 256), Image.LANCZOS).save(sys.argv[2])
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
