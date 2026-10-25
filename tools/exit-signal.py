"""模拟安装器的 RequestAppExit：向正在运行的暖序发退出事件并轮询等它退出。

对应 installer\暖序.iss 里 [Code] 段的同一套调用：
  OpenMutexW(SYNCHRONIZE, False, 'Local\\WarmSequence.SingleInstance')   -> 探活
  OpenEventW(EVENT_MODIFY_STATE, False, 'Local\\WarmSequence.Exit')      -> 取事件
  SetEvent(H)                                                            -> 发信号
  while 轮询 OpenMutexW                                                  -> 等它退出

用法：
  python exit-signal.py                # 发退出信号，最多等 15 秒
  python exit-signal.py --timeout 5000
  python exit-signal.py --probe        # 只探活，不发信号
"""

import argparse
import ctypes
import time
from ctypes import wintypes

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

MUTEX_NAME = "Local\\WarmSequence.SingleInstance"
EVENT_NAME = "Local\\WarmSequence.Exit"

SYNCHRONIZE = 0x00100000
EVENT_MODIFY_STATE = 0x0002

kernel32.OpenMutexW.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR]
kernel32.OpenMutexW.restype = wintypes.HANDLE
kernel32.OpenEventW.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR]
kernel32.OpenEventW.restype = wintypes.HANDLE
kernel32.SetEvent.argtypes = [wintypes.HANDLE]
kernel32.SetEvent.restype = wintypes.BOOL
kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
kernel32.CloseHandle.restype = wintypes.BOOL


def is_running() -> bool:
    h = kernel32.OpenMutexW(SYNCHRONIZE, False, MUTEX_NAME)
    if h:
        kernel32.CloseHandle(h)
        return True
    return False


def has_event() -> bool:
    h = kernel32.OpenEventW(EVENT_MODIFY_STATE, False, EVENT_NAME)
    if h:
        kernel32.CloseHandle(h)
        return True
    return False


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--timeout", type=int, default=15000, help="等待退出的毫秒数")
    ap.add_argument("--probe", action="store_true", help="只探活，不发信号")
    args = ap.parse_args()

    running = is_running()
    print(f"mutex  {MUTEX_NAME}  -> {'存在（程序在运行）' if running else '不存在'}")
    print(f"event  {EVENT_NAME}  -> {'存在' if has_event() else '不存在'}")

    if args.probe:
        return 0 if running else 1

    if not running:
        print("程序没在运行，无需发信号")
        return 0

    h = kernel32.OpenEventW(EVENT_MODIFY_STATE, False, EVENT_NAME)
    if not h:
        print(f"OpenEventW 失败，err={ctypes.get_last_error()}（老版本程序没有这个事件）")
        return 2
    ok = kernel32.SetEvent(h)
    kernel32.CloseHandle(h)
    print(f"SetEvent -> {bool(ok)}")

    t0 = time.monotonic()
    while (time.monotonic() - t0) * 1000 < args.timeout:
        if not is_running():
            ms = int((time.monotonic() - t0) * 1000)
            print(f"已退出，耗时 {ms} ms")
            return 0
        time.sleep(0.2)

    print(f"超时 {args.timeout} ms 仍未退出")
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
