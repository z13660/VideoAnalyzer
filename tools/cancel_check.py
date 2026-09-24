"""Checks the cancel-analysis button.

Clicks it while the deep passes are running, then reports:
  * how many of the pre-click ffmpeg decoders are still alive
  * what the log says was kept

Usage: python cancel_check.py <long-video> [seconds-before-click]
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

TH32CS_SNAPPROCESS = 0x00000002
PROCESS_TERMINATE = 0x0001
INVALID_HANDLE = ctypes.c_void_p(-1).value
kernel32 = ctypes.windll.kernel32


class PROCESSENTRY32(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD),
                ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)), ("th32ModuleID", wt.DWORD),
                ("cntThreads", wt.DWORD), ("th32ParentProcessID", wt.DWORD),
                ("pcPriClassBase", ctypes.c_long), ("dwFlags", wt.DWORD),
                ("szExeFile", ctypes.c_char * 260)]


def ffmpeg_pids():
    snap = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if snap == INVALID_HANDLE:
        return set()
    entry = PROCESSENTRY32()
    entry.dwSize = ctypes.sizeof(PROCESSENTRY32)
    pids = set()
    ok = kernel32.Process32First(snap, ctypes.byref(entry))
    while ok:
        if entry.szExeFile.decode("latin-1").lower() == "ffmpeg.exe":
            pids.add(entry.th32ProcessID)
        ok = kernel32.Process32Next(snap, ctypes.byref(entry))
    kernel32.CloseHandle(snap)
    return pids


def kill_all_ffmpeg():
    for pid in ffmpeg_pids():
        h = kernel32.OpenProcess(PROCESS_TERMINATE, False, pid)
        if h:
            kernel32.TerminateProcess(h, 1)
            kernel32.CloseHandle(h)


def find_window(timeout=60.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        for win in w.list_windows(visible_only=False):
            if NEEDLE in (win.get("title") or ""):
                return win
        time.sleep(0.4)
    return None


def main():
    video = sys.argv[1]
    before = float(sys.argv[2]) if len(sys.argv) > 2 else 8.0

    user32.SetProcessDPIAware()
    kill_all_ffmpeg()

    proc = subprocess.Popen([APP, video])
    win = find_window()
    if not win:
        print("window not found")
        proc.kill()
        return
    hwnd = win["hwnd"]

    w.force_foreground(hwnd)
    time.sleep(2.0)
    r = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    pw, ph = r.right - r.left, r.bottom - r.top
    user32.MoveWindow(hwnd, 20, 20, pw, ph, True)
    time.sleep(1.2)
    user32.GetWindowRect(hwnd, ctypes.byref(r))

    ImageGrab.grab(all_screens=True).crop(
        (r.left, r.top + 40, r.left + 700, r.top + 115)).save("_verify/cancel_toolbar.png")

    time.sleep(before)
    live_before = ffmpeg_pids()
    print(f"{before:.0f}s in: {len(live_before)} ffmpeg decoders")

    # 取消分析 sits right after 重新分析 in the toolbar
    w.click(r.left + 296, r.top + 76)
    time.sleep(3.0)

    live_after = ffmpeg_pids()
    survivors = live_before & live_after
    print(f"after the click: {len(live_after)} decoders, {len(survivors)} of the old ones left")
    print("   (only the playback decoder should remain: the analysis decoders must all be gone)")

    ImageGrab.grab(all_screens=True).crop(
        (r.left, r.top + 960, r.left + 1100, r.top + 1000)).save("_verify/cancel_status.png")

    try:
        proc.kill()
    except Exception:
        pass
    time.sleep(1.5)
    kill_all_ffmpeg()


if __name__ == "__main__":
    main()
