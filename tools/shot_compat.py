import ctypes, subprocess, sys, time, os
from ctypes import wintypes as wt
from PIL import ImageGrab

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, "Crossfire Legion 修改器 v1.11.0.exe")

user32 = ctypes.windll.user32
user32.GetWindowTextLengthW.restype = ctypes.c_int
user32.GetWindowTextW.argtypes = [wt.HWND, ctypes.c_wchar_p, ctypes.c_int]


def title(h):
    n = user32.GetWindowTextLengthW(h)
    if not n:
        return ""
    buf = ctypes.create_unicode_buffer(n + 1)
    user32.GetWindowTextW(h, buf, n + 1)
    return buf.value


def find_window(sub):
    hits = []
    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(h, _l):
        if user32.IsWindowVisible(h) and sub in title(h):
            hits.append(h)
        return True
    user32.EnumWindows(cb, 0)
    return hits


def rect(h):
    r = wt.RECT()
    user32.GetWindowRect(h, ctypes.byref(r))
    return r.left, r.top, r.right, r.bottom


def click_child(h, text):
    found = []
    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(ch, _l):
        t = title(ch)
        if t and text in t:
            found.append(ch)
        return True
    user32.EnumChildWindows(h, cb, 0)
    if not found:
        print("child not found:", text)
        return False
    r = wt.RECT()
    user32.GetWindowRect(found[0], ctypes.byref(r))
    cx, cy = (r.left + r.right) // 2, (r.top + r.bottom) // 2

    # SetForegroundWindow from a background process is ignored unless the input
    # queues are attached to the window that currently owns the foreground.
    fg = user32.GetForegroundWindow()
    tid_fg = user32.GetWindowThreadProcessId(fg, None)
    tid_me = ctypes.windll.kernel32.GetCurrentThreadId()
    user32.AttachThreadInput(tid_me, tid_fg, True)
    user32.BringWindowToTop(h)
    user32.SetForegroundWindow(h)
    user32.AttachThreadInput(tid_me, tid_fg, False)
    time.sleep(0.4)

    user32.SetCursorPos(cx, cy)
    time.sleep(0.2)
    user32.mouse_event(0x0002, 0, 0, 0, 0)
    time.sleep(0.08)
    user32.mouse_event(0x0004, 0, 0, 0, 0)
    print("clicked %r at %d,%d" % (text, cx, cy))
    return True


def shot(h, path, pad=8):
    l, t, r, b = rect(h)
    ImageGrab.grab((l - pad, t - pad, r + pad, b + pad)).save(path)
    print("saved", path)


def main():
    subprocess.Popen([EXE], close_fds=True)
    hits = []
    for _ in range(40):
        time.sleep(0.4)
        hits = find_window("修改器  v")
        if hits:
            break
    if not hits:
        print("installer window never appeared")
        return 1
    h = hits[0]
    print("window:", title(h))
    time.sleep(1.2)
    if not click_child(h, "适配说明"):
        return 1
    dlg = []
    for _ in range(25):
        time.sleep(0.4)
        dlg = find_window("适配说明")
        if dlg:
            break
    if not dlg:
        print("dialog did not open")
        return 1
    time.sleep(0.8)
    shot(dlg[0], os.path.join(ROOT, "docs", "compat-note.png"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
