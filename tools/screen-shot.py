"""真实屏幕截图（DPI-aware），纯标准库，不依赖 PIL。

为什么单独写：本机没装 Pillow，而验证「托盘图标 / 启动画面 / 界面品牌标」
必须截真实屏幕——headless 截不到托盘，DrawToBitmap 也截不到。
这里用 GDI BitBlt 抓 BGRA 原始像素，再用 zlib 手写 PNG 编码落盘。

关键点（都踩过坑）：
  1. 必须先 SetProcessDpiAwareness(2)。否则本进程是 DPI-unaware，
     GetSystemMetrics 返回逻辑尺寸，截物理屏幕只会得到左上角一块。
  2. 用 BitBlt 从屏幕 DC 拷，不要用 CopyFromScreen（.NET 专有），
     这样本脚本在纯 ctypes 下也能跑。
  3. PNG 编码用 filter type 0（None），行首加 0 字节即可，简单可靠。

用法：
  python screen-shot.py --out <前缀>            # 只截一张
  python screen-shot.py --out <前缀> --launch    # 启动暖序、等界面出来再截
"""

import argparse
import ctypes
import os
import struct
import subprocess
import sys
import time
import zlib
from ctypes import wintypes

user32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)

# HWND 是指针宽度，不声明 restype 会被截成 int32，64 位下句柄就废了
user32.FindWindowW.restype = wintypes.HWND
user32.FindWindowW.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR]

SRCCOPY = 0x00CC0020
DIB_RGB_COLORS = 0
BI_RGB = 0

DETACHED = 0x00000008
NEW_GROUP = 0x00000200
BREAKAWAY = 0x01000000

SWP_NOSIZE = 0x0001
SWP_NOZORDER = 0x0004
SWP_NOACTIVATE = 0x0010


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wintypes.DWORD),
        ("biWidth", wintypes.LONG),
        ("biHeight", wintypes.LONG),
        ("biPlanes", wintypes.WORD),
        ("biBitCount", wintypes.WORD),
        ("biCompression", wintypes.DWORD),
        ("biSizeImage", wintypes.DWORD),
        ("biXPelsPerMeter", wintypes.LONG),
        ("biYPelsPerMeter", wintypes.LONG),
        ("biClrUsed", wintypes.DWORD),
        ("biClrImportant", wintypes.DWORD),
    ]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wintypes.DWORD * 3)]


def make_dpi_aware():
    try:
        # 2 = PROCESS_PER_MONITOR_DPI_AWARE
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        try:
            user32.SetProcessDPIAware()
        except Exception:
            pass


def grab(rect=None):
    """抓屏，返回 (宽, 高, BGRA bytes)。rect 为 (x, y, w, h)，默认整屏。"""
    if rect is None:
        rect = (0, 0, user32.GetSystemMetrics(0), user32.GetSystemMetrics(1))
    x, y, w, h = rect

    screen_dc = user32.GetDC(0)
    mem_dc = gdi32.CreateCompatibleDC(screen_dc)
    bmp = gdi32.CreateCompatibleBitmap(screen_dc, w, h)
    gdi32.SelectObject(mem_dc, bmp)

    ok = gdi32.BitBlt(mem_dc, 0, 0, w, h, screen_dc, x, y, SRCCOPY)
    if not ok:
        raise OSError("BitBlt 失败: %d" % ctypes.get_last_error())

    bi = BITMAPINFO()
    bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.bmiHeader.biWidth = w
    # 负高度 = 自上而下，省得再翻转
    bi.bmiHeader.biHeight = -h
    bi.bmiHeader.biPlanes = 1
    bi.bmiHeader.biBitCount = 32
    bi.bmiHeader.biCompression = BI_RGB

    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mem_dc, bmp, 0, h, buf, ctypes.byref(bi), DIB_RGB_COLORS)

    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mem_dc)
    user32.ReleaseDC(0, screen_dc)
    return w, h, buf.raw


def write_png(path, w, h, bgra):
    """BGRA -> RGB PNG（filter 0），纯 zlib。"""
    rows = bytearray()
    stride = w * 4
    for yy in range(h):
        base = yy * stride
        line = bgra[base:base + stride]
        rgb = bytearray(w * 3)
        rgb[0::3] = line[2::4]   # B -> R
        rgb[1::3] = line[1::4]   # G
        rgb[2::3] = line[0::4]   # R -> B
        rows.append(0)           # filter type None
        rows.extend(rgb)

    def chunk(tag, data):
        out = struct.pack(">I", len(data)) + tag + data
        return out + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)  # 8bit truecolor
    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", ihdr)
           + chunk(b"IDAT", zlib.compress(bytes(rows), 6))
           + chunk(b"IEND", b""))
    with open(path, "wb") as f:
        f.write(png)
    return len(png)


def launch(exe):
    """分离式启动：DETACHED + 新进程组 + BREAKAWAY，避免被沙箱 Job 一起回收。
    BREAKAWAY 在「父进程所在 Job 不允许脱离」时会报 WinError 5，
    此时去掉该标志重试——进程仍在同一 Job 里，但本次调用内跑得完。"""
    flags = DETACHED | NEW_GROUP
    try:
        return subprocess.Popen([exe], cwd=os.path.dirname(exe),
                                creationflags=flags | BREAKAWAY, close_fds=True)
    except OSError:
        return subprocess.Popen([exe], cwd=os.path.dirname(exe),
                                creationflags=flags, close_fds=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True, help="输出路径前缀")
    ap.add_argument("--launch", action="store_true", help="先启动暖序再截")
    ap.add_argument("--exe", default=os.path.join(
        os.environ["LOCALAPPDATA"], "Programs", "暖序", "暖序.exe"))
    ap.add_argument("--at", default="4.0",
                    help="启动后第几秒截，可逗号分隔多个时刻，如 0.7,5.0")
    ap.add_argument("--exit-event", default="Local\\WarmSequence.Exit",
                    help="截完后发这个事件让程序优雅退出，传空字符串则不退出")
    ap.add_argument("--tray-w", type=int, default=1400, help="托盘裁剪宽度（物理像素）")
    ap.add_argument("--tray-h", type=int, default=64, help="托盘裁剪高度（物理像素）")
    ap.add_argument("--crop-window", action="store_true", help="额外按窗口矩形截一张")
    args = ap.parse_args()

    make_dpi_aware()
    sw, sh = user32.GetSystemMetrics(0), user32.GetSystemMetrics(1)
    print("屏幕物理尺寸 %dx%d" % (sw, sh))

    proc = None
    if args.launch:
        proc = launch(args.exe)
        print("已启动 pid=%d" % proc.pid)

    times = [float(t) for t in args.at.split(",") if t.strip()]
    start = time.time()
    for t in times:
        remain = start + t - time.time()
        if remain > 0:
            time.sleep(remain)
        w, h, bgra = grab()
        path = "%s-t%s.png" % (args.out, ("%.1f" % t).replace(".", "_"))
        size = write_png(path, w, h, bgra)
        print("t=%.1fs -> %s (%d bytes)" % (t, path, size))

    # 托盘区：右下角 tw x th 物理像素（Windows 11 会把新图标折叠进溢出区，
    # 截窄了看不到，所以默认给到 1400 宽）
    tw, th = min(args.tray_w, sw), min(args.tray_h, sh)
    _, _, tray = grab((sw - tw, sh - th, tw, th))
    tray_path = args.out + "-tray.png"
    write_png(tray_path, tw, th, tray)
    print("托盘 -> %s" % tray_path)

    if args.crop_window:
        hwnd = user32.FindWindowW(None, "暖序")
        if hwnd:
            # 截之前先把它挪到屏幕左上角：有些机器上它会被别的置顶窗口压着，
            # SetForegroundWindow 和 HWND_TOPMOST 都压不过去（对方自己也是置顶的）。
            # 挪位置不改变尺寸，截完原样挪回去。
            orig = wintypes.RECT()
            user32.GetWindowRect(hwnd, ctypes.byref(orig))
            user32.SetWindowPos(hwnd, 0, 8, 8, 0, 0,
                                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE)
            time.sleep(0.6)
            cr = wintypes.RECT()
            user32.GetClientRect(hwnd, ctypes.byref(cr))
            pt = wintypes.POINT(0, 0)
            user32.ClientToScreen(hwnd, ctypes.byref(pt))
            ww, wh = cr.right - cr.left, cr.bottom - cr.top
            _, _, win = grab((pt.x, pt.y, ww, wh))
            path = args.out + "-client.png"
            write_png(path, ww, wh, win)
            print("客户区 %dx%d -> %s" % (ww, wh, path))
            user32.SetWindowPos(hwnd, 0, orig.left, orig.top, 0, 0,
                                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE)
        else:
            print("没找到标题为「暖序」的窗口")

    if proc is not None:
        print("程序仍在运行：", proc.poll() is None)

    if args.exit_event:
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.OpenEventW.restype = wintypes.HANDLE
        h = kernel32.OpenEventW(0x0002, False, args.exit_event)  # EVENT_MODIFY_STATE
        if h:
            kernel32.SetEvent(h)
            kernel32.CloseHandle(h)
            print("已发出退出事件")
            for _ in range(60):
                if proc is None or proc.poll() is not None:
                    break
                time.sleep(0.25)
            print("退出码：", proc.poll() if proc else "n/a")
        else:
            print("没找到退出事件 %s（程序可能不支持）" % args.exit_event)


if __name__ == "__main__":
    sys.exit(main())
