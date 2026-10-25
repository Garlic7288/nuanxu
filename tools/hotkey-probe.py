"""全局快捷键真机验收：启动 → 发按键 → 断言 → 退出，必须一次调用跑完
（沙箱在调用结束时硬杀该调用内启动的 GUI 进程）。

覆盖：
  1. 注册：三组热键全部注册成功（没有 hotkeys.log）
  2. 前台不是暖序：把记事本顶到最前，证明热键不依赖窗口焦点
  3. Ctrl+Alt+E：护眼总开关翻转，再按一次翻回来
  4. Ctrl+Alt+F：跟随日光翻转，再按一次翻回来
  5. Ctrl+Alt+P：暂停关护眼，到点自动恢复（用 WARMSEQUENCE_PAUSE_SECONDS 缩短）
  6. 总开关：HotKeysEnabled=false 时热键必须彻底失效

用法：
  python hotkey-probe.py                 # 测 dist 里的构建
  python hotkey-probe.py --exe <path>    # 测装机版
"""

import argparse
import ctypes
import json
import os
import subprocess
import sys
import time
from ctypes import wintypes

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

MUTEX_NAME = "Local\\WarmSequence.SingleInstance"
EXIT_EVENT = "Local\\WarmSequence.Exit"

DETACHED_PROCESS = 0x00000008
CREATE_NEW_PROCESS_GROUP = 0x00000200
CREATE_BREAKAWAY_FROM_JOB = 0x01000000

SETTINGS = os.path.join(os.environ["LOCALAPPDATA"], "Nuánxù", "settings.json")
HOTKEY_LOG = os.path.join(os.environ["LOCALAPPDATA"], "Nuánxù", "hotkeys.log")

PAUSE_SECONDS = 4

VK_CONTROL = 0x11
VK_MENU = 0x12
KEYEVENTF_KEYUP = 0x0002

u32 = ctypes.WinDLL("user32", use_last_error=True)
k32 = ctypes.WinDLL("kernel32", use_last_error=True)

u32.GetForegroundWindow.restype = wintypes.HWND
u32.GetShellWindow.restype = wintypes.HWND
u32.FindWindowW.restype = wintypes.HWND
u32.FindWindowW.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR]
u32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
u32.SetForegroundWindow.argtypes = [wintypes.HWND]
u32.SetForegroundWindow.restype = wintypes.BOOL
u32.SwitchToThisWindow.argtypes = [wintypes.HWND, wintypes.BOOL]
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
u32.keybd_event.argtypes = [wintypes.BYTE, wintypes.BYTE, wintypes.DWORD, ctypes.POINTER(wintypes.ULONG)]

k32.OpenMutexW.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR]
k32.OpenMutexW.restype = wintypes.HANDLE
k32.OpenEventW.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR]
k32.OpenEventW.restype = wintypes.HANDLE
k32.SetEvent.argtypes = [wintypes.HANDLE]
k32.SetEvent.restype = wintypes.BOOL
k32.CloseHandle.argtypes = [wintypes.HANDLE]

SYNCHRONIZE = 0x00100000
EVENT_MODIFY_STATE = 0x0002
SW_MINIMIZE = 6

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


def signal_exit() -> bool:
    h = k32.OpenEventW(EVENT_MODIFY_STATE, False, EXIT_EVENT)
    if not h:
        return False
    k32.SetEvent(h)
    k32.CloseHandle(h)
    return True


def read_settings():
    try:
        with open(SETTINGS, encoding="utf-8-sig") as f:
            return json.load(f)
    except (FileNotFoundError, json.JSONDecodeError):
        return None


def wait_settings(pred, timeout):
    """轮询 settings.json 直到 pred 成立。程序每次改设置都会落盘，所以这是最可靠的观测点。"""
    end = time.time() + timeout
    last = None
    while time.time() < end:
        s = read_settings()
        if s is not None:
            last = s
            if pred(s):
                return True, s
        time.sleep(0.15)
    return False, last


def foreground_info():
    hwnd = u32.GetForegroundWindow()
    if not hwnd:
        return 0, ""
    pid = wintypes.DWORD()
    u32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    n = u32.GetWindowTextLengthW(hwnd)
    buf = ctypes.create_unicode_buffer(n + 1)
    u32.GetWindowTextW(hwnd, buf, n + 1)
    return pid.value, buf.value


u32.GetWindowTextLengthW.argtypes = [wintypes.HWND]
u32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]


def send_combo(vk: int):
    """模拟 Ctrl+Alt+<vk>。用 keybd_event 而不是 PostMessage：
    PostMessage 只是往队列里塞消息，绕过 RegisterHotKey 的全局分发，测不出真实行为。"""
    u32.keybd_event(VK_CONTROL, 0, 0, None)
    u32.keybd_event(VK_MENU, 0, 0, None)
    time.sleep(0.03)
    u32.keybd_event(vk, 0, 0, None)
    time.sleep(0.05)
    u32.keybd_event(vk, 0, KEYEVENTF_KEYUP, None)
    time.sleep(0.03)
    u32.keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, None)
    u32.keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, None)
    time.sleep(0.25)


def launch(exe: str, env_extra=None):
    env = dict(os.environ)
    env.update(env_extra or {})
    flags = DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP
    try:
        return subprocess.Popen([exe], cwd=os.path.dirname(exe), env=env,
                                creationflags=flags | CREATE_BREAKAWAY_FROM_JOB)
    except OSError:
        # 当前进程若已在「不允许 breakaway」的 Job 里，CreateProcess 会直接报 WinError 5。
        # 去掉该标志重试——和 launch-detached.py 的处理保持一致。
        return subprocess.Popen([exe], cwd=os.path.dirname(exe), env=env, creationflags=flags)


def stop(p, timeout=15):
    if p.poll() is not None:
        return p.poll()
    signal_exit()
    end = time.time() + timeout
    while time.time() < end:
        if p.poll() is not None:
            return p.poll()
        time.sleep(0.3)
    p.terminate()
    return p.poll()


def boot(exe, env_extra=None):
    """启动并等到「窗口起来 + WebView2 初始化完成」。
    太早发按键时窗体句柄可能还没建，RegisterHotKey 还没跑，测出来是竞态不是产品行为。"""
    p = launch(exe, env_extra)
    end = time.time() + 30
    while time.time() < end:
        if read_settings() is not None and is_running():
            break
        time.sleep(0.3)
    time.sleep(6.0)
    return p


def push_other_front():
    """把前台从暖序挪走，用来证明热键不依赖窗口焦点。

    这里**刻意不再拉起记事本**：早先版本为了造一个「别人的窗口」会 spawn notepad.exe，
    跑一轮就弹出好几个记事本，用户看到只会以为软件在乱开窗口。
    改用系统桌面窗口（GetShellWindow）——它本来就是顶层窗口，切过去同样能证明
    「暖序没有焦点」，而且不会在屏幕上多出任何东西。
    """
    fpid, title = foreground_info()
    if "暖序" not in title:
        return fpid, title

    shell = u32.GetShellWindow()
    if shell:
        u32.SwitchToThisWindow(shell, True)
        time.sleep(0.6)
    fpid, title = foreground_info()
    if "暖序" in title:
        # 桌面窗口切不过去时，退一步把暖序最小化：前台自然落到别的窗口上，
        # 语义仍是「暖序没有焦点」，效果一样
        u32.ShowWindow(u32.FindWindowW(None, "暖序"), SW_MINIMIZE)
        time.sleep(0.6)
        fpid, title = foreground_info()
    return fpid, title


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=os.path.join(ROOT, "dist", "暖序.exe"))
    a = ap.parse_args()
    exe = os.path.abspath(a.exe)
    if not os.path.isfile(exe):
        print("找不到 exe:", exe)
        return 2

    saved = None
    if os.path.isfile(SETTINGS):
        with open(SETTINGS, "rb") as f:
            saved = f.read()

    print(f"测试对象: {exe}")
    if is_running():
        print("已有一个暖序在跑，先请它退出")
        signal_exit()
        for _ in range(40):
            if not is_running():
                break
            time.sleep(0.25)

    try:
        # ---------- 正常模式 ----------
        print("\n--- 注册与三种热键 ---")
        if os.path.isfile(HOTKEY_LOG):
            os.remove(HOTKEY_LOG)
        p = boot(exe, {"WARMSEQUENCE_PAUSE_SECONDS": str(PAUSE_SECONDS)})
        base = read_settings()
        check("启动后能读到 settings.json", base is not None)
        if base is None:
            return 1
        check("三组热键应全部注册成功（无 hotkeys.log）", not os.path.isfile(HOTKEY_LOG),
              f"日志={open(HOTKEY_LOG, encoding='utf-8').read().strip() if os.path.isfile(HOTKEY_LOG) else ''}")

        fpid, ftitle = push_other_front()
        check("前台应已不是暖序（否则测不出「全局」）",
              "暖序" not in ftitle, f"前台={ftitle!r} pid={fpid}")

        # Ctrl+Alt+E
        before = read_settings()["Enabled"]
        send_combo(ord("E"))
        ok, s = wait_settings(lambda x: x["Enabled"] != before, 6)
        check("Ctrl+Alt+E 应翻转护眼总开关", ok, f"{before} → {s['Enabled'] if s else '?'}")
        send_combo(ord("E"))
        ok, s = wait_settings(lambda x: x["Enabled"] == before, 6)
        check("再按一次 Ctrl+Alt+E 应翻回来", ok, f"→ {s['Enabled'] if s else '?'}")

        # Ctrl+Alt+F
        before = read_settings()["FollowSun"]
        send_combo(ord("F"))
        ok, s = wait_settings(lambda x: x["FollowSun"] != before, 6)
        check("Ctrl+Alt+F 应翻转跟随日光", ok, f"{before} → {s['FollowSun'] if s else '?'}")
        send_combo(ord("F"))
        ok, s = wait_settings(lambda x: x["FollowSun"] == before, 6)
        check("再按一次 Ctrl+Alt+F 应翻回来", ok, f"→ {s['FollowSun'] if s else '?'}")

        # Ctrl+Alt+P
        print(f"\n--- 暂停（{PAUSE_SECONDS} 秒后自动恢复）---")
        send_combo(ord("P"))
        ok, s = wait_settings(lambda x: not x["Enabled"], 6)
        check("Ctrl+Alt+P 应立即关掉护眼", ok, f"Enabled={s['Enabled'] if s else '?'}")
        ok, s = wait_settings(lambda x: x["Enabled"], PAUSE_SECONDS + 12)
        check("到点应自动恢复护眼", ok, f"Enabled={s['Enabled'] if s else '?'}")
        # 恢复后 Enabled 应该和按之前一致；按之前是关的话这里会是关，属于正常
        send_combo(ord("P"))
        ok, s = wait_settings(lambda x: not x["Enabled"], 6)
        if ok:
            send_combo(ord("P"))
            ok2, s = wait_settings(lambda x: x["Enabled"], 6)
            check("暂停中再按 Ctrl+Alt+P 应立即恢复", ok2, f"Enabled={s['Enabled'] if s else '?'}")

        # ---------- 护眼本来就关着时按暂停 ----------
        # 这是个真实缺陷：暂停的语义是「先关、到点再开」，护眼本来就是关的还照样武装计时器，
        # 到点就会把用户自己关掉的护眼打开。修复后 StartPause 直接拒绝，不启动计时器。
        print("\n--- 护眼本来就关着时按暂停 ---")
        if not read_settings()["Enabled"]:
            send_combo(ord("E"))
            wait_settings(lambda x: x["Enabled"], 6)
        send_combo(ord("E"))
        ok, s = wait_settings(lambda x: not x["Enabled"], 6)
        check("先用 Ctrl+Alt+E 关掉护眼", ok, f"Enabled={s['Enabled'] if s else '?'}")
        send_combo(ord("P"))
        time.sleep(1.0)
        check("按暂停后护眼仍是关闭", not read_settings()["Enabled"])
        time.sleep(PAUSE_SECONDS + 6)
        after = read_settings()["Enabled"]
        check("到点后护眼不应被自动打开", not after, f"Enabled={after}")
        # 复原，别影响后面的用例
        send_combo(ord("E"))
        wait_settings(lambda x: x["Enabled"], 6)

        rc = stop(p)
        check("能优雅退出", rc == 0, f"退出码 {rc}")

        # ---------- 总开关关闭 ----------
        print("\n--- 关闭「启用全局快捷键」后热键必须失效 ---")
        cur = read_settings()
        cur["HotKeysEnabled"] = False
        with open(SETTINGS, "w", encoding="utf-8") as f:
            json.dump(cur, f, ensure_ascii=False, indent=2)
        p = boot(exe, {"WARMSEQUENCE_PAUSE_SECONDS": str(PAUSE_SECONDS)})
        check("总开关关闭后不应留下 hotkeys.log", not os.path.isfile(HOTKEY_LOG))
        push_other_front()
        before = read_settings()["Enabled"]
        send_combo(ord("E"))
        time.sleep(3.0)
        after = read_settings()["Enabled"]
        check("总开关关闭时 Ctrl+Alt+E 不应有任何效果", before == after, f"{before} → {after}")
        rc = stop(p)
        check("关闭总开关后仍能优雅退出", rc == 0, f"退出码 {rc}")
    finally:
        if saved is not None:
            with open(SETTINGS, "wb") as f:
                f.write(saved)
            print("\nsettings.json 已还原")
        if is_running():
            signal_exit()
            time.sleep(2)

    print("\n结果：" + ("全部通过" if ok_all else "有失败项"))
    return 0 if ok_all else 1


if __name__ == "__main__":
    sys.exit(main())
