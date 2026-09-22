"""Drag-scrub check: press on the timeline, drag across it, screenshot mid-drag.

This verifies two things that are easy to get wrong:
  * the preview thumbnail appears while dragging (no decoder restart, so it is instant)
  * the seek only happens on release, so dragging does not queue up decoder restarts

Usage: python scrub.py <video> <out_prefix> [wait]
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
MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004


def mouse_down(x, y):
    """Press without releasing, so a drag can be sampled mid-gesture.

    Plain mouse_event() is silently ignored here; SendInput (what winutil uses) works.
    """
    user32.SetCursorPos(int(x), int(y))
    time.sleep(0.05)
    inp = w.INPUT()
    inp.type = w.INPUT_MOUSE
    inp.u.mi = w.MOUSEINPUT(int(x), int(y), 0, w.MOUSEEVENTF_LEFTDOWN, 0, None)
    w._send([inp])


def mouse_move(x, y):
    user32.SetCursorPos(int(x), int(y))


def mouse_up(x, y):
    inp = w.INPUT()
    inp.type = w.INPUT_MOUSE
    inp.u.mi = w.MOUSEINPUT(int(x), int(y), 0, w.MOUSEEVENTF_LEFTUP, 0, None)
    w._send([inp])


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
    wait = float(sys.argv[3]) if len(sys.argv) > 3 else 50.0

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
    time.sleep(0.8)

    r = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    pw, ph = r.right - r.left, r.bottom - r.top
    user32.MoveWindow(hwnd, max(0, user32.GetSystemMetrics(0) - pw - 20), 20, pw, ph, True)
    time.sleep(1.0)
    user32.GetWindowRect(hwnd, ctypes.byref(r))

    # video pane and timeline, in physical pixels (scale is 1.0 on this setup)
    video_box = (r.left, r.top + 60, r.left + int(pw * 0.52), r.top + 400)
    # The graph stack occupies roughly the lower half of the window; probe_hit.py
    # is the tool that established this, and it is what to re-run if it drifts.
    tl_y = r.top + int((r.bottom - r.top) * 0.62)
    tl_x0 = r.left + 40
    tl_x1 = r.right - 150
    print("timeline y", tl_y, "x", tl_x0, "->", tl_x1)

    # start a drag near 20 % and move right in steps, grabbing a shot mid-drag

    start_x = tl_x0 + int((tl_x1 - tl_x0) * 0.20)
    mouse_down(start_x, tl_y)
    time.sleep(0.4)

    shots = []
    for i in range(6):
        x = start_x + int((tl_x1 - tl_x0) * 0.10) * i
        mouse_move(x, tl_y)
        time.sleep(0.3)
        if i in (2, 5):
            img = ImageGrab.grab(all_screens=True).crop(video_box)
            shots.append(img)
            img.save(f"{prefix}_drag{i}.png")

    mouse_up(x, tl_y)
    time.sleep(2.5)                     # let the real seek land
    after = ImageGrab.grab(all_screens=True).crop(video_box)
    after.save(f"{prefix}_after.png")

    proc.kill()

    if len(shots) >= 2:
        diff = ImageChops.difference(shots[0].convert("L"), shots[1].convert("L"))
        changed = sum(1 for p in diff.get_flattened_data() if p > 8)
        total = diff.size[0] * diff.size[1]
        print(f"preview changed between drag steps: {changed * 100.0 / total:.1f}% of pixels")
    if shots:
        diff = ImageChops.difference(shots[-1].convert("L"), after.convert("L"))
        changed = sum(1 for p in diff.get_flattened_data() if p > 8)
        total = diff.size[0] * diff.size[1]
        print(f"preview vs settled frame: {changed * 100.0 / total:.1f}% of pixels")


if __name__ == "__main__":
    main()