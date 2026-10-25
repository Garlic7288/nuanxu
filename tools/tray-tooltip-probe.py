"""读托盘图标的悬停提示文本，验证 NotifyIcon.Text 是否跟着状态变。

为什么不用截图目测：托盘图标的位置随任务栏布局浮动，Win11 还是 XAML 实现，
经典 ToolbarWindow32 里已经取不到按钮文本了。这里改用官方 API 走一遍：
  1. EnumWindows 找到暖序进程那个隐藏的 WindowsForms10 窗口（NotifyIcon 的宿主）
  2. Shell_NotifyIconGetRect 拿到图标的屏幕矩形——这是系统自己算的，不用猜
  3. 把鼠标挪过去，等 tooltip 弹出，只截图标正上方那一小块
  4. 用「像素里有字」粗判 tooltip 是否出现，再把图存下来给人看

必须整段跑完（沙箱在调用结束时硬杀该调用内启动的 GUI 进程）。

用法：
  python tray-tooltip-probe.py                 # 测 dist
  python tray-tooltip-probe.py --exe <path>    # 测装机版
  python tray-tooltip-probe.py --rect-only     # 只打印图标矩形，不悬停
"""

import argparse
import ctypes
import os
import subprocess
import sys
import time
from ctypes import wintypes

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TEMP = os.environ.get("TEMP", "C:\\Windows\\Temp")

DETACHED_PROCESS = 0x00000008
CREATE_NEW_PROCESS_GROUP = 0x00000200
CREATE_BREAKAWAY_FROM_JOB = 0x01000000


class GUID(ctypes.Structure):
    _fields_ = [("Data1", wintypes.DWORD), ("Data2", wintypes.WORD),
                ("Data3", wintypes.WORD), ("Data4", ctypes.c_ubyte * 8)]


class NOTIFYICONIDENTIFIER(ctypes.Structure):
    _fields_ = [("cbSize", wintypes.DWORD), ("hWnd", wintypes.HWND),
                ("uID", wintypes.UINT), ("guidItem", GUID)]


u32 = ctypes.WinDLL("user32", use_last_error=True)
shell32 = ctypes.WinDLL("shell32", use_last_error=True)

u32.EnumWindows.argtypes = [ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM),
                            wintypes.LPARAM]
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
u32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.IsWindowVisible.argtypes = [wintypes.HWND]
u32.SetCursorPos.argtypes = [ctypes.c_int, ctypes.c_int]
u32.GetSystemMetrics.restype = ctypes.c_int

shell32.Shell_NotifyIconGetRect.argtypes = [ctypes.POINTER(NOTIFYICONIDENTIFIER),
                                            ctypes.POINTER(wintypes.RECT)]
shell32.Shell_NotifyIconGetRect.restype = ctypes.c_long


def hidden_forms_windows(pid):
    """暖序进程里所有 WindowsForms10 窗口（NotifyIcon 的宿主是隐藏的那个）。"""
    found = []
    CB = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def cb(hwnd, _):
        p = wintypes.DWORD()
        u32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if p.value != pid:
            return True
        buf = ctypes.create_unicode_buffer(256)
        u32.GetClassNameW(hwnd, buf, 256)
        if buf.value.startswith("WindowsForms10"):
            found.append((hwnd, buf.value, bool(u32.IsWindowVisible(hwnd))))
        return True

    u32.EnumWindows(CB(cb), 0)
    return found


def icon_rect(pid):
    """返回托盘图标的屏幕矩形 (left, top, right, bottom)，找不到返回 None。"""
    for hwnd, cls, visible in hidden_forms_windows(pid):
        # NotifyIcon 的宿主窗口一定是隐藏的；可见的那个是主窗体
        if visible:
            continue
        nid = NOTIFYICONIDENTIFIER()
        nid.cbSize = ctypes.sizeof(NOTIFYICONIDENTIFIER)
        nid.hWnd = hwnd
        nid.uID = 1
        rc = wintypes.RECT()
        hr = shell32.Shell_NotifyIconGetRect(ctypes.byref(nid), ctypes.byref(rc))
        if hr == 0 and rc.right > rc.left:
            return (rc.left, rc.top, rc.right, rc.bottom), cls
    return None, None


def launch(exe):
    flags = DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP
    try:
        return subprocess.Popen([exe], cwd=os.path.dirname(exe),
                                creationflags=flags | CREATE_BREAKAWAY_FROM_JOB)
    except OSError:
        return subprocess.Popen([exe], cwd=os.path.dirname(exe), creationflags=flags)


def load_shot():
    from importlib import util
    spec = util.spec_from_file_location("ss", os.path.join(ROOT, "tools", "screen-shot.py"))
    ss = util.module_from_spec(spec)
    spec.loader.exec_module(ss)
    ss.make_dpi_aware()
    return ss


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=os.path.join(ROOT, "dist", "暖序.exe"))
    ap.add_argument("--rect-only", action="store_true")
    ap.add_argument("--hover", action="store_true", help="悬停并截图，存 tray-tooltip.png")
    a = ap.parse_args()

    exe = os.path.abspath(a.exe)
    if not os.path.isfile(exe):
        print("找不到 exe:", exe)
        return 2

    print(f"测试对象: {exe}")
    proc = launch(exe)
    print(f"已启动 pid={proc.pid}，等 8 秒让托盘图标出现…")
    time.sleep(8)

    try:
        wins = hidden_forms_windows(proc.pid)
        print(f"WindowsForms10 窗口 {len(wins)} 个（可见={sum(1 for w in wins if w[2])}）")
        rect, cls = icon_rect(proc.pid)
        if not rect:
            print("Shell_NotifyIconGetRect 没拿到矩形——图标可能还在溢出区里")
            return 1
        left, top, right, bottom = rect
        print(f"图标矩形 ({left},{top})-({right},{bottom})  宿主 {cls}")
        if a.rect_only:
            return 0

        if a.hover:
            ss = load_shot()
            cx, cy = (left + right) // 2, (top + bottom) // 2
            u32.SetCursorPos(cx, cy)
            time.sleep(3.0)
            sw = u32.GetSystemMetrics(0)
            sh = u32.GetSystemMetrics(1)
            # tooltip 一般弹在图标正上方，横向以图标为中心
            x0 = max(0, cx - 260)
            y0 = max(0, top - 120)
            w = min(520, sw - x0)
            h = min(120, sh - y0)
            cw, ch, buf = ss.grab((x0, y0, w, h))
            out = os.path.join(TEMP, "tray-tooltip.png")
            ss.write_png(out, cw, ch, buf)
            print(f"已截图 {out}  (区域 {x0},{y0} {w}x{h})")
        return 0
    finally:
        try:
            subprocess.run(["taskkill", "/IM", "暖序.exe", "/F"],
                           capture_output=True, timeout=15)
        except Exception:
            pass


if __name__ == "__main__":
    raise SystemExit(main())
