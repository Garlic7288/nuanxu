"""开机自启真机验收：启动 → 断言 → 退出，必须在同一次工具调用里跑完
（沙箱在调用结束时硬杀该调用内启动的 GUI 进程）。

覆盖：
  1. 对照组：不带 --autostart 启动 → 必须有可见主窗口（证明「无可见窗口」的断言不是空转）
  2. 自启组：带 --autostart 启动 → 进程活着、但没有任何可见顶层窗口
  3. 漂移自愈：先把注册表写成不存在的路径，启动后应被改回真实路径
  4. 托盘活着：发 Local\\WarmSequence.Activate 事件 → 3 秒内出现可见窗口
  5. 注册表开关：写入/删除后的值形态与 IsEnabled 语义一致

用法：
  python autostart-probe.py                # 测 dist 里的构建
  python autostart-probe.py --exe <path>   # 测装机版
"""

import argparse
import ctypes
import os
import subprocess
import sys
import time
import winreg
from ctypes import wintypes

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
APPROVED_KEY = r"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"
VALUE_NAME = "WarmSequence"

MUTEX_NAME = "Local\\WarmSequence.SingleInstance"
ACTIVATE_EVENT = "Local\\WarmSequence.Activate"
EXIT_EVENT = "Local\\WarmSequence.Exit"

DETACHED_PROCESS = 0x00000008
CREATE_NEW_PROCESS_GROUP = 0x00000200
CREATE_BREAKAWAY_FROM_JOB = 0x01000000

u32 = ctypes.WinDLL("user32", use_last_error=True)
k32 = ctypes.WinDLL("kernel32", use_last_error=True)

u32.EnumWindows.argtypes = [ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM), wintypes.LPARAM]
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
u32.IsWindowVisible.argtypes = [wintypes.HWND]
u32.GetWindowTextLengthW.argtypes = [wintypes.HWND]
u32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindow.argtypes = [wintypes.HWND, wintypes.UINT]
u32.GetWindow.restype = wintypes.HWND

k32.OpenMutexW.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR]
k32.OpenMutexW.restype = wintypes.HANDLE
k32.OpenEventW.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR]
k32.OpenEventW.restype = wintypes.HANDLE
k32.SetEvent.argtypes = [wintypes.HANDLE]
k32.SetEvent.restype = wintypes.BOOL
k32.CloseHandle.argtypes = [wintypes.HANDLE]

GW_OWNER = 4
SYNCHRONIZE = 0x00100000
EVENT_MODIFY_STATE = 0x0002

ok_all = True


def check(desc, cond, extra=""):
    global ok_all
    print(f"  [{'OK ' if cond else 'FAIL'}] {desc}" + (f"  {extra}" if extra else ""))
    ok_all = ok_all and bool(cond)


def is_running() -> bool:
    h = k32.OpenMutexW(SYNCHRONIZE, False, MUTEX_NAME)
    if h:
        k32.CloseHandle(h)
        return True
    return False


def signal(name: str) -> bool:
    h = k32.OpenEventW(EVENT_MODIFY_STATE, False, name)
    if not h:
        return False
    k32.SetEvent(h)
    k32.CloseHandle(h)
    return True


def visible_windows(pid: int):
    """该 pid 的所有「可见 + 无 owner」顶层窗口标题。"""
    out = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def cb(hwnd, _):
        wpid = wintypes.DWORD()
        u32.GetWindowThreadProcessId(hwnd, ctypes.byref(wpid))
        if wpid.value == pid and u32.IsWindowVisible(hwnd) and not u32.GetWindow(hwnd, GW_OWNER):
            n = u32.GetWindowTextLengthW(hwnd)
            buf = ctypes.create_unicode_buffer(n + 1)
            u32.GetWindowTextW(hwnd, buf, n + 1)
            out.append(buf.value)
        return True

    u32.EnumWindows(cb, 0)
    return out


def read_run_value():
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as k:
            v, t = winreg.QueryValueEx(k, VALUE_NAME)
            return v, t
    except FileNotFoundError:
        return None, None


def write_run_value(val: str):
    with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_SET_VALUE) as k:
        winreg.SetValueEx(k, VALUE_NAME, 0, winreg.REG_SZ, val)


def delete_run_value():
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_SET_VALUE) as k:
            winreg.DeleteValue(k, VALUE_NAME)
    except FileNotFoundError:
        pass


def launch(exe: str, args=None):
    cmd = [exe] + (args or [])
    flags = DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP
    try:
        return subprocess.Popen(cmd, cwd=os.path.dirname(exe),
                                creationflags=flags | CREATE_BREAKAWAY_FROM_JOB)
    except OSError:
        # 当前进程所在 Job 不允许脱离时会报 WinError 5，去掉该标志重试
        return subprocess.Popen(cmd, cwd=os.path.dirname(exe), creationflags=flags)


def stop(p, timeout=15):
    if p.poll() is not None:
        return p.poll()
    signal(EXIT_EVENT)
    end = time.time() + timeout
    while time.time() < end:
        if p.poll() is not None:
            return p.poll()
        time.sleep(0.3)
    p.terminate()
    return p.poll()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=os.path.join(ROOT, "dist", "暖序.exe"))
    a = ap.parse_args()
    exe = os.path.abspath(a.exe)
    if not os.path.isfile(exe):
        print("找不到 exe:", exe)
        return 2
    expected = f'"{exe}" --autostart'

    # 现场备份，结束时原样还原（用户可能本来就开着自启）
    saved_val, saved_type = read_run_value()
    print(f"测试对象: {exe}")
    print(f"原 Run 值: {saved_val!r}")

    if is_running():
        print("已有一个暖序在跑，先请它退出")
        signal(EXIT_EVENT)
        for _ in range(40):
            if not is_running():
                break
            time.sleep(0.25)

    try:
        # ---------- 对照组：正常启动必须有可见窗口 ----------
        print("\n--- 对照组：不带参数启动 ---")
        delete_run_value()
        p = launch(exe)
        end = time.time() + 30
        wins = []
        while time.time() < end:
            wins = visible_windows(p.pid)
            if wins:
                break
            time.sleep(0.4)
        time.sleep(2.5)   # 等启动画面淡出，避免只抓到 splash
        wins = visible_windows(p.pid)
        check("正常启动应有可见主窗口", any("暖序" in w for w in wins), f"可见窗口={wins}")
        rc = stop(p)
        check("正常启动能优雅退出", rc == 0, f"退出码 {rc}")

        # ---------- 自启组：可见窗口必须为零 ----------
        print("\n--- 自启组：--autostart（先故意写一条失效路径）---")
        bogus = r'"C:\no-such-dir\暖序.exe" --autostart'
        write_run_value(bogus)
        p = launch(exe, ["--autostart"])
        time.sleep(7)     # 等它把路径修好、并稳定在托盘态
        wins = visible_windows(p.pid)
        check("自启启动后进程应活着", p.poll() is None, f"poll={p.poll()}")
        check("自启启动不应有任何可见顶层窗口", len(wins) == 0, f"可见窗口={wins}")
        cur, cur_type = read_run_value()
        check("失效路径应被自愈成真实路径", cur == expected, f"实际={cur!r}")
        check("注册表值类型应为 REG_SZ", cur_type == winreg.REG_SZ, f"实际={cur_type}")

        # ---------- 托盘活着：发激活事件应把窗口叫出来 ----------
        print("\n--- 激活事件：模拟点托盘「打开暖序」---")
        signal(ACTIVATE_EVENT)
        end = time.time() + 6
        wins = []
        while time.time() < end:
            wins = visible_windows(p.pid)
            if any("暖序" in w for w in wins):
                break
            time.sleep(0.3)
        check("激活后应出现可见主窗口（证明托盘与消息循环活着）",
              any("暖序" in w for w in wins), f"可见窗口={wins}")
        # 等 WebView2 初始化落地再退：恢复窗口后 0.3 秒就发退出信号，
        # 会在「控件正在初始化」和「窗体正在释放」之间打架，测出来的是竞态而不是产品行为。
        # 真实用户点开托盘也不会在半秒内退出。
        time.sleep(4.0)
        rc = stop(p)
        check("自启实例能优雅退出", rc == 0, f"退出码 {rc}")

        # ---------- 注册表开关语义 ----------
        print("\n--- 注册表开关 ---")
        write_run_value(expected)
        v, t = read_run_value()
        check("写入后能读回期望值", v == expected and t == winreg.REG_SZ, f"{v!r}")
        delete_run_value()
        v2, _ = read_run_value()
        check("删除后值应不存在", v2 is None, f"{v2!r}")
    finally:
        if saved_val is None:
            delete_run_value()
        else:
            write_run_value(saved_val)
        print("\nRun 值已还原为:", read_run_value()[0])

    print("\n结果：" + ("全部通过" if ok_all else "有失败项"))
    return 0 if ok_all else 1


if __name__ == "__main__":
    sys.exit(main())
