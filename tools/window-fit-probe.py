"""窗口尺寸验收：启动后必须是「按内容开窗」，而不是一上来最大化。

覆盖三种情况：
  1. 全新用户（没有 settings.json）→ 窗口未最大化、客户区 ≈ 1280×920 CSS
  2. 老配置升级（StartMaximized=true + 1120×850，没有 WindowFitContent）→ 一次性迁移成内容尺寸
  3. 用户自己调过尺寸（WindowFitContent=true + 2000×1200）→ 尊重用户，不被覆盖

必须在同一次工具调用里跑完（沙箱在调用结束时硬杀该调用内启动的 GUI 进程）。

用法：
  python window-fit-probe.py                # 测 dist
  python window-fit-probe.py --exe <path>   # 测装机版
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
SETTINGS = os.path.join(os.environ["LOCALAPPDATA"], "Nuánxù", "settings.json")
MUTEX_NAME = "Local\\WarmSequence.SingleInstance"
EXIT_EVENT = "Local\\WarmSequence.Exit"

DETACHED_PROCESS = 0x00000008
CREATE_NEW_PROCESS_GROUP = 0x00000200
CREATE_BREAKAWAY_FROM_JOB = 0x01000000

CONTENT_W_CSS = 1280
CONTENT_H_CSS = 920

u32 = ctypes.WinDLL("user32", use_last_error=True)
k32 = ctypes.WinDLL("kernel32", use_last_error=True)

u32.FindWindowW.restype = wintypes.HWND
u32.FindWindowW.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR]
u32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
u32.GetClientRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
u32.IsZoomed.argtypes = [wintypes.HWND]
u32.IsZoomed.restype = wintypes.BOOL
u32.SystemParametersInfoW.argtypes = [wintypes.UINT, wintypes.UINT, ctypes.c_void_p, wintypes.UINT]
u32.SetProcessDpiAwarenessContext.argtypes = [ctypes.c_void_p]
# HDC 是指针宽度，不声明 restype 会被截成 int32，64 位下句柄就废了
u32.GetDC.restype = wintypes.HDC
u32.GetDC.argtypes = [wintypes.HWND]
u32.ReleaseDC.argtypes = [wintypes.HWND, wintypes.HDC]

k32.OpenMutexW.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR]
k32.OpenMutexW.restype = wintypes.HANDLE
k32.OpenEventW.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR]
k32.OpenEventW.restype = wintypes.HANDLE
k32.SetEvent.argtypes = [wintypes.HANDLE]
k32.CloseHandle.argtypes = [wintypes.HANDLE]

SYNCHRONIZE = 0x00100000
EVENT_MODIFY_STATE = 0x0002

ok_all = True


def check(desc, cond, extra=""):
    global ok_all
    print(f"  [{'OK ' if cond else 'FAIL'}] {desc}" + (f"  {extra}" if extra else ""))
    ok_all = ok_all and bool(cond)


def make_dpi_aware():
    # -4 = PER_MONITOR_AWARE_V2。不设的话本进程 DPI-unaware，
    # 读到的窗口矩形是逻辑值，和真实设备像素对不上，判断全错
    try:
        u32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
    except Exception:
        pass


def screen_scale() -> float:
    gdi = ctypes.WinDLL("gdi32")
    gdi.GetDeviceCaps.argtypes = [ctypes.c_void_p, ctypes.c_int]
    gdi.GetDeviceCaps.restype = ctypes.c_int
    hdc = u32.GetDC(0)
    dpi = gdi.GetDeviceCaps(hdc, 88)
    u32.ReleaseDC(0, hdc)
    return dpi / 96.0


def work_area():
    class R(ctypes.Structure):
        _fields_ = [("l", wintypes.LONG), ("t", wintypes.LONG),
                    ("r", wintypes.LONG), ("b", wintypes.LONG)]
    r = R()
    u32.SystemParametersInfoW(0x0030, 0, ctypes.byref(r), 0)
    return r.r - r.l, r.b - r.t


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


def write_settings(obj):
    os.makedirs(os.path.dirname(SETTINGS), exist_ok=True)
    with open(SETTINGS, "w", encoding="utf-8") as f:
        json.dump(obj, f, ensure_ascii=False, indent=2)


def launch(exe):
    flags = DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP
    try:
        return subprocess.Popen([exe], cwd=os.path.dirname(exe),
                                creationflags=flags | CREATE_BREAKAWAY_FROM_JOB)
    except OSError:
        return subprocess.Popen([exe], cwd=os.path.dirname(exe), creationflags=flags)


def stop(p, timeout=20):
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


def measure(exe):
    """启动 → 等界面稳定 → 读窗口矩形/客户区/是否最大化 → 退出。"""
    p = launch(exe)
    end = time.time() + 30
    hwnd = 0
    while time.time() < end:
        hwnd = u32.FindWindowW(None, "暖序")
        if hwnd and is_running():
            break
        time.sleep(0.3)
    time.sleep(6.0)   # 等启动画面淡出、WebView2 初始化完

    hwnd = u32.FindWindowW(None, "暖序")
    wr, cr = wintypes.RECT(), wintypes.RECT()
    u32.GetWindowRect(hwnd, ctypes.byref(wr))
    u32.GetClientRect(hwnd, ctypes.byref(cr))
    info = {
        "outer": (wr.right - wr.left, wr.bottom - wr.top),
        "client": (cr.right - cr.left, cr.bottom - cr.top),
        "origin": (wr.left, wr.top),
        "zoomed": bool(u32.IsZoomed(hwnd)),
    }
    rc = stop(p)
    info["rc"] = rc
    info["settings"] = read_settings()
    return info


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=os.path.join(ROOT, "dist", "暖序.exe"))
    a = ap.parse_args()
    exe = os.path.abspath(a.exe)
    if not os.path.isfile(exe):
        print("找不到 exe:", exe)
        return 2

    make_dpi_aware()
    scale = screen_scale()
    ww, wh = work_area()
    print(f"测试对象: {exe}")
    print(f"屏幕缩放 {scale:.2f}  工作区 {ww}×{wh} 设备像素")
    exp_client_w = round(CONTENT_W_CSS * scale)
    exp_page_h = round(CONTENT_H_CSS * scale)
    print(f"内容自适应期望客户区宽 {exp_client_w}，页面高 {exp_page_h}（另加菜单栏）")

    saved = None
    if os.path.isfile(SETTINGS):
        with open(SETTINGS, "rb") as f:
            saved = f.read()
    saved_bak = None
    if os.path.isfile(SETTINGS + ".bak"):
        with open(SETTINGS + ".bak", "rb") as f:
            saved_bak = f.read()

    if is_running():
        print("已有一个暖序在跑，先请它退出")
        signal_exit()
        for _ in range(40):
            if not is_running():
                break
            time.sleep(0.25)

    try:
        # ---------- 1. 全新用户 ----------
        print("\n--- 全新用户（无 settings.json）---")
        # 必须连 .bak 一起删：LoadSettings 在主文件不存在时会退回 .bak，
        # 只删主文件的话上一个用例的窗口尺寸会被"恢复"回来，测出来的是脏数据
        for path in (SETTINGS, SETTINGS + ".bak"):
            if os.path.isfile(path):
                os.remove(path)
        r = measure(exe)
        check("不应最大化启动", not r["zoomed"], f"zoomed={r['zoomed']} 外框={r['outer']}")
        check("客户区宽度应等于内容宽度", abs(r["client"][0] - exp_client_w) <= 2,
              f"实际 {r['client'][0]}  期望 {exp_client_w}")
        menu_h = r["client"][1] - exp_page_h
        check("客户区高度 = 页面高 + 菜单栏（30~90）", 30 <= menu_h <= 90,
              f"实际 {r['client'][1]}  菜单栏 {menu_h}")
        check("外框应小于工作区（不是铺满）", r["outer"][0] < ww and r["outer"][1] < wh,
              f"外框 {r['outer']} vs 工作区 {ww}×{wh}")
        # 居中不是必须的，但改了尺寸后如果还停在旧位置会显得很怪，这里只报告不断言
        print(f"       窗口位置 {r['origin']}（居中约 ({round((ww - r['outer'][0]) / 2)}, "
              f"{round((wh - r['outer'][1]) / 2)})）")
        check("能优雅退出", r["rc"] == 0, f"退出码 {r['rc']}")
        s = r["settings"] or {}
        check("退出后应记住窗口尺寸", s.get("WindowWidth", 0) > 0, f"WindowWidth={s.get('WindowWidth')}")
        check("退出后仍标记为已迁移", s.get("WindowFitContent") is True)

        # ---------- 2. 老配置升级 ----------
        print("\n--- 老配置升级（StartMaximized=true，1120×850，无迁移标记）---")
        old = {
            "Enabled": True, "FollowSun": True, "RemindersEnabled": False,
            "ReminderMinutes": 1, "ReminderText": "抬头看看远处，让眼睛放松一下。",
            "WarmthStrength": 65, "ReminderSound": True, "HotKeysEnabled": True,
            "StartMaximized": True, "WindowWidth": 1120, "WindowHeight": 850,
            "City": "广州",
        }
        write_settings(old)
        r = measure(exe)
        check("老配置不应再最大化启动", not r["zoomed"], f"zoomed={r['zoomed']}")
        check("老配置应被迁移成内容尺寸", abs(r["client"][0] - exp_client_w) <= 2,
              f"实际 {r['client'][0]}  期望 {exp_client_w}")
        s = r["settings"] or {}
        check("迁移后 StartMaximized 应为 false", s.get("StartMaximized") is False, f"={s.get('StartMaximized')}")
        check("迁移后应写入 WindowFitContent=true", s.get("WindowFitContent") is True)

        # ---------- 3. 用户自己调过尺寸 ----------
        print("\n--- 用户调过尺寸（2000×1200，已迁移）---")
        cur = r["settings"] or {}
        cur.update({"StartMaximized": False, "WindowFitContent": True,
                    "WindowWidth": 2000, "WindowHeight": 1200})
        write_settings(cur)
        r = measure(exe)
        check("应尊重用户保存的窗口尺寸", r["outer"] == (2000, 1200),
              f"实际 {r['outer']}  期望 (2000, 1200)")
        check("用户尺寸下也不应最大化", not r["zoomed"])
    finally:
        if saved is not None:
            with open(SETTINGS, "wb") as f:
                f.write(saved)
            print("\nsettings.json 已还原")
        if saved_bak is not None:
            with open(SETTINGS + ".bak", "wb") as f:
                f.write(saved_bak)
        if is_running():
            signal_exit()
            time.sleep(2)

    print("\n结果：" + ("全部通过" if ok_all else "有失败项"))
    return 0 if ok_all else 1


if __name__ == "__main__":
    sys.exit(main())
