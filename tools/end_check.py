"""End-of-playback check.

Plays a clip right through to the end and then:
  * reports the transport button state (it must be back to "play")
  * presses play again and checks the log for a seek back to 0

Usage: python end_check.py <video> <wait>
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
VK_SPACE, VK_HOME = 0x20, 0x24


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
    play_for = float(sys.argv[3]) if len(sys.argv) > 3 else 26.0
    prefix = sys.argv[4] if len(sys.argv) > 4 else "_verify/end"

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

    play_btn = (r.left + 533, r.top + 76)
    status = (r.left, r.top + 965, r.left + 900, r.top + 1000)

    def shot(name):
        img = ImageGrab.grab(all_screens=True)
        img.crop((play_btn[0] - 40, play_btn[1] - 25, play_btn[0] + 40, play_btn[1] + 25)).resize((320, 200)).save(f"{prefix}_{name}_btn.png")
        img.crop(status).save(f"{prefix}_{name}_status.png")
        print("  shot", name)

    w.press(VK_HOME)
    time.sleep(1.0)
    print(f"playing for {play_for:.0f}s ...")
    w.press(VK_SPACE)
    time.sleep(play_for)
    shot("after")

    print("pressing play again")
    w.press(VK_SPACE)
    time.sleep(3.0)
    shot("replay")

    proc.kill()
    print("done - inspect videoanalyzer.log")


if __name__ == "__main__":
    main()
