"""Pinned-playhead check.

Loads a clip, then samples the window at the points that matter:
  start   - first frame: the playhead must sit in the middle, data to its right
  end     - last frame: the playhead still in the middle, data to its left
  zoom    - a few zoom steps in: the strip must stay centred and scroll
  playN   - mid-playback: the data must travel while the playhead stays put

Usage: python timeline_check.py <video> <out_prefix> [wait]
"""
import ctypes
import ctypes.wintypes as wt
import subprocess
import sys
import time

from PIL import Image, ImageGrab

sys.path.insert(0, r"C:\Users\Administrator\.workbuddy-ai\skills\win-gui-automation\scripts")
import winutil as w  # noqa: E402

APP = r"Z:\TEMP3\VideoAnalyzer\src\VideoAnalyzer\bin\Debug\net8.0-windows\VideoAnalyzer.exe"
NEEDLE = "frame level stream analyser"
user32 = ctypes.windll.user32
VK_HOME, VK_END, VK_SPACE, VK_PLUS, VK_0 = 0x24, 0x23, 0x20, 0xBB, 0x30


def find_window(timeout=90.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        for win in w.list_windows(visible_only=False):
            if NEEDLE in (win.get("title") or ""):
                return win
        time.sleep(0.5)
    return None


def main():
    video, prefix = sys.argv[1], sys.argv[2]
    wait = float(sys.argv[3]) if len(sys.argv) > 3 else 40.0
    zooms = int(sys.argv[4]) if len(sys.argv) > 4 else 7

    user32.SetProcessDPIAware()
    proc = subprocess.Popen([APP, video])
    win = find_window()
    if win is None:
        print("window not found")
        proc.kill()
        return
    hwnd = win["hwnd"]

    w.force_foreground(hwnd)
    print(f"analysing for {wait:.0f}s ...")
    time.sleep(wait)

    w.force_foreground(hwnd)
    time.sleep(1.0)

    # park the window so the crop is stable
    r = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    pw, ph = r.right - r.left, r.bottom - r.top
    user32.MoveWindow(hwnd, 20, 20, pw, ph, True)
    time.sleep(1.2)

    user32.GetWindowRect(hwnd, ctypes.byref(r))
    full = ImageGrab.grab(all_screens=True)
    scale = full.size[0] / user32.GetSystemMetrics(78)
    x0 = int(user32.GetSystemMetrics(76) * scale)
    box = (
        max(0, int(r.left * scale + x0)),
        max(0, int(r.top * scale)),
        min(full.size[0], int(r.right * scale + x0)),
        min(full.size[1], int(r.bottom * scale)),
    )
    print("window rect", (r.left, r.top, r.right, r.bottom), "scale", scale, "x0", x0, "box", box)

    def shot(name):
        img = ImageGrab.grab(all_screens=True).crop(box)
        img.save(f"{prefix}_{name}.png")
        print(" ->", f"{prefix}_{name}.png", img.size)

    w.press(VK_HOME)
    time.sleep(1.0)
    shot("start")

    w.press(VK_END)
    time.sleep(1.5)
    shot("end")

    # zoom in a few steps, then look at the start of the clip again
    for _ in range(zooms):
        w.press(VK_PLUS)
        time.sleep(0.25)
    time.sleep(0.8)
    shot("zoomed")

    w.press(VK_HOME)
    time.sleep(1.0)
    shot("zoom_start")

    w.press(VK_SPACE)
    time.sleep(2.5)
    shot("play1")
    time.sleep(2.5)
    shot("play2")
    w.press(VK_SPACE)

    proc.kill()


if __name__ == "__main__":
    main()
