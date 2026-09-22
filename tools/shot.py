"""Screenshot helper for the VideoAnalyzer verification run.

Usage: python shot.py <out.png> [window_substring]
Grabs the whole virtual screen (physical pixels) and, when a window
substring is given, also crops that window.
"""
import ctypes
import sys

from PIL import ImageGrab

sys.path.insert(0, r"C:\Users\Administrator\.workbuddy-ai\skills\win-gui-automation\scripts")
import winutil as w  # noqa: E402


def virtual_scale():
    """Return (scale, primary_x0) mapping physical pixels to DPI-unaware logical ones."""
    user32 = ctypes.windll.user32
    user32.SetProcessDPIAware()
    logical_w = user32.GetSystemMetrics(78)  # SM_CXVIRTUALSCREEN
    physical = ImageGrab.grab(all_screens=True)
    if logical_w <= 0:
        return 1.0, 0
    scale = physical.size[0] / logical_w
    primary_x0 = int(user32.GetSystemMetrics(76) * scale)  # SM_XVIRTUALSCREEN
    return scale, primary_x0


def main():
    out = sys.argv[1]
    needle = sys.argv[2] if len(sys.argv) > 2 else None

    img = ImageGrab.grab(all_screens=True)
    img.save(out)
    print(f"full screen {img.size} -> {out}")

    if not needle:
        return
    for win in w.list_windows():
        title = win.get("title") or ""
        if needle.lower() in title.lower():
            l, t, r, b = win["rect"]
            scale, primary_x0 = virtual_scale()
            box = (
                max(0, int((l * scale) + primary_x0)),
                max(0, int(t * scale)),
                min(img.size[0], int(r * scale + primary_x0)),
                min(img.size[1], int(b * scale)),
            )
            if box[2] - box[0] < 10 or box[3] - box[1] < 10:
                continue
            crop = img.crop(box)
            crop_path = out.replace(".png", "_win.png")
            crop.save(crop_path)
            print(f"window '{title}' {win['rect']} -> {crop_path} {crop.size}")
            return
    print(f"window containing '{needle}' not found")


if __name__ == "__main__":
    main()
