"""查「暖色写入后又被抹回原色」到底是谁干的。

同时盯两件事：
  1. 硬件 LUT 的 G255（暖度 80 时应为 60816，原色是 65535）
  2. settings.json 里的 WarmthStrength（若被改成 0，说明是我们自己写回了原色）

只要 LUT 变化或 WarmthStrength 变化就打印一行，带时间戳。
这样能区分「有外部程序抹 LUT」和「我们自己的逻辑把暖度改成了 0」。

用法：python who-clobbers.py [秒数]
"""

import ctypes
import json
import os
import subprocess
import sys
import time
from ctypes import wintypes

INSTALL_DIR = os.path.join(os.environ["LOCALAPPDATA"], "Programs", "暖序")
EXE = os.path.join(INSTALL_DIR, "暖序.exe")
DATA = os.path.join(os.environ["LOCALAPPDATA"], "Nuánxù")
SETTINGS = os.path.join(DATA, "settings.json")

TEST = ('{"Enabled":true,"FollowSun":false,"RemindersEnabled":false,"ReminderMinutes":30,'
        '"ReminderText":"抬头看看远处，让眼睛放松一下。","SkipUntil":null,"WarmthStrength":80,"ReminderSound":false,'
        '"StartMaximized":true,"WindowWidth":1400,"WindowHeight":1000,"City":"\\u5E7F\\u5DDE"}')

gdi32 = ctypes.WinDLL("gdi32")
user32 = ctypes.WinDLL("user32")
user32.GetDC.argtypes = [wintypes.HWND]
user32.GetDC.restype = wintypes.HDC
user32.ReleaseDC.argtypes = [wintypes.HWND, wintypes.HDC]
user32.ReleaseDC.restype = ctypes.c_int
gdi32.GetDeviceGammaRamp.argtypes = [wintypes.HDC, ctypes.c_void_p]
gdi32.SetDeviceGammaRamp.argtypes = [wintypes.HDC, ctypes.c_void_p]


def g255():
    h = user32.GetDC(None)
    try:
        buf = (ctypes.c_ushort * 768)()
        if not gdi32.GetDeviceGammaRamp(h, ctypes.byref(buf)):
            return -1
        return buf[511]
    finally:
        user32.ReleaseDC(None, h)


def identity():
    h = user32.GetDC(None)
    try:
        ramp = []
        for _ in range(3):
            ramp += [i * 257 for i in range(256)]
        buf = (ctypes.c_ushort * 768)(*ramp)
        gdi32.SetDeviceGammaRamp(h, ctypes.byref(buf))
    finally:
        user32.ReleaseDC(None, h)


def read_warmth():
    try:
        with open(SETTINGS, "r", encoding="utf-8") as f:
            return json.load(f).get("WarmthStrength")
    except Exception:
        return "?"


def main():
    secs = float(sys.argv[1]) if len(sys.argv) > 1 else 14.0

    backup = None
    if os.path.exists(SETTINGS):
        backup = open(SETTINGS, "rb").read()
    with open(SETTINGS, "w", encoding="utf-8") as f:
        f.write(TEST)

    identity()
    print(f"启动前 G255={g255()}  WarmthStrength={read_warmth()}")

    p = subprocess.Popen([EXE], cwd=INSTALL_DIR, creationflags=0x00000008 | 0x00000200)
    print(f"已启动 pid={p.pid}，采样 {secs}s")

    t0 = time.monotonic()
    prev_lut, prev_w = None, None
    try:
        while time.monotonic() - t0 < secs:
            v, w = g255(), read_warmth()
            if v != prev_lut or w != prev_w:
                print(f"  {time.monotonic()-t0:6.2f}s  G255={v:5d}  WarmthStrength={w}")
                prev_lut, prev_w = v, w
            time.sleep(0.05)
    finally:
        try:
            subprocess.run([sys.executable,
                            os.path.join(os.path.dirname(os.path.abspath(__file__)), "exit-signal.py"),
                            "--timeout", "15000"], capture_output=True, timeout=30)
        except Exception:
            pass
        time.sleep(1.5)
        identity()
        if backup is not None:
            open(SETTINGS, "wb").write(backup)
        print(f"收尾：G255={g255()}")


if __name__ == "__main__":
    main()
