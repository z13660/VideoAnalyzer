"""Audio-cluster check: the mute glyph must state which way the button will act, and the
sliders must not paint WPF's stock dotted focus rectangle on this background.

Usage: python audio_check.py <video> <prefix> [wait]
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
    wait = float(sys.argv[3]) if len(sys.argv) > 3 else 30.0

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

    # the audio cluster, in window coordinates
    box = (r.left + 890, r.top + 55, r.left + 1180, r.top + 110)
    mute = (r.left + 922, r.top + 76)
    slider = (r.left + 1000, r.top + 76)

    def shot(name):
        ImageGrab.grab(all_screens=True).crop(box).save(f"{prefix}_{name}.png")
        print(" ->", f"{prefix}_{name}.png")

    shot("0_loaded")

    w.click(*slider)          # focus the volume slider
    time.sleep(0.6)
    shot("1_slider_focus")

    w.click(*mute)
    time.sleep(0.8)
    shot("2_muted")

    w.click(*mute)
    time.sleep(0.8)
    shot("3_unmuted")

    proc.kill()


if __name__ == "__main__":
    main()
