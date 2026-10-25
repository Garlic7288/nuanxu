"""验证「点 X 只收进托盘、不退出」。

为什么不能用 PostMessage(WM_CLOSE)：外部进程投递的 WM_CLOSE 会被 WinForms
归类成 CloseReason.TaskManagerClosing，走的是「直接退出」分支，
测出来的是"点一下程序就没了"，和真实点击完全是两码事。

正确做法：
  1. SetProcessDpiAwareness(2)  —— 否则本进程是 DPI-unaware，拿到的坐标是逻辑值
  2. WM_GETTITLEBARINFOEX 取标题栏各按钮的精确屏幕矩形（rgrect[5] = 关闭按钮）
  3. SetCursorPos + mouse_event 真实点击
  4. 观察：窗口应隐藏、进程应存活

用法：python tray-close-probe.py
"""

import ctypes
import os
import subprocess
import sys
import time
from ctypes import wintypes

INSTALL_DIR = os.path.join(os.environ["LOCALAPPDATA"], "Programs", "暖序")
EXE = os.path.join(INSTALL_DIR, "暖序.exe")
MUTEX = "Local\\WarmSequence.SingleInstance"

DETACHED = 0x00000008
NEW_GROUP = 0x00000200
BREAKAWAY = 0x01000000

WM_GETTITLEBARINFOEX = 0x042F
WM_NCHITTEST = 0x0084
HTCLOSE = 20
CWID_CLOSE = 5
CWID_MAX = 6
MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
user32 = ctypes.WinDLL("user32", use_last_error=True)

# 先声明 DPI 感知，再拿坐标，否则 150% 缩放下坐标全错
try:
    ctypes.WinDLL("shcore").SetProcessDpiAwareness(2)
except OSError:
    user32.SetProcessDPIAware()


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


class TITLEBARINFOEX(ctypes.Structure):
    _fields_ = [("cbSize", wintypes.DWORD),
                ("rcTitleBar", RECT),
                ("rgstate", wintypes.DWORD * CWID_MAX),
                ("rgrect", RECT * CWID_MAX)]


kernel32.OpenMutexW.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR]
kernel32.OpenMutexW.restype = wintypes.HANDLE
kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
user32.IsWindowVisible.argtypes = [wintypes.HWND]
user32.IsWindowVisible.restype = wintypes.BOOL
user32.GetWindowTextLengthW.argtypes = [wintypes.HWND]
user32.GetWindowTextLengthW.restype = ctypes.c_int
user32.GetWindow.argtypes = [wintypes.HWND, wintypes.UINT]
user32.GetWindow.restype = wintypes.HWND
user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
user32.SendMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, ctypes.c_void_p]
user32.SendMessageW.restype = ctypes.c_ssize_t
user32.SetCursorPos.argtypes = [ctypes.c_int, ctypes.c_int]
user32.mouse_event.argtypes = [wintypes.DWORD, wintypes.DWORD, wintypes.DWORD,
                               wintypes.DWORD, ctypes.c_size_t]
user32.GetCursorPos.argtypes = [ctypes.POINTER(wintypes.POINT)]
user32.SetForegroundWindow.argtypes = [wintypes.HWND]
user32.SetForegroundWindow.restype = wintypes.BOOL
user32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(RECT)]

EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)


def alive():
    h = kernel32.OpenMutexW(0x00100000, False, MUTEX)
    if h:
        kernel32.CloseHandle(h)
        return True
    return False


def main_window(pid):
    found = []

    def cb(hwnd, _):
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if (owner.value == pid and user32.IsWindowVisible(hwnd)
                and not user32.GetWindow(hwnd, 4)
                and user32.GetWindowTextLengthW(hwnd) > 0):
            found.append(hwnd)
        return True

    user32.EnumWindows(EnumProc(cb), 0)
    return found[0] if found else 0


def close_button_rect(hwnd):
    info = TITLEBARINFOEX()
    info.cbSize = ctypes.sizeof(TITLEBARINFOEX)
    user32.SendMessageW(hwnd, WM_GETTITLEBARINFOEX, 0, ctypes.byref(info))
    r = info.rgrect[CWID_CLOSE]
    return r.left, r.top, r.right, r.bottom


def make_lparam(x, y):
    return ((y & 0xFFFF) << 16) | (x & 0xFFFF)


def hit_test(hwnd, x, y):
    return user32.SendMessageW(hwnd, WM_NCHITTEST, 0, ctypes.c_void_p(make_lparam(x, y)))


def find_close_button(hwnd):
    """在窗口右上角扫一遍，找 WM_NCHITTEST 返回 HTCLOSE 的点。

    比 WM_GETTITLEBARINFOEX 可靠：后者在最大化 / 某些 DWM 组合下会返回全零矩形。
    命中判据由系统自己给（HTCLOSE），不用猜像素偏移。
    """
    rect = RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    print(f"  窗口矩形 ({rect.left},{rect.top})-({rect.right},{rect.bottom})")

    hits = []
    for dy in range(4, 70, 3):
        for dx in range(4, 240, 4):
            x, y = rect.right - dx, rect.top + dy
            if hit_test(hwnd, x, y) == HTCLOSE:
                hits.append((x, y))
    if not hits:
        return None
    xs = [p[0] for p in hits]
    ys = [p[1] for p in hits]
    return (min(xs) + max(xs)) // 2, (min(ys) + max(ys)) // 2, len(hits)


def main():
    if not os.path.exists(EXE):
        raise SystemExit(f"找不到 {EXE}，先装 1.8.1")
    if alive():
        raise SystemExit("暖序已在运行，先退出再跑这个探针")

    flags = DETACHED | NEW_GROUP
    try:
        p = subprocess.Popen([EXE], cwd=INSTALL_DIR, creationflags=flags | BREAKAWAY)
    except OSError:
        p = subprocess.Popen([EXE], cwd=INSTALL_DIR, creationflags=flags)
    print(f"已启动 pid={p.pid}")
    time.sleep(8)

    hwnd = main_window(p.pid)
    if not hwnd:
        raise SystemExit("没找到主窗口")
    print(f"主窗口 hwnd={hwnd}  alive={alive()}")

    left, top, right, bottom = close_button_rect(hwnd)
    if right <= left or bottom <= top:
        print("  WM_GETTITLEBARINFOEX 返回空矩形，改用 WM_NCHITTEST 扫描")
        found = find_close_button(hwnd)
        if not found:
            raise SystemExit("扫描不到关闭按钮（HTCLOSE）")
        cx, cy, n = found
        print(f"  命中 HTCLOSE 的采样点 {n} 个，取中心 ({cx},{cy})")
    else:
        cx, cy = (left + right) // 2, (top + bottom) // 2
        print(f"  关闭按钮矩形 ({left},{top})-({right},{bottom})  中心=({cx},{cy})")

    # 点之前再确认一次：这个位置确实是关闭按钮
    if hit_test(hwnd, cx, cy) != HTCLOSE:
        raise SystemExit(f"({cx},{cy}) 的 NCHITTEST 不是 HTCLOSE，放弃点击以免误触")
    print(f"  NCHITTEST({cx},{cy}) = HTCLOSE，确认是关闭按钮")

    # 先把窗口置前：点击未激活窗口的第一下常常只用来激活，不会真的触发按钮
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.6)

    user32.SetCursorPos(cx, cy)
    time.sleep(0.3)
    pt = wintypes.POINT()
    user32.GetCursorPos(ctypes.byref(pt))
    print(f"鼠标已到 ({pt.x},{pt.y})")

    def click():
        user32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
        time.sleep(0.06)
        user32.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)

    # 最多点 3 次，间隔 1.2 秒：第一下可能只用于激活窗口
    for attempt in range(1, 4):
        click()
        print(f"第 {attempt} 次点击关闭按钮")
        time.sleep(1.2)
        if not alive() or not user32.IsWindowVisible(hwnd):
            break

    print("\n时间线：")
    t0 = time.monotonic()
    last = None
    for _ in range(20):
        time.sleep(0.3)
        ms = int((time.monotonic() - t0) * 1000)
        a = alive()
        vis = bool(user32.IsWindowVisible(hwnd))
        if (a, vis) != last:
            print(f"  {ms:5d} ms  alive={a}  windowVisible={vis}")
            last = (a, vis)

    ok = alive() and not user32.IsWindowVisible(hwnd)
    print()
    print("结论：" + ("点 X 已收进托盘，进程存活 —— 符合预期"
                    if ok else "点 X 后进程退出或窗口仍在 —— 不符合预期"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
