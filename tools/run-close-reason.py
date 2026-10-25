"""跑 CloseReasonProbe：外部投递 WM_CLOSE，读回 WinForms 给出的 CloseReason。

结论只有两种：
  UserClosing        → 「PostMessage(WM_CLOSE) 模拟点 X」这个测试方法是成立的
  其它（TaskManagerClosing 等） → 该方法不成立，不能用它判断「点 X 是否收进托盘」

用法：python run-close-reason.py
"""

import ctypes
import os
import subprocess
import sys
import time
from ctypes import wintypes

HERE = os.path.dirname(os.path.abspath(__file__))
PROJ = os.path.join(HERE, "CloseReasonProbe")
EXE = os.path.join(PROJ, "bin", "Debug", "net8.0-windows", "CloseReasonProbe.exe")
LOG = r"C:\wxaudit\close-reason.txt"

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
user32 = ctypes.WinDLL("user32", use_last_error=True)

kernel32.CreateMutexW.restype = wintypes.HANDLE
user32.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
user32.PostMessageW.restype = wintypes.BOOL
user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
user32.GetWindowTextLengthW.argtypes = [wintypes.HWND]
user32.GetWindowTextLengthW.restype = ctypes.c_int
user32.GetWindow.argtypes = [wintypes.HWND, wintypes.UINT]
user32.GetWindow.restype = wintypes.HWND
user32.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]

EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.GetWindowTextW.restype = ctypes.c_int
user32.IsWindowVisible.argtypes = [wintypes.HWND]
user32.IsWindowVisible.restype = wintypes.BOOL


def title_of(hwnd):
    buf = ctypes.create_unicode_buffer(256)
    user32.GetWindowTextW(hwnd, buf, 256)
    return buf.value


def list_windows(pid):
    out = []

    def cb(hwnd, _):
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid:
            out.append((hwnd, title_of(hwnd), bool(user32.IsWindowVisible(hwnd)),
                        bool(user32.GetWindow(hwnd, 4))))
        return True

    user32.EnumWindows(EnumProc(cb), 0)
    return out


def find_window(pid, want_title):
    wins = list_windows(pid)
    print(f"  该进程顶层窗口 {len(wins)} 个：")
    for hwnd, t, vis, owned in wins:
        print(f"    hwnd={hwnd} title={t!r} visible={vis} hasOwner={owned}")
    for hwnd, t, vis, owned in wins:
        if t == want_title:
            return hwnd
    return 0


def main():
    os.makedirs(r"C:\wxaudit", exist_ok=True)
    if os.path.exists(LOG):
        os.remove(LOG)

    print("编译探针…")
    r = subprocess.run(["dotnet", "build", PROJ, "-v", "q", "--nologo"],
                       capture_output=True, text=True)
    if r.returncode != 0:
        print(r.stdout[-3000:])
        print(r.stderr[-3000:])
        raise SystemExit("编译失败")

    p = subprocess.Popen([EXE], creationflags=0x00000008 | 0x00000200)
    print(f"已启动探针 pid={p.pid}")
    time.sleep(4)

    hwnd = find_window(p.pid, "CloseReasonProbe")
    if not hwnd:
        raise SystemExit("找不到标题为 CloseReasonProbe 的窗口")
    print(f"窗口 hwnd={hwnd}，投递 WM_CLOSE")

    ok = user32.PostMessageW(hwnd, 0x0010, 0, 0)
    print(f"  PostMessage -> {bool(ok)}")
    time.sleep(2)

    alive = p.poll() is None
    print(f"  投递后进程存活: {alive}")

    text = open(LOG, encoding="utf-8").read() if os.path.exists(LOG) else "<无日志>"
    print("\n--- 探针日志 ---")
    print(text.strip())

    if not alive:
        print("\n结论：外部 WM_CLOSE 直接把程序关掉了 → 该方法不能用来模拟「点 X」")
        return 1
    if "CloseReason=UserClosing" in text:
        print("\n结论：外部 WM_CLOSE 被归为 UserClosing → 可以用来模拟「点 X」")
        return 0
    print("\n结论：归为其它 CloseReason，方法不成立")
    return 1


if __name__ == "__main__":
    sys.exit(main())
