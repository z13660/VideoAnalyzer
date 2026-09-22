"""CPU profile of the running app, phase by phase.

Samples the process's CPU time (kernel + user) over each phase so the cost of the picture,
the overlays and the analysis can be told apart.

Usage: python perf_check.py <video> [analysis_wait]
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
kernel32 = ctypes.windll.kernel32
VK_SPACE, VK_HOME = 0x20, 0x24
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000


def cpu_seconds(pid):
    h = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not h:
        return 0.0
    c, e, k, u = (wt.FILETIME() for _ in range(4))
    kernel32.GetProcessTimes(h, ctypes.byref(c), ctypes.byref(e), ctypes.byref(k), ctypes.byref(u))
    kernel32.CloseHandle(h)

    def ft(f):
        return (f.dwHighDateTime << 32) + f.dwLowDateTime

    return (ft(k) + ft(u)) / 1e7


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
    analysis_wait = float(sys.argv[2]) if len(sys.argv) > 2 else 40.0

    user32.SetProcessDPIAware()
    proc = subprocess.Popen([APP, video])
    pid = proc.pid

    win = find_window()
    if win is None:
        print("window not found")
        proc.kill()
        return
    hwnd = win["hwnd"]

    r = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    pw, ph = r.right - r.left, r.bottom - r.top
    user32.MoveWindow(hwnd, 20, 20, pw, ph, True)
    w.force_foreground(hwnd)

    phases = []

    def measure(name, seconds, start=None, end=None):
        if start:
            w.press(start)
            time.sleep(0.4)
        before = cpu_seconds(pid)
        time.sleep(seconds)
        after = cpu_seconds(pid)
        if end:
            w.press(end)
            time.sleep(0.4)
        used = after - before
        phases.append((name, used, seconds, used / seconds * 100.0))
        print(f"{name:34s} {used:6.2f} cpu-s over {seconds:.1f}s  = {used / seconds * 100:5.1f}% of one core")

    time.sleep(6)
    print(f"analysing for {analysis_wait:.0f}s ...")
    measure("analysis (QP + motion + playback)", analysis_wait)
    time.sleep(4)

    # overlay button: first of the three toggles on the right of the toolbar
    overlay = (r.left + 1503, r.top + 76)

    w.press(VK_HOME)
    time.sleep(0.6)
    measure("idle, paused", 6)

    measure("playing, motion vectors", 10, start=VK_HOME, end=VK_SPACE)
    time.sleep(0.6)

    # motion vectors -> block noise -> macroblock grid -> off
    for label in ("block noise", "macroblock grid", "overlay off"):
        w.click(*overlay)
        time.sleep(0.6)
        measure(f"playing, {label}", 8, start=VK_HOME, end=VK_SPACE)
        time.sleep(0.6)

    proc.kill()
    print()
    print("phase                                 cpu-s   wall   share of one core")
    for name, used, wall, pct in phases:
        print(f"{name:34s} {used:7.2f} {wall:6.1f}   {pct:5.1f}%")


if __name__ == "__main__":
    main()
