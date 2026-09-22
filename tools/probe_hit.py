"""Find the screen rows that actually hit the graph stack, by clicking and watching the log.

The window layout depends on the monitor size and DPI, so hard coding a y offset keeps
breaking. This clicks a column of candidates and reports which ones the app reacted to
(the app logs every seek to videoanalyzer.log).

Usage: python probe_hit.py <video> [wait]
"""
import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

sys.path.insert(0, r"C:\Users\Administrator\.workbuddy-ai\skills\win-gui-automation\scripts")
import winutil as w  # noqa: E402

APP = r"Z:\TEMP3\VideoAnalyzer\src\VideoAnalyzer\bin\Debug\net8.0-windows\VideoAnalyzer.exe"
LOG = r"Z:\TEMP3\VideoAnalyzer\src\VideoAnalyzer\bin\Debug\net8.0-windows\videoanalyzer.log"
NEEDLE = "frame level stream analyser"

user32 = ctypes.windll.user32


def find_window(timeout=40.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        for win in w.list_windows(visible_only=False):
            if NEEDLE in (win.get("title") or ""):
                return win
        time.sleep(0.5)
    return None


def log_tail():
    try:
        with open(LOG, encoding="utf-8", errors="replace") as fh:
            return fh.read().count("seek:")
    except FileNotFoundError:
        return 0


def main():
    video = sys.argv[1]
    wait = float(sys.argv[2]) if len(sys.argv) > 2 else 45.0

    user32.SetProcessDPIAware()
    if os.path.exists(LOG):
        os.remove(LOG)

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
    pw = r.right - r.left
    print("window rect", (r.left, r.top, r.right, r.bottom))

    x = r.left + int(pw * 0.45)
    hits = []
    for frac in (0.40, 0.45, 0.50, 0.55, 0.60, 0.65, 0.70, 0.75, 0.80, 0.85, 0.90, 0.95):
        y = r.top + int((r.bottom - r.top) * frac)
        before = log_tail()
        w.click(x, y)
        time.sleep(0.6)
        after = log_tail()
        mark = "HIT " if after > before else "    "
        print(f"  {mark} frac {frac:.2f} -> y {y}")
        if after > before:
            hits.append(y)

    proc.kill()
    if hits:
        print("clickable rows:", hits[0], "..", hits[-1])
    else:
        print("no row reacted")


if __name__ == "__main__":
    main()