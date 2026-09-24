"""Checks the on-disk analysis cache.

Run 1 analyses for a few seconds, cancels and closes — which must save an entry.
Run 2 opens the same file again and must restore it without starting a decoder.

Usage: python cache_check.py <video>
"""
import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

from PIL import ImageGrab

sys.path.insert(0, r"C:\Users\Administrator\.workbuddy-ai\skills\win-gui-automation\scripts")
import winutil as w  # noqa: E402

APP = r"Z:\TEMP3\VideoAnalyzer\src\VideoAnalyzer\bin\Debug\net8.0-windows\VideoAnalyzer.exe"
LOG = r"Z:\TEMP3\VideoAnalyzer\src\VideoAnalyzer\bin\Debug\net8.0-windows\videoanalyzer.log"
CACHE = os.path.join(os.environ.get("LOCALAPPDATA", r"C:\Users\Administrator\AppData\Local"),
                     "VideoAnalyzer", "cache")
NEEDLE = "frame level stream analyser"
user32 = ctypes.windll.user32


def find_window(timeout=60.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        for win in w.list_windows(visible_only=False):
            if NEEDLE in (win.get("title") or ""):
                return win
        time.sleep(0.4)
    return None


def open_app(video, wait, r=None):
    proc = subprocess.Popen([APP, video])
    win = find_window()
    if not win:
        print("window not found")
        proc.kill()
        return None, None
    hwnd = win["hwnd"]
    w.force_foreground(hwnd)
    time.sleep(1.5)

    rect = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    user32.MoveWindow(hwnd, 20, 20, rect.right - rect.left, rect.bottom - rect.top, True)
    time.sleep(1.2)
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    time.sleep(wait)
    return proc, rect


def main():
    video = sys.argv[1]
    user32.SetProcessDPIAware()

    print("in-memory cache: nothing on disk to clear")

    # ---- run 1: analyse a little, cancel, close --------------------------------
    proc, r = open_app(video, 22)
    if proc is None:
        return
    w.click(r.left + 296, r.top + 76)          # 取消分析
    time.sleep(2.5)
    proc.kill()
    time.sleep(1.5)

    saved = [l for l in open(LOG, encoding="utf-8", errors="replace") if "cache: held" in l]
    print("run 1:", saved[-1].strip() if saved else "NOTHING HELD")

    # ---- run 2: the same file, which should come back from the cache -----------
    mark = len(open(LOG, encoding="utf-8", errors="replace").readlines())
    proc, r = open_app(video, 8)
    if proc is None:
        return
    ImageGrab.grab(all_screens=True).crop(
        (r.left, r.top + 560, r.left + 1620, r.top + 1000)).save("_verify/cache_run2.png")
    ImageGrab.grab(all_screens=True).crop(
        (r.left, r.top + 960, r.left + 1400, r.top + 1000)).save("_verify/cache_status.png")
    proc.kill()

    tail = open(LOG, encoding="utf-8", errors="replace").readlines()[mark:]
    restored = [l.strip() for l in tail if "cache: restored" in l]
    decoders = [l.strip() for l in tail if "deep: qpNative" in l or "motion: decoding" in l]
    print("run 2:", restored[-1] if restored else "NO RESTORE LINE")
    print("run 2 deep decoders started:", len(decoders), "(0 means the cache was enough)")
    print("on-disk cache dir exists:", os.path.isdir(
        os.path.join(os.environ.get("LOCALAPPDATA", ""), "VideoAnalyzer")), "(should be False)")


if __name__ == "__main__":
    main()
