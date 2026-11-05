"""Launches the installer, screenshots it, opens the compatibility note, screenshots that too.

Everything happens inside one process run, because a WinExe started from a shell that
exits immediately gets torn down with the shell's process group.
"""

import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

from PIL import ImageGrab

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, "Crossfire Legion 修改器 v1.11.0.exe")

user32 = ctypes.windll.user32
CB = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)


def find_window(needle):
    hits = []

    def cb(h, _l):
        n = user32.GetWindowTextLengthW(h)
        if n and user32.IsWindowVisible(h):
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(h, buf, n + 1)
            if needle in buf.value:
                hits.append((h, buf.value))
        return True

    user32.EnumWindows(CB(cb), 0)
    return hits


def rect(h):
    r = wt.RECT()
    user32.GetWindowRect(h, ctypes.byref(r))
    return r.left, r.top, r.right, r.bottom


def click(h, cx, cy):
    pt = wt.POINT(cx, cy)
    user32.ClientToScreen(h, ctypes.byref(pt))
    user32.SetCursorPos(pt.x, pt.y)
    time.sleep(0.2)
    user32.SetForegroundWindow(h)
    time.sleep(0.2)
    user32.mouse_event(0x0002, 0, 0, 0, 0)
    time.sleep(0.06)
    user32.mouse_event(0x0004, 0, 0, 0, 0)
    return pt.x, pt.y


def click_child(h, text):
    """Clicks a child control by its caption - far more reliable than guessing client coords."""
    found = []

    def cb(ch, _l):
        n = user32.GetWindowTextLengthW(ch)
        if n:
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(ch, buf, n + 1)
            if text in buf.value:
                found.append(ch)
        return True

    user32.EnumChildWindows(h, CB(cb), 0)
    if not found:
        print("child control not found: %s" % text)
        return False
    r = wt.RECT()
    user32.GetWindowRect(found[0], ctypes.byref(r))
    pt = wt.POINT((r.left + r.right) // 2, (r.top + r.bottom) // 2)
    user32.SetCursorPos(pt.x, pt.y)
    time.sleep(0.2)
    user32.mouse_event(0x0002, 0, 0, 0, 0)
    time.sleep(0.06)
    user32.mouse_event(0x0004, 0, 0, 0, 0)
    print("clicked %r at %d,%d" % (text, pt.x, pt.y))
    return True


def shot(h, path, pad=8):
    l, t, r, b = rect(h)
    img = ImageGrab.grab((l - pad, t - pad, r + pad, b + pad))
    img.save(path)
    print("saved %s %s" % (path, img.size))


def main():
    subprocess.Popen([EXE], close_fds=True)
    for _ in range(30):
        time.sleep(0.4)
        hits = find_window("修改器  v")
        if hits:
            break
    else:
        print("window never appeared")
        return 1

    h, title = hits[0]
    print("window: %r" % title)
    l, t, r, b = rect(h)
    print("rect: %d,%d %dx%d" % (l, t, r - l, b - t))

    time.sleep(1.0)
    shot(h, os.path.join(ROOT, "docs", "installer.png"))

    # open the compatibility note by its caption, not by guessed coordinates
    user32.SetForegroundWindow(h)
    time.sleep(0.3)
    click_child(h, "适配说明")
    time.sleep(1.5)

    hits = find_window("适配说明")
    if not hits:
        print("doc dialog did not open")
        return 1
    dh = hits[0][0]
    shot(dh, os.path.join(ROOT, "docs", "compat-note.png"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
