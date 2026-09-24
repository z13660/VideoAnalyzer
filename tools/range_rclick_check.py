"""Checks the right-click range cycle on the timeline: start, end, clear.

Usage: python range_rclick_check.py <video> [seconds-before-cancel]
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
MOUSEEVENTF_RIGHTDOWN = 0x0008
MOUSEEVENTF_RIGHTUP = 0x0010


def right_click(x, y):
    user32.SetCursorPos(int(x), int(y))
    time.sleep(0.1)
    down = w.INPUT()
    down.type = w.INPUT_MOUSE
    down.u.mi = w.MOUSEINPUT(int(x), int(y), 0, MOUSEEVENTF_RIGHTDOWN, 0, None)
    up = w.INPUT()
    up.type = w.INPUT_MOUSE
    up.u.mi = w.MOUSEINPUT(int(x), int(y), 0, MOUSEEVENTF_RIGHTUP, 0, None)
    w._send([down, up])


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
    user32.MoveWindow(hwnd, 20, 20, r.right - r.left, r.bottom - r.top, True)
    time.sleep(1.2)
    user32.GetWindowRect(hwnd, ctypes.byref(r))

    # the strip sits under the graph stack; t=20 s and t=30 s on the fit view of a 60 s clip
    # measured: the ruler labels sit at window y 921-929, the strip runs to ~949;
    # t=0 is at x=778 and the fit view of a 60 s clip is 25.2 px/s
    strip_y = r.top + 930
    x20 = r.left + 1282
    x30 = r.left + 1483

    time.sleep(wait)
    w.click(r.left + 296, r.top + 76)          # cancel the full analysis first
    time.sleep(2.0)
    print("cancelled the full analysis")

    # 1st right-click: the start
    right_click(x20, strip_y)
    time.sleep(1.0)
    ImageGrab.grab(all_screens=True).crop(
        (r.left, r.top + 915, r.left + 1620, r.top + 1000)).save("_verify/rc_1_start.png")
    ImageGrab.grab(all_screens=True).crop(
        (r.left, r.top + 960, r.left + 1200, r.top + 1000)).save("_verify/rc_1_status.png")
    print("1st right-click: start marked")

    # 2nd right-click: the end, which runs the range
    right_click(x30, strip_y)
    time.sleep(8.0)
    ImageGrab.grab(all_screens=True).crop(
        (r.left, r.top + 560, r.left + 1620, r.top + 1000)).save("_verify/rc_2_range.png")
    print("2nd right-click: end marked, range analysed")

    # 3rd right-click: clear, back to the whole clip
    right_click(x20, strip_y)
    time.sleep(6.0)
    ImageGrab.grab(all_screens=True).crop(
        (r.left, r.top + 560, r.left + 1620, r.top + 1000)).save("_verify/rc_3_cleared.png")
    print("3rd right-click: range cleared, whole clip analysing")

    w.click(r.left + 296, r.top + 76)          # stop the full pass so the test ends quickly
    time.sleep(1.5)
    try:
        proc.kill()
    except Exception:
        pass


if __name__ == "__main__":
    main()
