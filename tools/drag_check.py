"""Drag-gesture check for the pinned-playhead timeline.

With the playhead pinned the strip is what moves, so a drag grabs the strip: dragging right
brings earlier frames back under the playhead and the time must go DOWN. A press on its own must
move nothing — the jump only happens on release, and only for a click.

Usage: python drag_check.py <video> <wait>
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
VK_END = 0x23
VK_HOME = 0x24


def mouse_down(x, y):
    user32.SetCursorPos(int(x), int(y))
    time.sleep(0.05)
    inp = w.INPUT()
    inp.type = w.INPUT_MOUSE
    inp.u.mi = w.MOUSEINPUT(int(x), int(y), 0, w.MOUSEEVENTF_LEFTDOWN, 0, None)
    w._send([inp])


def mouse_move(x, y):
    user32.SetCursorPos(int(x), int(y))


def mouse_up(x, y):
    inp = w.INPUT()
    inp.type = w.INPUT_MOUSE
    inp.u.mi = w.MOUSEINPUT(int(x), int(y), 0, w.MOUSEEVENTF_LEFTUP, 0, None)
    w._send([inp])


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
    prefix = sys.argv[3] if len(sys.argv) > 3 else None

    user32.SetProcessDPIAware()
    proc = subprocess.Popen([APP, video])
    win = find_window()
    if win is None:
        print("window not found")
        proc.kill()
        return
    hwnd = win["hwnd"]

    w.force_foreground(hwnd)
    print(f"analysing for {wait:.0f}s ...")
    time.sleep(wait)
    w.force_foreground(hwnd)
    time.sleep(1.0)

    r = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    pw, ph = r.right - r.left, r.bottom - r.top
    user32.MoveWindow(hwnd, 20, 20, pw, ph, True)
    time.sleep(1.2)
    user32.GetWindowRect(hwnd, ctypes.byref(r))

    row_y = r.top + 605
    mid_x = r.left + 779

    # start from the end of the clip so a drag to the right has room to travel
    w.press(VK_END)
    time.sleep(1.2)

    pane = (r.left, r.top + 60, r.left + int(pw * 0.52), r.top + 400)

    def grab(name):
        if prefix:
            from PIL import ImageGrab
            ImageGrab.grab(all_screens=True).crop(pane).save(f"{prefix}_{name}.png")

    grab("before")
    mouse_down(mid_x, row_y)
    time.sleep(0.5)
    for i, step in enumerate((60, 120, 180)):
        mouse_move(mid_x + step, row_y)
        time.sleep(0.6)
        grab(f"drag{i}")
    mouse_up(mid_x + 180, row_y)
    time.sleep(1.2)
    print("dragged right by 180 px")

    # and a press that never moves must seek to the point pressed
    w.press(VK_END)
    time.sleep(1.2)
    mouse_down(mid_x - 150, row_y)
    time.sleep(0.4)
    mouse_up(mid_x - 150, row_y)
    time.sleep(1.2)
    print("clicked 150 px left of centre")

    # with the playhead free the playhead itself follows the cursor, so a drag right goes forward
    w.click(r.left + 1548, r.top + 76)
    time.sleep(0.8)
    w.press(VK_HOME)
    time.sleep(1.0)
    mouse_down(mid_x, row_y)
    time.sleep(0.4)
    mouse_move(mid_x + 180, row_y)
    time.sleep(0.6)
    mouse_up(mid_x + 180, row_y)
    time.sleep(1.2)
    print("pin off: dragged right by 180 px")

    proc.kill()
    print("done - inspect videoanalyzer.log")


if __name__ == "__main__":
    main()
