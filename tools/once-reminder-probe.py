"""验证 1.8.3 的「仅一次」提醒：弹一次 → 点「休息好了」→ 开关自动关闭且状态变「已完成」。

为什么不能只靠单元测试：这里要验的是「弹窗真的弹出来了」「副标题文案跟模式一致」
「点完之后前端三态文案对不对」，三件事都跨了 WinForms 弹窗、IPC、WebView2 三层，
只有把真实程序跑起来才看得见。

做法：
  1. 备份 settings.json，写一份 RemindersEnabled=true / ReminderMinutes=1 / ReminderRepeat=false
  2. 启动暖序，等 75 秒（1 分钟到点 + 余量）
  3. 用 EnumWindows 找到「休息一下 · 暖序」弹窗，读它的 Static 文字核对副标题
  4. 找到「休息好了」按钮，PostMessage BM_CLICK
  5. 截客户区，读 #remStatus / #reminderToggle 的文案（靠截图人工看 + 读 settings.json 的 RemindersEnabled）
  6. 1.8.4 新增：在「已完成」态下点 seg「循环」，断言 settings.json 的 ReminderRepeat 变 true，
     并截图确认状态栏从「已完成」变成「已暂停」（不能残留"循环 + 已完成"的自相矛盾）
  7. 发退出事件，恢复 settings.json

用法：python once-reminder-probe.py
"""

import ctypes
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
from ctypes import wintypes

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from importlib import util as _util

_spec = _util.spec_from_file_location(
    "screen_shot", os.path.join(os.path.dirname(os.path.abspath(__file__)), "screen-shot.py"))
shot = _util.module_from_spec(_spec)
_spec.loader.exec_module(shot)

u = ctypes.WinDLL("user32", use_last_error=True)
u.FindWindowW.restype = wintypes.HWND
u.FindWindowW.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR]
u.IsWindowEnabled.restype = ctypes.c_bool
u.IsWindowEnabled.argtypes = [wintypes.HWND]

INSTALL_DIR = os.path.join(os.environ["LOCALAPPDATA"], "Programs", "暖序")
# 允许指向别处（比如 dist 里的新构建），默认测已安装的那份
EXE = os.environ.get("WARMSEQUENCE_EXE") or os.path.join(INSTALL_DIR, "暖序.exe")
DATA_DIR = os.path.join(os.environ["LOCALAPPDATA"], "Nuánxù")
SETTINGS = os.path.join(DATA_DIR, "settings.json")
BACKUP = os.path.join(DATA_DIR, "settings.json.probe-bak")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
INDEX = os.path.join(ROOT, "index.html")
EDGE_CANDIDATES = [
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
]

# 用 headless 量「循环」按钮的位置：视口尺寸必须和真实窗口的 CSS 视口一致，
# 否则媒体查询会换档，卡片位置全变。
SEG_MOCK = """<script>
window.chrome = { webview: {
  postMessage: function () {},
  addEventListener: function (t, fn) { window.__onNative = fn; }
}};
</script>
"""
SEG_STATE = """<script>
(function () {
  if (!window.__onNative) { document.title = 'ERR|no-listener'; return; }
  window.__onNative({ data: {
    command: 'native-state',
    enabled: true, followSun: true, remindersEnabled: false, skippedToday: false,
    reminderRepeat: false, onceCompleted: true,
    city: '广州', minutes: 30, text: '抬头看看远处，让眼睛放松一下。',
    warmth: 40, nextReminderSeconds: 0,
    sunrise: '06:26', sunset: '17:55', weather: null
  }});
})();
</script>
"""
SEG_MEASURE = """<script>
(function () {
  var b = document.querySelector('#reminderMode button[data-repeat="1"]');
  var v = '|VIEW|' + innerWidth + 'x' + innerHeight;
  if (!b) { document.title = 'SEG|none' + v; return; }
  var r = b.getBoundingClientRect();
  document.title = 'SEG|' + (r.left + r.width / 2) + ',' + (r.top + r.height / 2) + v;
})();
</script>
"""

BM_CLICK = 0x00F5


def read_settings():
    with open(SETTINGS, encoding="utf-8") as f:
        return json.load(f)


def write_settings(obj):
    with open(SETTINGS, "w", encoding="utf-8") as f:
        json.dump(obj, f, ensure_ascii=False)


def find_dialog():
    return u.FindWindowW(None, "休息一下 · 暖序")


def child_texts(hwnd):
    """返回 [(class, text), ...]，用来核对弹窗里的文案与按钮。"""
    out = []
    u.EnumChildWindows.restype = ctypes.c_bool

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
    def cb(ch, _):
        n = u.GetWindowTextLengthW(ch)
        buf = ctypes.create_unicode_buffer(n + 2)
        u.GetWindowTextW(ch, buf, n + 2)
        cls = ctypes.create_unicode_buffer(256)
        u.GetClassNameW(ch, cls, 256)
        out.append((cls.value, buf.value, ch))
        return True

    u.EnumChildWindows(hwnd, cb, 0)
    return out


def click_button(hwnd, label):
    # WinForms 的按钮类名是 WindowsForms10.Button.app.0.xxxx（不是原生 "Button"），
    # 所以只按类名前缀 + 文字匹配
    for cls, text, ch in child_texts(hwnd):
        if cls.startswith("WindowsForms10.Button") and text.strip() == label:
            u.PostMessageW(ch, BM_CLICK, 0, 0)
            return True
    return False


def find_edge():
    for p in EDGE_CANDIDATES:
        if os.path.isfile(p):
            return p
    return None


def dump_title(edge, url, profile, win_w, win_h):
    cmd = [
        edge, "--headless=new", "--disable-gpu", "--no-first-run",
        "--no-default-browser-check", "--user-data-dir=" + profile,
        "--window-size=%d,%d" % (win_w, win_h),
        "--virtual-time-budget=3000", "--dump-dom", url,
    ]
    out = subprocess.run(cmd, capture_output=True, timeout=90).stdout
    m = re.search(rb"<title>(.*?)</title>", out, re.S)
    return m.group(1).decode("utf-8", "replace").strip() if m else "<no title>"


def measure_seg_css(edge, profile, css_w, css_h, tmpdir):
    """在「与真实窗口同尺寸的 CSS 视口」里量出 seg「循环」按钮的中心（CSS px）。

    为什么要两轮：headless 的 --window-size 不等于视口（有约 30×95 的边框差），
    而卡片位置对高度档位极敏感。第一轮量出真实视口，第二轮把差额补进窗口尺寸。
    """
    html = open(INDEX, encoding="utf-8").read()
    idx = html.rfind("<script>")
    if idx < 0:
        return None, "index.html 里找不到主 <script>"
    page = html[:idx] + SEG_MOCK + html[idx:]
    page = page.replace("</body></html>", SEG_STATE + SEG_MEASURE + "</body></html>")
    path = os.path.join(tmpdir, "seg.html")
    with open(path, "w", encoding="utf-8") as f:
        f.write(page)
    url = "file:///" + path.replace("\\", "/")

    win_w, win_h = round(css_w) + 30, round(css_h) + 95
    title = ""
    for _ in range(3):
        title = dump_title(edge, url, profile, win_w, win_h)
        m = re.search(r"SEG\|([\d.]+),([\d.]+)\|VIEW\|(\d+)x(\d+)", title)
        if not m:
            return None, title
        vw, vh = int(m.group(3)), int(m.group(4))
        if abs(vw - css_w) <= 2 and abs(vh - css_h) <= 2:
            return (float(m.group(1)), float(m.group(2))), title
        win_w += round(css_w) - vw
        win_h += round(css_h) - vh
    return None, title


def click_at(x, y):
    u.SetCursorPos.argtypes = [ctypes.c_int, ctypes.c_int]
    u.SetCursorPos(x, y)
    time.sleep(0.2)
    u.mouse_event(0x0002, 0, 0, 0, 0)  # LEFTDOWN
    time.sleep(0.06)
    u.mouse_event(0x0004, 0, 0, 0, 0)  # LEFTUP


def webview_rect(hwnd):
    """返回 WebView2 页面区域的屏幕坐标与尺寸（物理像素）。

    不能拿 Form 的客户区当视口：客户区顶部还有 32px 的菜单栏（文件/设置/帮助），
    页面从菜单栏下面才开始。差这 32px 就会把点击落到 seg 上方，白白失败一次。
    """
    found = []
    u.EnumChildWindows.restype = ctypes.c_bool

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
    def cb(ch, _):
        cls = ctypes.create_unicode_buffer(256)
        u.GetClassNameW(ch, cls, 256)
        if cls.value.startswith("Chrome_WidgetWin_0"):
            found.append(ch)
        return True

    u.EnumChildWindows(hwnd, cb, 0)
    if not found:
        return None
    r = wintypes.RECT()
    u.GetWindowRect(found[0], ctypes.byref(r))
    return r.left, r.top, r.right - r.left, r.bottom - r.top


def main():
    shot.make_dpi_aware()
    if not os.path.isfile(EXE):
        print("没找到已安装的暖序：", EXE)
        return 2

    had = os.path.isfile(SETTINGS)
    if had:
        shutil.copy2(SETTINGS, BACKUP)

    try:
        base = read_settings() if had else {}
        base.update({
            "RemindersEnabled": True,
            "ReminderMinutes": 1,
            "ReminderRepeat": False,
            "ReminderSound": False,
            "SkipUntil": None,
        })
        write_settings(base)
        print("已写入测试设置：仅一次 / 1 分钟 / 提醒开启")

        proc = shot.launch(EXE)
        print("已启动 pid=%d，等 78 秒让它到点…" % proc.pid)

        dlg = 0
        deadline = time.time() + 78
        while time.time() < deadline:
            time.sleep(2)
            dlg = find_dialog()
            if dlg:
                break

        if not dlg:
            print("FAIL 等不到提醒弹窗（78 秒内没出现）")
            return 1

        time.sleep(0.8)
        texts = child_texts(dlg)
        print("\n弹窗内文字：")
        for cls, text, _ in texts:
            if text.strip():
                print("  %-8s %s" % (cls, text))

        # 副标题必须跟模式一致：单次模式下要说清楚「只提醒这一次」
        restored = [t for _, t, _ in child_texts(dlg)]
        ok_hint = any("只提醒这一次" in t for t in restored)
        print("%s 副标题应为单次文案" % ("PASS" if ok_hint else "FAIL"))

        out_dir = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                               "release", "preview")
        w, h, bgra = shot.grab()
        shot.write_png(os.path.join(out_dir, "183-once-dialog.png"), w, h, bgra)
        print("已截图 -> release/preview/183-once-dialog.png")

        if not click_button(dlg, "休息好了"):
            print("FAIL 没找到「休息好了」按钮")
            return 1
        print("已点击「休息好了」")
        time.sleep(2.5)

        still = find_dialog()
        print("%s 弹窗应已关闭" % ("PASS" if not still else "FAIL"))

        # 单次模式的核心断言：开关必须被自动关掉，且磁盘上的设置也是关的
        s = read_settings()
        print("%s RemindersEnabled 应被自动置 false（实际 %s）"
              % ("PASS" if s.get("RemindersEnabled") is False else "FAIL",
                 s.get("RemindersEnabled")))
        print("%s ReminderRepeat 应仍为 false（实际 %s）"
              % ("PASS" if s.get("ReminderRepeat") is False else "FAIL",
                 s.get("ReminderRepeat")))

        # 再等 3 秒，确认它不会因为 _nextReminder 过期而反复弹
        time.sleep(3)
        print("%s 完成后不应再次弹出" % ("PASS" if not find_dialog() else "FAIL"))

        hwnd = u.FindWindowW(None, "暖序")
        if hwnd:
            orig = wintypes.RECT()
            u.GetWindowRect(hwnd, ctypes.byref(orig))
            u.SetWindowPos(hwnd, 0, 8, 8, 0, 0, 0x0001 | 0x0004 | 0x0010)
            time.sleep(0.6)
            cr = wintypes.RECT()
            u.GetClientRect(hwnd, ctypes.byref(cr))
            pt = wintypes.POINT(0, 0)
            u.ClientToScreen(hwnd, ctypes.byref(pt))
            cw, chh = cr.right - cr.left, cr.bottom - cr.top
            _, _, bgra = shot.grab((pt.x, pt.y, cw, chh))
            shot.write_png(os.path.join(out_dir, "183-once-done.png"), cw, chh, bgra)
            print("已截图 -> release/preview/183-once-done.png")

            # ---------- 1.8.4 新增：在「已完成」态下点 seg「循环」 ----------
            print("\n--- 切模式：在「已完成」态下点「循环」 ---")
            u.GetDpiForWindow.restype = ctypes.c_uint
            dpi = u.GetDpiForWindow(hwnd) or 96
            scale = dpi / 96.0
            page = webview_rect(hwnd)
            if not page:
                print("SKIP 找不到 WebView2 子窗口")
                page = (pt.x, pt.y, cw, chh)
            px, py, pw, ph = page
            print("页面区 %dx%d 物理 @(%d,%d) / DPI %d / 缩放 %.2f"
                  % (pw, ph, px, py, dpi, scale))
            edge = find_edge()
            css_w, css_h = pw / scale, ph / scale
            point, title = (None, "找不到 msedge.exe")
            if edge:
                tmpdir = tempfile.mkdtemp(prefix="wxseg_")
                try:
                    point, title = measure_seg_css(edge, os.path.join(tmpdir, "p"),
                                                  css_w, css_h, tmpdir)
                finally:
                    shutil.rmtree(tmpdir, ignore_errors=True)
            print("  量测结果：%s" % title)
            if point:
                sx, sy = px + point[0] * scale, py + point[1] * scale
                print("  换算屏幕坐标 (%.0f, %.0f)，开始点击" % (sx, sy))
                u.SetForegroundWindow(hwnd)
                time.sleep(0.4)
                click_at(round(sx), round(sy))
                time.sleep(1.8)
                after = read_settings()
                switched = after.get("ReminderRepeat") is True
                print("%s 点「循环」后 ReminderRepeat 应变 true（实际 %s）"
                      % ("PASS" if switched else "FAIL", after.get("ReminderRepeat")))
                _, _, bgra2 = shot.grab((pt.x, pt.y, cw, chh))
                shot.write_png(os.path.join(out_dir, "184-after-switch.png"), cw, chh, bgra2)
                print("已截图 -> release/preview/184-after-switch.png")
            else:
                print("SKIP 量不到 seg 坐标，跳过真机点击（前端已由 firstframe-audit 副本 C 覆盖）")

            u.SetWindowPos(hwnd, 0, orig.left, orig.top, 0, 0, 0x0001 | 0x0004 | 0x0010)

        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.OpenEventW.restype = wintypes.HANDLE
        ev = kernel32.OpenEventW(0x0002, False, "Local\\WarmSequence.Exit")
        if ev:
            kernel32.SetEvent(ev)
            kernel32.CloseHandle(ev)
            for _ in range(60):
                if proc.poll() is not None:
                    break
                time.sleep(0.25)
            print("退出码：", proc.poll())
        return 0
    finally:
        # 恢复用户原设置（含 .bak，AtomicFile 每次写都会留一份）
        if had:
            shutil.copy2(BACKUP, SETTINGS)
            os.remove(BACKUP)
            print("已恢复原 settings.json")
        else:
            for p in (SETTINGS, SETTINGS + ".bak"):
                if os.path.isfile(p):
                    os.remove(p)


if __name__ == "__main__":
    sys.exit(main())
