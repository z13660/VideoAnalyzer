"""Transport checks around seeking.

  * playing from the last frame must start again from the top
  * a click that seeks while playing must keep playing
  * a drag that seeks while playing must resume on release

Usage: python playback_check.py <video> <wait>
"""
import ctypes
import ctypes.wintypes as wt
import subprocess
import sys
import time

sys.path.insert(0, r"C:\Users\Administrator\.workbuddy-ai\skills\win-gui-automation\scripts")
import winutil as w  # noqa: E402

APP = r"Z:\TEMP3\VideoAnalyzer\src\VideoAnalyzer\bin\Debug\net8.0-windows\VideoAnalyzer.exe"
NEEDLE = "frame level stream analyser"
user32 = ctypes.windll.user32
VK_SPACE, VK_END = 0x20, 0x23


def find_window(timeout=90.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        for win in w.list_windows(visible_only=False):
            if NEEDLE in (win.get("title") or ""):
                return win
        time.sleep(0.5)
    return None


def main():
    video = sys.argv[1]
    wait = float(sys.argv[2]) if len(sys.argv) > 2 else 40.0

    user32.SetProcessDPIAware()
    proc = subprocess.Popen([APP, video])
    win = find_window()
    if win is None:
        print("window not found")
        proc.kill()
        return
    hwnd = win["hwnd"]

    w.force_foreground(hwnd)
    time.sleep(wait)
    w.force_foreground(hwnd)
    time.sleep(1.0)

    r = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    pw, ph = r.right - r.left, r.bottom - r.top
    user32.MoveWindow(hwnd, 20, 20, pw, ph, True)
    time.sleep(1.2)
    user32.GetWindowRect(hwnd, ctypes.byref(r))

    row_y = r.top + 605
    mid_x = r.left + 779

    print("A. play, then click to seek while playing")
    w.press(VK_SPACE)
    time.sleep(3.0)
    w.click(mid_x - 200, row_y)
    time.sleep(2.5)
    print("   -> log: scrub commit should say resume=True and no stop before it")

    print("B. pause; the stop line proves the click kept it running")
    w.press(VK_SPACE)
    time.sleep(1.0)

    print("C. play from the end must restart at the top")
    w.press(VK_END)
    time.sleep(1.2)
    w.press(VK_SPACE)
    time.sleep(3.0)
    w.press(VK_SPACE)
    time.sleep(0.5)

    print("D. play, drag to seek, release must resume")
    w.press(VK_SPACE)
    time.sleep(2.5)
    user32.SetCursorPos(mid_x, row_y)
    time.sleep(0.2)
    inp = w.INPUT(); inp.type = w.INPUT_MOUSE
    inp.u.mi = w.MOUSEINPUT(mid_x, row_y, 0, w.MOUSEEVENTF_LEFTDOWN, 0, None)
    w._send([inp])
    time.sleep(0.4)
    for step in (60, 120, 180):
        user32.SetCursorPos(mid_x + step, row_y)
        time.sleep(0.5)
    inp2 = w.INPUT(); inp2.type = w.INPUT_MOUSE
    inp2.u.mi = w.MOUSEINPUT(mid_x + 180, row_y, 0, w.MOUSEEVENTF_LEFTUP, 0, None)
    w._send([inp2])
    time.sleep(3.0)
    w.press(VK_SPACE)
    time.sleep(0.5)

    proc.kill()
    print("done - inspect videoanalyzer.log")


if __name__ == "__main__":
    main()
