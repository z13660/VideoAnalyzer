"""Click-accuracy check for the pinned-playhead timeline.

With the playhead pinned, a press re-centres the view. If the release then re-reads the time
from the (moved) ruler, the committed time runs away from the pointer — a click on 2 s landed
on 4 s. This clicks a row of positions and compares the time the press previewed with the time
the release committed; they must be identical.

Usage: python click_check.py <video> <wait>
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
VK_HOME = 0x24


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
    print(f"analysing for {wait:.0f}s ...")
    time.sleep(wait)
    w.force_foreground(hwnd)
    time.sleep(1.0)

    r = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    pw, ph = r.right - r.left, r.bottom - r.top
    user32.MoveWindow(hwnd, 20, 20, pw, ph, True)
    time.sleep(1.2)
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    print("window at", (r.left, r.top))

    # The playhead sits at the middle of the strip when pinned, and the first row of the graph
    # stack is a big, stable target. Both share one time-to-x mapping.
    row_y = r.top + (int(sys.argv[3]) if len(sys.argv) > 3 else 605)
    mid_x = r.left + 779

    for off in (-360, -240, 120, 240, 360):
        w.press(VK_HOME)
        time.sleep(0.8)
        w.click(mid_x + off, row_y)
        time.sleep(1.4)
        print("clicked at offset", off)

    proc.kill()
    print("done - now compare 'scrub: preview' with 'scrub: commit' in videoanalyzer.log")


if __name__ == "__main__":
    main()
