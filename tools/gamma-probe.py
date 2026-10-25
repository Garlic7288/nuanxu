"""显卡伽马表只读检查脚本（含一个「写入特征值」模式，用于回归测试）。

用法：
  python gamma-probe.py                  # 读当前 LUT 的几个采样点
  python gamma-probe.py --set 200        # 写入特征 LUT（G=i*200），用于验证程序会不会覆盖它
  python gamma-probe.py --identity       # 复位成线性表（测试完务必执行）

为什么要单独写：SetDeviceGammaRamp 是全局副作用，任何「验证完忘了复位」都会让用户屏幕偏色。
"""

import argparse
import ctypes
from ctypes import wintypes

gdi32 = ctypes.WinDLL("gdi32")
user32 = ctypes.WinDLL("user32")

gdi32.CreateDCW.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.LPCWSTR, ctypes.c_void_p]
gdi32.CreateDCW.restype = wintypes.HDC
gdi32.DeleteDC.argtypes = [wintypes.HDC]
gdi32.SetDeviceGammaRamp.argtypes = [wintypes.HDC, ctypes.c_void_p]
gdi32.GetDeviceGammaRamp.argtypes = [wintypes.HDC, ctypes.c_void_p]
user32.GetDC.argtypes = [wintypes.HWND]
user32.GetDC.restype = wintypes.HDC
user32.ReleaseDC.argtypes = [wintypes.HWND, wintypes.HDC]

POINTS = (0, 32, 128, 200, 255)


def open_dc(device=None):
    """device=None 时用 GetDC(0)（主屏），否则用 CreateDC 拿指定显示器。"""
    if device is None:
        return user32.GetDC(None), "release"
    hdc = gdi32.CreateDCW("DISPLAY", device, None, None)
    return hdc, "delete"


def close_dc(hdc, kind):
    if kind == "release":
        user32.ReleaseDC(None, hdc)
    else:
        gdi32.DeleteDC(hdc)


def read(device=None):
    hdc, kind = open_dc(device)
    if not hdc:
        raise SystemExit("拿不到显示器 DC")
    try:
        buf = (ctypes.c_ushort * 768)()
        if not gdi32.GetDeviceGammaRamp(hdc, ctypes.byref(buf)):
            raise SystemExit("GetDeviceGammaRamp 失败")
        return [int(x) for x in buf]
    finally:
        close_dc(hdc, kind)


def write(ramp, device=None):
    hdc, kind = open_dc(device)
    if not hdc:
        raise SystemExit("拿不到显示器 DC")
    try:
        buf = (ctypes.c_ushort * 768)(*ramp)
        return bool(gdi32.SetDeviceGammaRamp(hdc, ctypes.byref(buf)))
    finally:
        close_dc(hdc, kind)


def show(ramp):
    for ch, name in ((0, "R"), (256, "G"), (512, "B")):
        vals = " ".join(f"{i}:{ramp[ch + i]:5d}" for i in POINTS)
        print(f"  {name}  {vals}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--device", default=None, help=r"如 \\.\DISPLAY1；不传则用主屏 GetDC(0)")
    ap.add_argument("--set", type=int, default=None, help="写入特征 LUT：所有通道 = i*N")
    ap.add_argument("--identity", action="store_true", help="复位成线性表")
    args = ap.parse_args()

    if args.identity:
        ramp = []
        for _ in range(3):
            ramp += [i * 257 for i in range(256)]
        print("复位线性表:", write(ramp, args.device))
        show(read(args.device))
        return

    if args.set is not None:
        n = args.set
        ramp = []
        for _ in range(3):
            ramp += [i * n for i in range(256)]
        print(f"写入特征 LUT (i*{n}):", write(ramp, args.device))
        show(read(args.device))
        return

    print("当前 LUT:")
    show(read(args.device))


if __name__ == "__main__":
    main()
