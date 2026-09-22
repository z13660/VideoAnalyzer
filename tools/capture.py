"""Launch VideoAnalyzer, wait for analysis, force it visible, and grab a tight crop.

The tricky part on this machine is the multi-monitor + DPI setup:
  * Pillow's ImageGrab returns physical pixels across the whole virtual screen.
  * GetWindowRect (from a DPI-unaware process) returns virtualised logical pixels.
So the window is first moved with a DPI-aware message-based call and then the crop
is computed from the physical grab size, not from the logical rect.

Usage: python capture.py <video> <out.png> [wait_seconds]
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


def physical_geometry():
    """(scale, primary_x0): physical = logical * scale + primary_x0."""
    user32.SetProcessDPIAware()
    logical_w = user32.GetSystemMetrics(78)          # SM_CXVIRTUALSCREEN
    logical_x = user32.GetSystemMetrics(76)          # SM_XVIRTUALSCREEN
    img = ImageGrab.grab(all_screens=True)
    scale = img.size[0] / logical_w if logical_w else 1.0
    return scale, -logical_x * scale, img


def find_window(timeout=40.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        for win in w.list_windows(visible_only=False):
            if NEEDLE in (win.get("title") or ""):
                return win
        time.sleep(0.5)
    return None


def main():
    video, out = sys.argv[1], sys.argv[2]
    wait = float(sys.argv[3]) if len(sys.argv) > 3 else 75.0

    proc = subprocess.Popen([APP, video])
    win = find_window()
    if win is None:
        print("window not found")
        proc.kill()
        return
    hwnd = win["hwnd"]
    print("pid", proc.pid, "hwnd", hwnd)

    w.force_foreground(hwnd)
    print(f"analysing for {wait:.0f}s ...")
    time.sleep(wait)

    w.force_foreground(hwnd)
    time.sleep(1.0)

    # optional: step forward with the Right arrow so a P/B frame is on screen
    steps = int(sys.argv[4]) if len(sys.argv) > 4 else 0
    if steps:
        user32.SetForegroundWindow(hwnd)
        time.sleep(0.4)
        for _ in range(steps):
            w.press(0x27)          # VK_RIGHT
            time.sleep(0.05)
        time.sleep(1.5)
        print(f"stepped {steps} frames")

    # optional: click the timeline at a given fraction to land mid-clip
    seek = float(sys.argv[5]) if len(sys.argv) > 5 else -1.0
    if seek >= 0:
        rr = wt.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(rr))
        # the timeline strip sits just above the status bar
        ty = int(rr.top + (rr.bottom - rr.top) * 0.62)
        tx = int(rr.left + (rr.right - rr.left) * seek)
        user32.SetForegroundWindow(hwnd)
        time.sleep(0.4)
        w.click(tx, ty)
        time.sleep(2.0)
        print(f"seeked to {seek:.0%} at ({tx},{ty})")

    # move it fully onto the primary monitor; done DPI-aware so the numbers stick
    r = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    phys_w, phys_h = r.right - r.left, r.bottom - r.top
    primary_w = user32.GetSystemMetrics(0)
    x = max(0, primary_w - phys_w - 20)
    user32.MoveWindow(hwnd, x, 20, phys_w, phys_h, True)
    time.sleep(1.2)

    # optional: zoom the shared time view by wheeling over the graph stack
    zoom = int(sys.argv[6]) if len(sys.argv) > 6 else 0
    if zoom:
        rr = wt.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(rr))
        gx = int(rr.left + (rr.right - rr.left) * 0.45)
        gy = int(rr.top + (rr.bottom - rr.top) * 0.62)   # inside the graph stack
        w.click(gx, gy)                 # focus the app
        time.sleep(0.4)
        for _ in range(abs(zoom)):
            w.scroll(gx, gy, 1 if zoom > 0 else -1)
            time.sleep(0.12)
        time.sleep(1.2)
        print(f"zoomed {zoom} notches at ({gx},{gy})")

    scale, x0, img = physical_geometry()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    print("logical rect", (r.left, r.top, r.right, r.bottom))

    phys = ImageGrab.grab(all_screens=True)
    phys.save(out)

    left = int(r.left * scale + x0)
    top = int(r.top * scale)
    right = int(r.right * scale + x0)
    bottom = int(r.bottom * scale)
    left, top = max(0, left), max(0, top)
    right, bottom = min(phys.size[0], right), min(phys.size[1], bottom)
    print("crop box", (left, top, right, bottom))

    crop_path = out.replace(".png", "_win.png")
    phys.crop((left, top, right, bottom)).save(crop_path)
    print("->", crop_path)

    proc.kill()


if __name__ == "__main__":
    main()