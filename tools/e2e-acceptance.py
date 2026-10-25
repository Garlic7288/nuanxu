"""暖序端到端验收（版本号从 csproj 读，不写死）（一次跑完，不跨工具调用）。

为什么必须一次跑完：沙箱在工具调用结束时回收该调用内启动的 GUI 进程（硬杀），
跨调用测出来的「伽马表没复位 / session.lock 残留」是被 kill 的现象，不是产品 bug。

覆盖：
  1. 运行中静默安装（升级/覆盖安装走同一路径）→ 无弹窗、优雅退出、伽马复位
  2. 唤醒事件 → RestoreWindow 正常执行（含新增的 SendState）+ 静态检查守卫代码
  3. 退出协议 + 伽马复位
  4. --diag 回归
  5. 卸载循环：程序目录与缓存清掉、settings.json 保留
  6. 重装 + 收尾复位

关于「点 X 收进托盘」：**本脚本不测**。已实测证明外部 PostMessage(WM_CLOSE) 会被
WinForms 归为 CloseReason.TaskManagerClosing（走真退出分支），与真实点击不等价；
真实点击又要求窗口在前台，会被编辑器遮挡而点到别的窗口上。
这一条只能人工点一次确认。

用法：python e2e-acceptance.py
"""

import ctypes
import hashlib
import os
import subprocess
import sys
import time
from ctypes import wintypes

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DIST = os.path.join(ROOT, "dist")
RELEASE = os.path.join(ROOT, "release")


def app_version() -> str:
    """从 csproj 读版本号。写死版本号会在每次升级后直接找不到安装包（1.8.1 的教训）。"""
    import re
    csproj = os.path.join(ROOT, "WarmSequence", "暖序.csproj")
    with open(csproj, encoding="utf-8") as f:
        m = re.search(r"<Version>\s*([0-9][0-9.]*)\s*</Version>", f.read())
    if not m:
        raise SystemExit("暖序.csproj 里找不到 <Version>")
    return m.group(1)


VERSION = app_version()
SETUP = os.path.join(RELEASE, f"暖序-{VERSION}-安装程序.exe")
if not os.path.isfile(SETUP):
    raise SystemExit(f"找不到安装包 {SETUP}（先跑 build-installer.ps1）")
INSTALL_DIR = os.path.join(os.environ["LOCALAPPDATA"], "Programs", "暖序")
DATA_DIR = os.path.join(os.environ["LOCALAPPDATA"], "Nuánxù")
INSTALLED_EXE = os.path.join(INSTALL_DIR, "暖序.exe")
INSTALLED_DLL = os.path.join(INSTALL_DIR, "暖序.dll")
# 卸载器路径不写死：Inno 编号会递增，且可能残留无 .dat 的孤儿 exe，阶段 5 用 find_uninstaller() 现场解析。
DIST_DLL = os.path.join(DIST, "暖序.dll")
LOG_DIR = r"C:\wxaudit"


def find_uninstaller():
    """找出真正可用的卸载器。

    Inno 的卸载器文件名会递增（unins000 / unins001 / …）。**只有配套 .dat 存在的
    那一个才有效**——缺 dat 的卸载器启动后什么都不做、直接返回 0，
    会让阶段 5 的「已删除」断言全部误报成产品 bug。
    这种孤儿 exe 通常是上一次卸载中途被打断留下的（dat 已被删、exe 还在）。
    """
    if not os.path.isdir(INSTALL_DIR):
        return None
    best = None
    for name in os.listdir(INSTALL_DIR):
        low = name.lower()
        if not (low.startswith("unins") and low.endswith(".exe")):
            continue
        if not os.path.exists(os.path.join(INSTALL_DIR, name[:-4] + ".dat")):
            continue
        if best is None or low > best.lower():
            best = name
    return os.path.join(INSTALL_DIR, best) if best else None

ACTIVATE_EVENT = "Local\\WarmSequence.Activate"
EXIT_EVENT = "Local\\WarmSequence.Exit"
MUTEX = "Local\\WarmSequence.SingleInstance"

DETACHED = 0x00000008
NEW_GROUP = 0x00000200
BREAKAWAY = 0x01000000

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
user32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32")

gdi32.GetDeviceGammaRamp.argtypes = [wintypes.HDC, ctypes.c_void_p]
gdi32.SetDeviceGammaRamp.argtypes = [wintypes.HDC, ctypes.c_void_p]
user32.GetDC.argtypes = [wintypes.HWND]
user32.GetDC.restype = wintypes.HDC
user32.ReleaseDC.argtypes = [wintypes.HWND, wintypes.HDC]
kernel32.OpenEventW.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR]
kernel32.OpenEventW.restype = wintypes.HANDLE
kernel32.OpenMutexW.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR]
kernel32.OpenMutexW.restype = wintypes.HANDLE
kernel32.SetEvent.argtypes = [wintypes.HANDLE]
kernel32.SetEvent.restype = wintypes.BOOL
kernel32.CloseHandle.argtypes = [wintypes.HANDLE]

SYNCHRONIZE = 0x00100000
EVENT_MODIFY_STATE = 0x0002

results = []


def check(name, ok, detail=""):
    results.append((name, ok, detail))
    print(f"  [{'OK ' if ok else 'FAIL'}] {name}" + (f"  {detail}" if detail else ""))


def read_lut():
    hdc = user32.GetDC(None)
    try:
        buf = (ctypes.c_ushort * 768)()
        if not gdi32.GetDeviceGammaRamp(hdc, ctypes.byref(buf)):
            return None
        return [int(x) for x in buf]
    finally:
        user32.ReleaseDC(None, hdc)


def write_identity():
    hdc = user32.GetDC(None)
    try:
        ramp = []
        for _ in range(3):
            ramp += [i * 257 for i in range(256)]
        buf = (ctypes.c_ushort * 768)(*ramp)
        return bool(gdi32.SetDeviceGammaRamp(hdc, ctypes.byref(buf)))
    finally:
        user32.ReleaseDC(None, hdc)


def is_identity(lut):
    return lut is not None and lut[255] == 65535 and lut[511] == 65535 and lut[767] == 65535


def describe(lut):
    return "<读不到>" if lut is None else f"R255={lut[255]} G255={lut[511]} B255={lut[767]}"


def launch(exe, cwd=None):
    flags = DETACHED | NEW_GROUP
    try:
        return subprocess.Popen([exe], cwd=cwd or os.path.dirname(exe),
                                creationflags=flags | BREAKAWAY).pid
    except OSError:
        return subprocess.Popen([exe], cwd=cwd or os.path.dirname(exe),
                                creationflags=flags).pid


def is_running():
    h = kernel32.OpenMutexW(SYNCHRONIZE, False, MUTEX)
    if h:
        kernel32.CloseHandle(h)
        return True
    return False


def signal_event(name):
    h = kernel32.OpenEventW(EVENT_MODIFY_STATE, False, name)
    if not h:
        return False
    ok = kernel32.SetEvent(h)
    kernel32.CloseHandle(h)
    return bool(ok)


def wait_exit(timeout_ms=20000):
    t0 = time.monotonic()
    while (time.monotonic() - t0) * 1000 < timeout_ms:
        if not is_running():
            return int((time.monotonic() - t0) * 1000)
        time.sleep(0.2)
    return None


def md5(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest().upper()


def run_setup(args, log_name):
    os.makedirs(LOG_DIR, exist_ok=True)
    log = os.path.join(LOG_DIR, log_name)
    if os.path.exists(log):
        os.remove(log)
    t0 = time.monotonic()
    rc = subprocess.call([SETUP] + args + [f"/LOG={log}"])
    return rc, int(time.monotonic() - t0), log


def source_contains(method_sig, needle):
    """静态检查：Program.cs 里某个方法体内是否包含某段代码。"""
    src = open(os.path.join(ROOT, "WarmSequence", "Program.cs"), encoding="utf-8").read()
    i = src.find(method_sig)
    if i < 0:
        return False
    brace = src.find("{", i)
    depth = 0
    for j in range(brace, len(src)):
        if src[j] == "{":
            depth += 1
        elif src[j] == "}":
            depth -= 1
            if depth == 0:
                return needle in src[i:j + 1]
    return False


SETTINGS = os.path.join(DATA_DIR, "settings.json")
SETTINGS_BAK = os.path.join(LOG_DIR, "settings.user-backup.json")

# 固定暖度 80%：跟随日光时白天的暖度接近 0，LUT 就是线性表，
# 「LUT 已被写成暖色」这条断言会随运行时刻漂移，必须先把配置钉死
TEST_SETTINGS = (
    '{"Enabled":true,"FollowSun":false,"RemindersEnabled":false,"ReminderMinutes":30,'
    '"ReminderText":"\\u62ac\\u5934\\u770b\\u770b\\u8fdc\\u5904\\uff0c\\u8ba9\\u773c\\u775b\\u653e\\u677e\\u4e00\\u4e0b\\u3002",'
    '"SkipUntil":null,"WarmthStrength":80,'
    '"ReminderSound":false,"StartMaximized":true,"WindowWidth":1400,"WindowHeight":1000,'
    '"City":"\\u5e7f\\u5dde"}'
)


def backup_settings():
    os.makedirs(LOG_DIR, exist_ok=True)
    if os.path.exists(SETTINGS):
        with open(SETTINGS, "rb") as src, open(SETTINGS_BAK, "wb") as dst:
            dst.write(src.read())
        return True
    return False


def apply_test_settings():
    with open(SETTINGS, "w", encoding="utf-8") as f:
        f.write(TEST_SETTINGS)


def restore_settings(had):
    if had and os.path.exists(SETTINGS_BAK):
        with open(SETTINGS_BAK, "rb") as src, open(SETTINGS, "wb") as dst:
            dst.write(src.read())
    elif not had and os.path.exists(SETTINGS):
        os.remove(SETTINGS)


def main():
    new_md5 = md5(DIST_DLL)
    os.makedirs(LOG_DIR, exist_ok=True)

    print("=" * 70)
    print("阶段 0 · 安装完整性前置检查")
    print("=" * 70)
    # Inno 的卸载器是 uninsNNN.exe + uninsNNN.dat 两个文件，dat 存着「装了哪些文件」。
    # 只有 exe 没有 dat 的孤儿（上一次卸载中途被打断就会留下），卸载器启动后
    # 什么都不做、直接返回 0，于是阶段 5 的「安装目录已删除 / 缓存已删除」全部
    # 误报成产品 bug。这里先把它测出来并修复，避免给出错误的失败结论。
    valid = find_uninstaller()
    installed = os.path.isdir(INSTALL_DIR) and os.path.exists(INSTALLED_EXE)
    # 未安装是正常起点（干净机器），只有「装了但卸载器缺 .dat」才算破损
    check("安装状态完整（存在配套 .dat 的卸载器）", valid is not None or not installed,
          os.path.basename(valid) if valid else ("未安装，将首次安装" if not installed else "破损安装"))
    if valid is None:
        rc, secs, _ = run_setup(["/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CURRENTUSER"],
                                "install0-repair.log")
        print(f"  修复安装退出码={rc}  耗时 {secs}s")
        check("覆盖安装已重建卸载数据", find_uninstaller() is not None)

    # 把暖度钉成固定 80%，否则「LUT 应为暖色」会随运行时刻漂移（白天跟随日光时暖度≈0）
    had_settings = backup_settings()
    apply_test_settings()

    print("=" * 70)
    print("阶段 1 · 运行中静默安装（升级与覆盖安装走同一路径）")
    print("=" * 70)
    before = md5(INSTALLED_DLL) if os.path.exists(INSTALLED_DLL) else "<未安装>"
    print(f"  安装前 dll md5: {before[:16]}")
    print(f"  dist  dll md5: {new_md5[:16]}")

    pid = launch(INSTALLED_EXE)
    print(f"  已启动 pid={pid}")
    time.sleep(8)
    lut = read_lut()
    check("程序在运行", is_running())
    check("LUT 已被写成暖色", lut is not None and lut[511] < 65535, describe(lut))

    rc, secs, log = run_setup(["/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CURRENTUSER"],
                              "install1.log")
    print(f"  安装器退出码={rc}  耗时 {secs}s")
    check("安装器退出码 = 0（无弹窗、未取消）", rc == 0, f"实际 {rc}")
    check("运行中的程序已被优雅关闭", not is_running())

    time.sleep(1)
    lut = read_lut()
    check("LUT 已复位成线性表", is_identity(lut), describe(lut))
    check("session.lock 已清理", not os.path.exists(os.path.join(DATA_DIR, "session.lock")))
    check(f"安装目录 dll = dist 的 {VERSION}", md5(INSTALLED_DLL) == new_md5)

    print()
    print("=" * 70)
    print("阶段 2 · 唤醒路径（RestoreWindow 新增了 SendState）")
    print("=" * 70)
    pid = launch(INSTALLED_EXE)
    print(f"  已启动 pid={pid}")
    time.sleep(8)
    check("程序在运行", is_running())
    ok = signal_event(ACTIVATE_EVENT)
    check("能发出唤醒事件", ok)
    time.sleep(2.5)
    check("唤醒后进程仍正常（RestoreWindow 没抛异常）", is_running())

    check("静态检查：RestoreWindow 内含 SendState()",
          source_contains("private void RestoreWindow()", "SendState()"))
    check("静态检查：UpdateColor 的推送受 Visible 守卫",
          source_contains("private void UpdateColor(bool force = false)", "if (Visible) SendState();"))
    check("静态检查：隐藏态仍有 30 秒兜底推送",
          source_contains("private void MaybeSendCountdown(DateTime now)", "TotalSeconds >= 30"))
    print("  说明：「点 X 收进托盘」无法自动化——外部 WM_CLOSE 会被判为")
    print("        TaskManagerClosing（走真退出），与真实点击不等价。需人工点一次确认。")

    print()
    print("=" * 70)
    print("阶段 3 · 退出协议 + 伽马复位")
    print("=" * 70)
    print(f"  退出前 LUT: {describe(read_lut())}")
    check("能发出退出事件", signal_event(EXIT_EVENT))
    ms = wait_exit()
    check("已优雅退出", ms is not None, f"{ms} ms" if ms is not None else "超时")
    time.sleep(0.5)
    lut = read_lut()
    check("退出后 LUT 已复位", is_identity(lut), describe(lut))
    check("session.lock 已清理", not os.path.exists(os.path.join(DATA_DIR, "session.lock")))

    print()
    print("=" * 70)
    print("阶段 4 · --diag 回归")
    print("=" * 70)
    diag = os.path.join(DATA_DIR, "diag.txt")
    if os.path.exists(diag):
        os.remove(diag)
    r = subprocess.run([INSTALLED_EXE, "--diag"], cwd=INSTALL_DIR, capture_output=True, timeout=180)
    print(f"  --diag 退出码={r.returncode}")
    if os.path.exists(diag):
        text = open(diag, encoding="utf-8", errors="replace").read()
        for line in text.splitlines():
            s = line.strip()
            if s.startswith(("[enum]", "[cross]", "[apply]", "[restore]")):
                print(f"    {s}")
        check("枚举到 1 块显示器", "count=1" in text)
        check("回读校验通过（verified=True）", "verified=True" in text)
        check("CreateDC 与 GetDC(0) 命中同一硬件 LUT（diff=0）", "diff=0" in text)
        check("伪造 DISPLAY9 写入被正确拒绝", "[fail-path] 伪造 DISPLAY9 写入=False" in text)
        check("结束时还原成功（restore ok=True）", "[restore] ok=True" in text)
        check("还原回差为 0（restoreDiff=0）", "restoreDiff=0" in text)
    else:
        check("diag.txt 已生成", False, "文件不存在")

    print()
    print("=" * 70)
    print("阶段 5 · 卸载循环（运行中卸载）")
    print("=" * 70)
    pid = launch(INSTALLED_EXE)
    print(f"  启动 pid={pid}")
    time.sleep(8)
    check("卸载前进程在运行", is_running())

    ulog = os.path.join(LOG_DIR, "uninstall.log")
    if os.path.exists(ulog):
        os.remove(ulog)
    unins = find_uninstaller()
    check("找到有效卸载器（配套 .dat 存在）", unins is not None,
          os.path.basename(unins) if unins else "无")
    r = subprocess.run([unins, "/SILENT", "/SUPPRESSMSGBOXES", f"/LOG={ulog}"],
                       capture_output=True, timeout=300)
    print(f"  卸载器退出码={r.returncode}")
    # Inno 的卸载器是自解压壳：主进程返回 0 只代表「已把卸载任务派出去」，
    # 真正的删除在 %TEMP% 下的子进程里继续跑。而 usPostUninstall 里还有 Sleep(3000)
    # 等 WebView2 子进程退出，这个时间差更大。不轮询就会在删除完成前断言，全部误报。
    t_wait = time.monotonic()
    while time.monotonic() - t_wait < 90:
        if not os.path.exists(INSTALL_DIR) and not os.path.exists(os.path.join(DATA_DIR, "WebView2")):
            break
        time.sleep(0.3)
    print(f"  等待卸载收尾 {time.monotonic() - t_wait:.1f}s")
    if os.path.exists(ulog):
        tail = [l for l in open(ulog, encoding="utf-8", errors="replace").read().splitlines()
                if any(k in l for k in ("Error", "error", "Deleting", "Unable", "Cancel"))][-6:]
        for l in tail:
            print(f"    | {l.strip()}")
    check("卸载器退出码 = 0", r.returncode == 0, f"实际 {r.returncode}")
    check("运行中的程序已被关闭", not is_running())
    check("安装目录已删除", not os.path.exists(INSTALL_DIR))
    check("WebView2 缓存已删除", not os.path.exists(os.path.join(DATA_DIR, "WebView2")))
    check("gamma-original.json 已删除", not os.path.exists(os.path.join(DATA_DIR, "gamma-original.json")))
    check("gamma-original.json.bak 已删除", not os.path.exists(os.path.join(DATA_DIR, "gamma-original.json.bak")))
    check("session.lock 已删除", not os.path.exists(os.path.join(DATA_DIR, "session.lock")))
    check("diag.txt 已删除", not os.path.exists(diag))
    check("settings.json 保留", os.path.exists(os.path.join(DATA_DIR, "settings.json")))

    print()
    print("=" * 70)
    print("阶段 6 · 重装 + 收尾")
    print("=" * 70)
    rc, secs, log = run_setup(["/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CURRENTUSER"],
                              "install2.log")
    print(f"  重装退出码={rc}  耗时 {secs}s")
    check("重装成功", rc == 0, f"实际 {rc}")
    check("安装目录已恢复", os.path.exists(INSTALLED_EXE))
    if os.path.exists(INSTALLED_DLL):
        check(f"重装后 dll = {VERSION}", md5(INSTALLED_DLL) == new_md5)
    check("settings.json 仍在", os.path.exists(os.path.join(DATA_DIR, "settings.json")))


if __name__ == "__main__":
    had = os.path.exists(SETTINGS)
    try:
        main()
    finally:
        write_identity()
        restore_settings(had)
        print()
        print(f"收尾：LUT -> {describe(read_lut())}   残留进程: {'有' if is_running() else '无'}")
        print(f"      用户配置已恢复: {os.path.exists(SETTINGS)}")
        total = len(results)
        failed = [n for n, ok, _ in results if not ok]
        print("=" * 70)
        print(f"合计 {total} 项，通过 {total - len(failed)}，失败 {len(failed)}")
        for n in failed:
            print(f"  FAIL: {n}")
        print("=" * 70)
        sys.exit(1 if failed else 0)
