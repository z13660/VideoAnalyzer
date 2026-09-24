"""Checks the analysis-range dialog: open it, type a range, run it.

Usage: python range_dialog_check.py <video> [seconds-before-cancel]
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
VK_CONTROL, VK_A, VK_TAB = 0x11, 0x41, 0x09


def ctrl_a():
    w._send([w._key(VK_CONTROL), w._key(VK_A), w._key(VK_A, up=True), w._key(VK_CONTROL, up=True)])


def find_window(timeout=60.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        for win in w.list_windows(visible_only=False):
            if NEEDLE in (win.get("title") or ""):
                return win
        time.sleep(0.4)
    return None


def find_dialog(timeout=10.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        for win in w.list_windows(visible_only=False):
            title = win.get("title") or ""
            if "分析范围" in title:
                return win
        time.sleep(0.3)
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

    time.sleep(wait)
    w.click(r.left + 296, r.top + 76)          # 取消分析
    time.sleep(2.0)
    print("cancelled the full analysis")

    w.click(r.left + 296, r.top + 76)          # 分析视图范围 (same slot once the other hides)
    time.sleep(2.0)

    dlg = find_dialog()
    if not dlg:
        print("dialog did not appear")
        proc.kill()
        return
    dr = wt.RECT()
    user32.GetWindowRect(dlg["hwnd"], ctypes.byref(dr))
    print(f"dialog at {(dr.left, dr.top, dr.right, dr.bottom)}")
    ImageGrab.grab(all_screens=True).crop(
        (dr.left - 8, dr.top - 8, dr.right + 8, dr.bottom + 8)).save("_verify/range_dialog.png")

    # 20 s to 30 s
    ctrl_a()
    w.type_text("20")
    time.sleep(0.3)
    w.press(VK_TAB)
    time.sleep(0.3)
    ctrl_a()
    w.type_text("30")
    time.sleep(0.3)
    ImageGrab.grab(all_screens=True).crop(
        (dr.left - 8, dr.top - 8, dr.right + 8, dr.bottom + 8)).save("_verify/range_dialog_filled.png")

    w.press(0x0D)                               # Enter = 开始分析
    time.sleep(8.0)

    ImageGrab.grab(all_screens=True).crop(
        (r.left, r.top + 560, r.left + 1620, r.top + 1000)).save("_verify/range_dialog_result.png")
    ImageGrab.grab(all_screens=True).crop(
        (r.left, r.top + 960, r.left + 1200, r.top + 1000)).save("_verify/range_dialog_status.png")
    print("done - check the log for the range")

    try:
        proc.kill()
    except Exception:
        pass


if __name__ == "__main__":
    main()
