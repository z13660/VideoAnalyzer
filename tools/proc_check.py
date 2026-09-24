"""Counts the ffmpeg children the app leaves behind.

  * after the window closes while the analysis is running
  * after switching files while the analysis is running

Usage: python proc_check.py <long-video> <second-video> [seconds-before-kill]
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
VK_CONTROL, VK_O = 0x11, 0x4F


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
    """Every live ffmpeg.exe, straight from the process snapshot (no external tools)."""
    snap = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if snap == INVALID_HANDLE:
        return []
    entry = PROCESSENTRY32()
    entry.dwSize = ctypes.sizeof(PROCESSENTRY32)
    pids = []
    ok = kernel32.Process32First(snap, ctypes.byref(entry))
    while ok:
        if entry.szExeFile.decode("latin-1").lower() == "ffmpeg.exe":
            pids.append(entry.th32ProcessID)
        ok = kernel32.Process32Next(snap, ctypes.byref(entry))
    kernel32.CloseHandle(snap)
    return pids


def ffmpeg_count():
    return len(ffmpeg_pids())


def kill_all_ffmpeg():
    for pid in ffmpeg_pids():
        h = kernel32.OpenProcess(PROCESS_TERMINATE, False, pid)
        if h:
            kernel32.TerminateProcess(h, 1)
            kernel32.CloseHandle(h)


def ctrl_o():
    w._send([w._key(VK_CONTROL), w._key(VK_O), w._key(VK_O, up=True), w._key(VK_CONTROL, up=True)])


def find_window(timeout=60.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        for win in w.list_windows(visible_only=False):
            if NEEDLE in (win.get("title") or ""):
                return win
        time.sleep(0.4)
    return None


def launch(video):
    proc = subprocess.Popen([APP, video])
    win = find_window()
    return proc, (win or {}).get("hwnd")


def main():
    long_video, second_video = sys.argv[1], sys.argv[2]
    before_kill = float(sys.argv[3]) if len(sys.argv) > 3 else 6.0

    user32.SetProcessDPIAware()
    kill_all_ffmpeg()
    print("baseline ffmpeg processes:", ffmpeg_count())

    # ---- A. close the window while the analysis is running --------------------
    proc, hwnd = launch(long_video)
    if not hwnd:
        print("window not found")
        proc.kill()
        return
    w.force_foreground(hwnd)
    time.sleep(before_kill)
    print(f"A. {before_kill:.0f}s into the analysis: {ffmpeg_count()} ffmpeg children")

    user32.PostMessageW(hwnd, 0x0010, 0, 0)          # WM_CLOSE
    time.sleep(4.0)
    left = ffmpeg_count()
    print(f"A. after the window closed: {left} ffmpeg children {'<- LEAK' if left else '(clean)'}")
    try:
        proc.kill()
    except Exception:
        pass

    # ---- B. switch files while the analysis is running ------------------------
    kill_all_ffmpeg()
    proc, hwnd = launch(long_video)
    if not hwnd:
        print("window not found")
        return
    w.force_foreground(hwnd)
    time.sleep(before_kill)
    before = set(ffmpeg_pids())
    print(f"B. {before_kill:.0f}s into the analysis: {len(before)} ffmpeg children")

    ctrl_o()
    time.sleep(2.5)
    w.type_text(second_video + "\n", newline="enter")
    time.sleep(7.0)
    after = set(ffmpeg_pids())
    survivors = before & after
    print(f"B. after switching files: {len(after)} ffmpeg children")
    print(f"   survivors from the old file: {len(survivors)} {sorted(survivors)} "
          f"{'<- LEAK' if survivors else '(clean)'}")

    try:
        proc.kill()
    except Exception:
        pass
    time.sleep(2.0)
    kill_all_ffmpeg()


if __name__ == "__main__":
    main()
