"""Checks the range-limited deep pass.

Cancels the full analysis, zooms the strip to a narrow range, asks for that range to be
analysed, and grabs the graph stack. The log records the range that was actually requested, so
the screenshot can be read against it.

Usage: python range_check.py <video> [seconds-before-cancel]
"""
import ctypes
import ctypes.wintypes as wt
import subprocess
import sys
import time

from PIL import ImageGrab

sys.path.insert(0, r"C:\Users\Administrator\.workbuddy-ai\skills\win-gui-automation\scripts")
import winutil as w  # noqa: E402

APP = r"Z:\TEMP3\VideoAnalyzer\src\VideoAnalyzer\bin\Debug\net8.0-windows\VideoAnalyzer.exe"
NEEDLE = "frame level stream analyser"
user32 = ctypes.windll.user32
VK_END, VK_ADD = 0x23, 0x6B


def find_window(timeout=60.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        for win in w.list_windows(visible_only=False):
            if NEEDLE in (win.get("title") or ""):
                return win
        time.sleep(0.4)
    return None


def main():
    video = sys.argv[1]
    wait = float(sys.argv[2]) if len(sys.argv) > 2 else 5.0

    user32.SetProcessDPIAware()
    proc = subprocess.Popen([APP, video])
    win = find_window()
    if not win:
        print("window not found")
        proc.kill()
        return
    hwnd = win["hwnd"]

    w.force_foreground(hwnd)
    time.sleep(1.5)
    r = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    pw, ph = r.right - r.left, r.bottom - r.top
    user32.MoveWindow(hwnd, 20, 20, pw, ph, True)
    time.sleep(1.2)
    user32.GetWindowRect(hwnd, ctypes.byref(r))

    ImageGrab.grab(all_screens=True).crop(
        (r.left, r.top + 40, r.left + 800, r.top + 115)).save("_verify/range_toolbar.png")

    # 1. either stop the full analysis early (range test) or let it finish (reference test)
    time.sleep(wait)
    if wait < 25:
        w.click(r.left + 296, r.top + 76)
        time.sleep(2.0)
        print("cancelled the full analysis")
    else:
        print("full analysis finished")
    ImageGrab.grab(all_screens=True).crop(
        (r.left, r.top + 40, r.left + 800, r.top + 115)).save("_verify/range_toolbar2.png")

    # 2. put the playhead at the end and zoom in, so the visible range is the last few seconds
    w.press(VK_END)
    time.sleep(1.0)
    for _ in range(4):
        w.press(VK_ADD)
        time.sleep(0.4)

    ImageGrab.grab(all_screens=True).crop(
        (r.left, r.top + 560, r.left + 1620, r.top + 1000)).save("_verify/range_before.png")

    # 3. analyse just that range (skipped in the reference run)
    if wait < 25:
        w.click(r.left + 296, r.top + 76)
        time.sleep(6.0)

    ImageGrab.grab(all_screens=True).crop(
        (r.left, r.top + 560, r.left + 1620, r.top + 1000)).save("_verify/range_after.png")
    ImageGrab.grab(all_screens=True).crop(
        (r.left, r.top + 960, r.left + 1200, r.top + 1000)).save("_verify/range_status.png")
    print("range pass requested - check the log for the range and compare with range_after.png")

    try:
        proc.kill()
    except Exception:
        pass


if __name__ == "__main__":
    main()
