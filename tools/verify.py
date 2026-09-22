"""Launch VideoAnalyzer, wait for the analysis, bring it to the front and grab a shot.

Everything happens inside one process so the app is not reaped when the caller's
shell exits.  Usage:  python verify.py <video> <out.png> [wait_seconds]
"""
import ctypes
import subprocess
import sys
import time

from PIL import ImageGrab

sys.path.insert(0, r"C:\Users\Administrator\.workbuddy-ai\skills\win-gui-automation\scripts")
import winutil as w  # noqa: E402

APP = r"Z:\TEMP3\VideoAnalyzer\src\VideoAnalyzer\bin\Debug\net8.0-windows\VideoAnalyzer.exe"
TITLE_NEEDLE = "frame level stream analyser"

user32 = ctypes.windll.user32
SWP_NOZORDER = 0x0004
SWP_SHOWWINDOW = 0x0040


def find_window(timeout=30.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        for win in w.list_windows(visible_only=False):
            if TITLE_NEEDLE in (win.get("title") or ""):
                return win
        time.sleep(0.5)
    return None


def place(hwnd, x, y, cx, cy):
    user32.SetWindowPos(hwnd, 0, x, y, cx, cy, SWP_NOZORDER | SWP_SHOWWINDOW)


def main():
    video = sys.argv[1]
    out = sys.argv[2]
    wait = float(sys.argv[3]) if len(sys.argv) > 3 else 70.0

    user32.SetProcessDPIAware()
    proc = subprocess.Popen([APP, video])
    print("launched pid", proc.pid)

    win = find_window()
    if win is None:
        print("window not found")
        proc.kill()
        return
    print("window:", win)

    # Bring it forward but leave the position alone: this Python process is
    # DPI-unaware, so SetWindowPos coordinates get virtualised and the window
    # would land somewhere unexpected.  The caller crops from the full grab.
    hwnd = win["hwnd"]
    w.force_foreground(hwnd)
    time.sleep(0.6)

    print(f"waiting {wait:.0f}s for the analysis to finish ...")
    time.sleep(wait)

    w.force_foreground(hwnd)
    time.sleep(0.8)

    img = ImageGrab.grab(all_screens=True)
    img.save(out)
    print("shot:", img.size, "->", out)

    crop_path = out.replace(".png", "_win.png")
    img.crop((1900, 0, 3840, 1080)).save(crop_path)
    print("crop ->", crop_path)

    proc.kill()


if __name__ == "__main__":
    main()
