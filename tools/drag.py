import ctypes
import sys
import time

user32 = ctypes.windll.user32

x1, y1, x2, y2 = (int(v) for v in sys.argv[1:5])
steps = int(sys.argv[5]) if len(sys.argv) > 5 else 12

user32.SetCursorPos(x1, y1)
time.sleep(0.25)
user32.mouse_event(0x0002, 0, 0, 0, 0)   # left down
time.sleep(0.12)

for i in range(1, steps + 1):
    user32.SetCursorPos(int(x1 + (x2 - x1) * i / steps), int(y1 + (y2 - y1) * i / steps))
    time.sleep(0.05)

time.sleep(0.2)
user32.mouse_event(0x0004, 0, 0, 0, 0)   # left up
print("dragged %d,%d -> %d,%d" % (x1, y1, x2, y2))
