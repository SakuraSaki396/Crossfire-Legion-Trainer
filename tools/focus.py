"""Brings the game window to the foreground before synthetic input."""

import ctypes
import ctypes.wintypes as wt
import sys
import time

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32

PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
SW_RESTORE = 9


def window_pid(hwnd):
    pid = wt.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    return pid.value


def process_name(pid):
    h = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not h:
        return ""
    try:
        buf = ctypes.create_unicode_buffer(260)
        size = wt.DWORD(260)
        if kernel32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(size)):
            return buf.value.rsplit("\\", 1)[-1].lower()
    finally:
        kernel32.CloseHandle(h)
    return ""


target = (sys.argv[1] if len(sys.argv) > 1 else "crossfire_legion.exe").lower()

EnumProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
found = []


def cb(hwnd, _lparam):
    if not user32.IsWindowVisible(hwnd):
        return True
    if user32.GetWindow(hwnd, 4):        # GW_OWNER -> skip tool windows
        return True
    if process_name(window_pid(hwnd)) == target:
        r = wt.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(r))
        if r.right - r.left > 200 and r.bottom - r.top > 200:
            found.append(hwnd)
    return True


user32.EnumWindows(EnumProc(cb), 0)
if not found:
    raise SystemExit("window not found: " + target)

hwnd = found[0]
user32.ShowWindow(hwnd, SW_RESTORE)
time.sleep(0.2)
try:
    user32.SetForegroundWindow(hwnd)
except Exception:
    pass
time.sleep(0.4)
print("focused hwnd=%s title=%s" % (hwnd, user32.GetWindowTextW(hwnd)))
