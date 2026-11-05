import ctypes
import ctypes.wintypes as wt
import sys

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32

SM_CXSCREEN = 0
SM_CYSCREEN = 1
SRCCOPY = 0x00CC0020
DIB_RGB_COLORS = 0


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wt.DWORD),
        ("biWidth", ctypes.c_long),
        ("biHeight", ctypes.c_long),
        ("biPlanes", wt.WORD),
        ("biBitCount", wt.WORD),
        ("biCompression", wt.DWORD),
        ("biSizeImage", wt.DWORD),
        ("biXPelsPerMeter", ctypes.c_long),
        ("biYPelsPerMeter", ctypes.c_long),
        ("biClrUsed", wt.DWORD),
        ("biClrImportant", wt.DWORD),
    ]


def capture(path, max_width=1920):
    w = user32.GetSystemMetrics(SM_CXSCREEN)
    h = user32.GetSystemMetrics(SM_CYSCREEN)

    hdc = user32.GetDC(0)
    memdc = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(memdc, bmp)
    gdi32.BitBlt(memdc, 0, 0, w, h, hdc, 0, 0, SRCCOPY)

    bi = BITMAPINFOHEADER()
    bi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.biWidth = w
    bi.biHeight = -h          # top-down
    bi.biPlanes = 1
    bi.biBitCount = 32
    bi.biCompression = 0
    bi.biSizeImage = w * h * 4

    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(memdc, bmp, 0, h, buf, ctypes.byref(bi), DIB_RGB_COLORS)

    data = bytearray(buf.raw)
    # BGRA -> BGR rows padded to 4 bytes (already 4-byte aligned)
    file_header = bytearray()
    file_header += b"BM"
    file_header += (14 + 40 + len(data)).to_bytes(4, "little")
    file_header += (0).to_bytes(2, "little")
    file_header += (0).to_bytes(2, "little")
    file_header += (54).to_bytes(4, "little")

    info = bytearray(40)
    info[0:4] = (40).to_bytes(4, "little")
    info[4:8] = w.to_bytes(4, "little", signed=True)
    info[8:12] = (-h).to_bytes(4, "little", signed=True)
    info[12:14] = (1).to_bytes(2, "little")
    info[14:16] = (32).to_bytes(2, "little")
    info[16:20] = (0).to_bytes(4, "little")
    info[20:24] = len(data).to_bytes(4, "little")

    with open(path, "wb") as f:
        f.write(file_header)
        f.write(info)
        f.write(data)

    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(memdc)
    user32.ReleaseDC(0, hdc)
    return w, h


if __name__ == "__main__":
    out = sys.argv[1] if len(sys.argv) > 1 else "shot.bmp"
    print(capture(out))
