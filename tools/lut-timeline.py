"""盯住伽马表：启动暖序后每 100ms 采一次，看暖色是「没写」还是「写了又被抹掉」。

用法：python lut-timeline.py [秒数]
"""

import ctypes
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
user32.ReleaseDC.argtypes = [wintypes.HWND, wintypes.HDC]     # 少了这行会 int too long
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


def main():
    secs = float(sys.argv[1]) if len(sys.argv) > 1 else 12.0

    backup = None
    if os.path.exists(SETTINGS):
        backup = open(SETTINGS, "rb").read()
    with open(SETTINGS, "w", encoding="utf-8") as f:
        f.write(TEST)

    identity()
    print(f"启动前 G255={g255()}（应为 65535）")

    p = subprocess.Popen([EXE], cwd=INSTALL_DIR, creationflags=0x00000008 | 0x00000200)
    print(f"已启动 pid={p.pid}，开始采样 {secs}s（暖度 80 时 G255≈60816）")

    t0 = time.monotonic()
    prev = None
    try:
        while time.monotonic() - t0 < secs:
            v = g255()
            if v != prev:
                print(f"  {time.monotonic()-t0:6.2f}s  G255={v}")
                prev = v
            time.sleep(0.1)
    finally:
        # 退出程序并还原配置
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
