"""真机验证暖度卡片：按给定 FollowSun 启动 dist 里的暖序，截客户区，再优雅退出。

用法：
  python _warmth-probe.py on    # 跟随日光开
  python _warmth-probe.py off   # 跟随日光关（固定暖度）

会临时改写 settings.json 的 FollowSun，跑完还原（含 .bak）。
必须在同一次工具调用里跑完：调用结束沙箱会硬杀该调用启动的 GUI 进程。
"""
import ctypes
import json
import os
import shutil
import sys
import time
from ctypes import wintypes
from importlib import util as _util

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
_spec = _util.spec_from_file_location("screen_shot", os.path.join(HERE, "screen-shot.py"))
shot = _util.module_from_spec(_spec)
_spec.loader.exec_module(shot)

DATA = os.path.join(os.environ["LOCALAPPDATA"], "Nuánxù")
SETTINGS = os.path.join(DATA, "settings.json")
# 默认测 dist 里的构建；要验装机版就设 WARMSEQUENCE_EXE 指向安装目录的 exe
EXE = os.environ.get("WARMSEQUENCE_EXE") or os.path.join(ROOT, "dist", "暖序.exe")
OUT = os.path.join(ROOT, "release", "preview")

u = ctypes.WinDLL("user32", use_last_error=True)
u.FindWindowW.restype = wintypes.HWND
u.FindWindowW.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR]


def set_follow(on: bool):
    with open(SETTINGS, encoding="utf-8") as f:
        cfg = json.load(f)
    cfg["FollowSun"] = on
    with open(SETTINGS, "w", encoding="utf-8") as f:
        json.dump(cfg, f, ensure_ascii=False)
    return cfg


def wait_window(timeout=25):
    end = time.time() + timeout
    while time.time() < end:
        h = u.FindWindowW(None, "暖序")
        if h:
            return h
        time.sleep(0.4)
    return 0


def main() -> int:
    mode = sys.argv[1] if len(sys.argv) > 1 else "on"
    on = mode == "on"
    shot.make_dpi_aware()

    backup = SETTINGS + ".probe-bak"
    shutil.copy2(SETTINGS, backup)
    if os.path.isfile(SETTINGS + ".bak"):
        shutil.copy2(SETTINGS + ".bak", backup + ".bak")
    try:
        cfg = set_follow(on)
        print(f"已把 FollowSun 设为 {on}，WarmthStrength={cfg.get('WarmthStrength')}")

        import subprocess
        p = subprocess.Popen([EXE], cwd=os.path.dirname(EXE),
                             creationflags=0x00000008 | 0x00000200 | 0x01000000)
        print("已启动 pid", p.pid)
        hwnd = wait_window()
        if not hwnd:
            print("没等到窗口")
            return 2
        time.sleep(4.0)   # 等启动画面淡出 + WebView2 首帧

        r = wintypes.RECT()
        u.GetWindowRect(hwnd, ctypes.byref(r))
        w, h = r.right - r.left, r.bottom - r.top
        print("窗口", r.left, r.top, w, h)

        full = os.path.join(OUT, f"110-warmth-{mode}-full.png")
        gw, gh, bgra = shot.grab((r.left, r.top, w, h))
        shot.write_png(full, gw, gh, bgra)
        print("整窗 ->", full)

        # 卡片区：客户区顶部往下 260 逻辑像素足够覆盖「当前暖度 + 滑块」
        u.GetDpiForWindow.restype = ctypes.c_uint
        scale = (u.GetDpiForWindow(hwnd) or 96) / 96.0
        pt = wintypes.POINT(0, 0)
        u.ClientToScreen(hwnd, ctypes.byref(pt))
        cw, ch = int(760 * scale), int(300 * scale)
        card = os.path.join(OUT, f"110-warmth-{mode}-card.png")
        gw, gh, bgra = shot.grab((pt.x + int(40 * scale), pt.y + int(150 * scale), cw, ch))
        shot.write_png(card, gw, gh, bgra)
        print("卡片 ->", card)

        # 退出：发 Local\WarmSequence.Exit 事件
        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        k32.OpenEventW.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR]
        k32.OpenEventW.restype = wintypes.HANDLE
        k32.SetEvent.argtypes = [wintypes.HANDLE]
        k32.SetEvent.restype = wintypes.BOOL
        ev = k32.OpenEventW(0x0002, False, "Local\\WarmSequence.Exit")
        if not ev:
            print("打不开退出事件")
            p.terminate()
        else:
            k32.SetEvent(ev)
            for _ in range(40):
                if p.poll() is not None:
                    break
                time.sleep(0.25)
            print("退出码", p.poll())
        time.sleep(1.0)
        return 0
    finally:
        shutil.copy2(backup, SETTINGS)
        if os.path.isfile(backup + ".bak"):
            shutil.copy2(backup + ".bak", SETTINGS + ".bak")
        os.remove(backup)
        if os.path.isfile(backup + ".bak"):
            os.remove(backup + ".bak")
        print("settings.json 已还原")


if __name__ == "__main__":
    sys.exit(main())
