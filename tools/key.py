import ctypes
import sys
import time

user32 = ctypes.windll.user32

key = sys.argv[1]
delay = float(sys.argv[2]) if len(sys.argv) > 2 else 0.05

table = {
    "F1": 0x70, "F2": 0x71, "F3": 0x72, "F4": 0x73, "F5": 0x74,
    "F6": 0x75, "F7": 0x76, "F8": 0x77, "F9": 0x78, "F10": 0x79, "F11": 0x7A, "F12": 0x7B,
    "ESC": 0x1B, "SPACE": 0x20, "ENTER": 0x0D,
}

vk = table.get(key.upper())
if vk is None:
    raise SystemExit("unknown key " + key)

user32.keybd_event(vk, 0, 0, 0)
time.sleep(delay)
user32.keybd_event(vk, 0, 2, 0)
print("pressed", key)
