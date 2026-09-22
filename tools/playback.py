"""Playback check: start the analyser, hit play, and sample the video region over time.

Confirms that (a) the picture advances during playback and (b) the motion-vector
overlay is redrawn per frame rather than frozen on the frame that was on screen when
play started. Saves a contact sheet so the result can be eyeballed as well.

Usage: python playback.py <video> <out_prefix> [wait] [shots] [interval]
"""
import ctypes
import ctypes.wintypes as wt
import subprocess
import sys
import time

from PIL import Image, ImageChops, ImageGrab

sys.path.insert(0, r"C:\Users\Administrator\.workbuddy-ai\skills\win-gui-automation\scripts")
import winutil as w  # noqa: E402

APP = r"Z:\TEMP3\VideoAnalyzer\src\VideoAnalyzer\bin\Debug\net8.0-windows\VideoAnalyzer.exe"
NEEDLE = "frame level stream analyser"
user32 = ctypes.windll.user32
VK_SPACE = 0x20
VK_HOME = 0x24


def find_window(timeout=40.0):
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
    shots = int(sys.argv[4]) if len(sys.argv) > 4 else 5
    interval = float(sys.argv[5]) if len(sys.argv) > 5 else 0.6

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

    # put the window on the primary monitor and crop its video pane
    r = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    pw, ph = r.right - r.left, r.bottom - r.top
    user32.MoveWindow(hwnd, max(0, user32.GetSystemMetrics(0) - pw - 20), 20, pw, ph, True)
    time.sleep(1.0)

    scale = ImageGrab.grab(all_screens=True).size[0] / user32.GetSystemMetrics(78)
    x0 = -user32.GetSystemMetrics(76) * scale
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    print("scale", scale, "x0", x0, "rect", (r.left, r.top, r.right, r.bottom))

    # video pane: left column, between the toolbar and the graph stack
    vl = int(r.left * scale + x0)
    vt = int((r.top + 60) * scale)
    vr = int((r.left + (r.right - r.left) * 0.52) * scale + x0)
    vb = int((r.top + 400) * scale)
    box = (max(0, vl), max(0, vt), max(0, vr), max(0, vb))
    print("video box", box)

    # focus the app and start playback from the beginning
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.4)
    w.press(VK_HOME)
    time.sleep(0.6)
    w.press(VK_SPACE)
    print("playback started")

    frames = []
    for i in range(shots):
        time.sleep(interval)
        img = ImageGrab.grab(all_screens=True).crop(box)
        frames.append(img)
        img.save(f"{prefix}_{i}.png")

    proc.kill()

    # frame-to-frame difference tells us whether the pane is actually animating
    print("\npane change between consecutive samples:")
    for i in range(1, len(frames)):
        diff = ImageChops.difference(frames[i - 1].convert("L"), frames[i].convert("L"))
        changed = sum(1 for p in diff.getdata() if p > 8)
        total = diff.size[0] * diff.size[1]
        print(f"  {i-1}->{i}: {changed * 100.0 / total:5.1f}% of pixels changed")

    sheet = Image.new("RGB", (frames[0].width * len(frames), frames[0].height))
    for i, f in enumerate(frames):
        sheet.paste(f, (i * f.width, 0))
    sheet.save(f"{prefix}_sheet.png")
    print("contact sheet ->", f"{prefix}_sheet.png")


if __name__ == "__main__":
    main()