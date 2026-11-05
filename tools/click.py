import ctypes
import sys
import time

user32 = ctypes.windll.user32

user32.SetCursorPos(int(sys.argv[1]), int(sys.argv[2]))
time.sleep(0.25)
user32.mouse_event(0x0002, 0, 0, 0, 0)   # left down
time.sleep(0.06)
user32.mouse_event(0x0004, 0, 0, 0, 0)   # left up
print("clicked", sys.argv[1], sys.argv[2])
