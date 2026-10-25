"""全新用户默认值探针：移走 settings.json，模拟一台新机器首次启动，检查生成的默认配置。

为什么不能只读代码：默认值是「属性初始值」，但保存路径、反序列化、迁移逻辑都可能把它改掉；
而且这类需求（默认间隔、提醒音开关）用户只会在界面上看，必须拿真实生成的配置文件说话。

做法：
  1. 备份并移走 settings.json（连同 AtomicFile 留下的 .bak）
  2. 启动暖序 → 等 6 秒 → 发退出事件 → 等它优雅退出（退出时会写盘）
  3. 读新生成的 settings.json，逐项断言
  4. 还原用户原配置

用法：python defaults-probe.py
      WARMSEQUENCE_EXE=C:/wxrun/暖序.exe python defaults-probe.py   # 测指定构建
"""

import ctypes
import json
import os
import shutil
import sys
import time
from ctypes import wintypes

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from importlib import util as _util

_spec = _util.spec_from_file_location(
    "screen_shot", os.path.join(os.path.dirname(os.path.abspath(__file__)), "screen-shot.py"))
shot = _util.module_from_spec(_spec)
_spec.loader.exec_module(shot)

EXE = os.environ.get("WARMSEQUENCE_EXE") or os.path.join(
    os.environ["LOCALAPPDATA"], "Programs", "暖序", "暖序.exe")
DATA_DIR = os.path.join(os.environ["LOCALAPPDATA"], "Nuánxù")
SETTINGS = os.path.join(DATA_DIR, "settings.json")
BACKUP = SETTINGS + ".defaults-bak"

# (键, 期望值, 说明)
EXPECT = [
    ("ReminderMinutes", 1, "提醒间隔默认 1 分钟"),
    ("ReminderSound", True, "提醒音默认开启"),
    ("RemindersEnabled", False, "休息提醒默认停用"),
    ("ReminderRepeat", False, "提醒方式默认「仅一次」"),
    ("FollowSun", True, "暖度默认跟随日光"),
    ("WarmthStrength", 65, "固定暖度默认 65%（跟随日光时不再参与计算）"),
    # 窗口尺寸：退出时 SaveWindowState 会把实际尺寸写进 WindowWidth/Height，
    # 所以这里只能断言「不最大化」和「已标记为按内容自适应」这两项语义
    ("StartMaximized", False, "启动不最大化（按界面内容开窗）"),
    ("WindowFitContent", True, "已标记为按内容自适应窗口"),
]


def main():
    shot.make_dpi_aware()
    if not os.path.isfile(EXE):
        print("没找到暖序：", EXE)
        return 2

    had = os.path.isfile(SETTINGS)
    if had:
        shutil.copy2(SETTINGS, BACKUP)

    ok = True
    try:
        for p in (SETTINGS, SETTINGS + ".bak"):
            if os.path.isfile(p):
                os.remove(p)
        print("已移走 settings.json，模拟全新用户")

        proc = shot.launch(EXE)
        print("已启动 pid=%d，等 6 秒让它完成初始化…" % proc.pid)
        time.sleep(6)

        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        # 必须显式声明 argtypes/restype：不声明时 HANDLE 会被截成 int32，
        # 64 位下拿到的句柄是坏的，OpenEventW 看起来「打不开」
        kernel32.OpenEventW.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR]
        kernel32.OpenEventW.restype = wintypes.HANDLE
        kernel32.SetEvent.argtypes = [wintypes.HANDLE]
        kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
        ev = kernel32.OpenEventW(0x0002, False, "Local\\WarmSequence.Exit")
        if not ev:
            print("FAIL 打不开退出事件（进程存活=%s，err=%d）"
                  % (proc.poll() is None, ctypes.get_last_error()))
            return 1
        kernel32.SetEvent(ev)
        kernel32.CloseHandle(ev)
        for _ in range(60):
            if proc.poll() is not None:
                break
            time.sleep(0.25)
        print("退出码：", proc.poll())
        time.sleep(0.5)

        if not os.path.isfile(SETTINGS):
            print("FAIL 退出后没有生成 settings.json")
            return 1

        with open(SETTINGS, encoding="utf-8") as f:
            s = json.load(f)
        print("\n生成的默认配置：")
        for k, v in s.items():
            print("  %-18s %s" % (k, v))

        print("\n--- 断言 ---")
        for key, want, desc in EXPECT:
            got = s.get(key)
            hit = got == want
            print("  [%s] %s（实际 %s）" % ("OK " if hit else "FAIL", desc, got))
            ok = ok and hit

        print("\n结果：" + ("全部通过" if ok else "有失败项"))
        return 0 if ok else 1
    finally:
        for p in (SETTINGS, SETTINGS + ".bak"):
            if os.path.isfile(p):
                os.remove(p)
        if had:
            shutil.copy2(BACKUP, SETTINGS)
            os.remove(BACKUP)
            print("已还原原 settings.json")


if __name__ == "__main__":
    sys.exit(main())
