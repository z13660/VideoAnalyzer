"""Video full-screen check: double-click the picture to fill the screen, again (or Escape) to
come back.

Usage: python fullscreen_check.py <video> <prefix> [wait]
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
VK_ESCAPE = 0x1B


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

    # the middle of the picture
    px = r.left + int((r.right - r.left) * 0.25)
    py = r.top + int((r.bottom - r.top) * 0.30)

    def shot(name):
        img = ImageGrab.grab(all_screens=True)
        img.save(f"{prefix}_{name}.png")
        rect = wt.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(rect))
        print(f"  {name}: window {(rect.left, rect.top, rect.right, rect.bottom)} screen {img.size}")

    def double_click():
        w.click(px, py)
        time.sleep(0.08)
        w.click(px, py)

    shot("0_normal")
    double_click()
    time.sleep(1.6)
    shot("1_fullscreen")
    w.press(VK_ESCAPE)
    time.sleep(1.6)
    shot("2_escape_back")
    double_click()
    time.sleep(1.6)
    shot("3_fullscreen_again")
    double_click()
    time.sleep(1.6)
    shot("4_back")

    proc.kill()
    print("done")


if __name__ == "__main__":
    main()
