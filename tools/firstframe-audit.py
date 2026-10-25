"""首帧占位审计：确认「还没收到 native-state」时界面显示占位符，而不是假的日出日落。

做法：
  1. 造两份 index.html 副本，都在主脚本之前注入一个假的 window.chrome.webview
     （headless Edge 里没有真桥接，不注入就会走「浏览器预览」分支，测不到桌面路径）
  2. 副本 A：什么都不推 → 应该全是 --:--
  3. 副本 B：在解析末尾同步推一条 native-state → 应该全是真值
  4. 副本 C：推「单次已完成 + 模式已切到循环」的载荷（1.8.4 修复后的真实形状）
     → 必须显示「已暂停 / 开启」，不能残留「已完成 / 再来一次」
  5. 副本 D：固定暖度模式（followSun:false）→ 胶囊写「固定暖度」、滑块必须可拖
     （这是唯一「滑块应该能拖」的路径，锁定逻辑写反了全靠它抓）
  6. 末尾追加测量脚本，把结果写进 document.title，再用 --dump-dom 读回来

为什么要走 document.title：`--dump-dom` 下 setTimeout 常来不及触发，
但在解析末尾同步执行的代码一定拿得到结果。

用法：python firstframe-audit.py
"""

import os
import re
import shutil
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EDGE_CANDIDATES = [
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
]

MOCK_BRIDGE = """<script>
// 假桥接：只为让 index.html 走「有 C# 桥」的分支
window.__msgs = [];
window.chrome = { webview: {
  postMessage: function (m) { window.__msgs.push(m); },
  addEventListener: function (t, fn) { window.__onNative = fn; }
}};
</script>
"""

MEASURE = """<script>
(function () {
  function txt(sel, i) {
    var n = document.querySelectorAll(sel);
    if (!n || n.length <= i) return '<missing>';
    return n[i].textContent.trim();
  }
  var parts = [];
  parts.push('clock=' + txt('#clock', 0));
  parts.push('phase=' + txt('#phaseNow', 0));
  for (var i = 0; i < 4; i++) parts.push('tl' + i + '=' + txt('.phase time', i));
  parts.push('rise=' + txt('.schedule-row time', 0));
  parts.push('set=' + txt('.schedule-row time', 1));
  parts.push('warmVal=' + txt('#warmValue', 0));
  parts.push('warmLbl=' + txt('#warmLabel', 0));
  // 滑块可交互性：跟随日光/护眼关闭时必须是 disabled，否则用户会以为还能手动调暖度
  parts.push('sliderDis=' + (document.getElementById('warmSlider').disabled ? 1 : 0));
  parts.push('city=' + txt('#cityLabel', 0));
  // 提醒卡片：默认停用时首帧不能闪出「已开启」
  parts.push('remStatus=' + txt('#remStatus', 0));
  parts.push('remToggle=' + txt('#reminderToggle', 0));
  // 提醒间隔默认值：v1.0.0 起是 1 分钟，占位也不能写死成 30
  parts.push('interval=' + txt('#interval', 0));
  // 倒计时区：首帧不应出现 83:00 这类异常值
  parts.push('cd=' + txt('#reminderCountdown', 0));
  parts.push('msgPreview=' + txt('#messagePreview', 0));
  // 模式切换：默认必须是「仅一次」高亮，不能两个都不亮
  var segOn = document.querySelectorAll('#reminderMode button.on');
  parts.push('modeOn=' + (segOn.length === 1 ? segOn[0].textContent.trim() : '<' + segOn.length + '>'));
  document.title = 'RESULT|' + parts.join('|');
})();
</script>
"""

# 一条足够有辨识度的 state：日出 04:33 / 日落 21:07，城市「乌鲁木齐」
STATE_JS = """<script>
(function () {
  if (!window.__onNative) { document.title = 'RESULT|ERROR|no-listener'; return; }
  window.__onNative({ data: {
    command: 'native-state',
    enabled: true, followSun: true, remindersEnabled: false, skippedToday: false,
    reminderRepeat: false, onceCompleted: false,
    city: '乌鲁木齐', minutes: 45, text: '抬头看看远处，让眼睛放松一下。',
    warmth: 33, nextReminderSeconds: 754,
    sunrise: '04:33', sunset: '21:07',
    weather: null
  }});
})();
</script>
"""


# 副本 C：模拟「单次弹完 → 用户把模式切到循环」之后 C# 会发出的真实载荷。
# 1.8.4 起 reminder-mode 分支切到循环时会清掉 _onceCompleted，
# 所以这里 onceCompleted 必须是 false、reminderRepeat 必须是 true。
STATE_JS_C = """<script>
(function () {
  if (!window.__onNative) { document.title = 'RESULT|ERROR|no-listener'; return; }
  window.__onNative({ data: {
    command: 'native-state',
    enabled: true, followSun: true, remindersEnabled: false, skippedToday: false,
    reminderRepeat: true, onceCompleted: false,
    city: '乌鲁木齐', minutes: 45, text: '抬头看看远处，让眼睛放松一下。',
    warmth: 33, nextReminderSeconds: 754,
    sunrise: '04:33', sunset: '21:07',
    weather: null
  }});
})();
</script>
"""


# 副本 D：固定暖度模式（跟随日光关闭）。这是唯一「滑块应该可拖」的路径，
# 不覆盖它的话，锁定逻辑写错了也不会被发现。
STATE_JS_D = """<script>
(function () {
  if (!window.__onNative) { document.title = 'RESULT|ERROR|no-listener'; return; }
  window.__onNative({ data: {
    command: 'native-state',
    enabled: true, followSun: false, remindersEnabled: false, skippedToday: false,
    reminderRepeat: false, onceCompleted: false,
    city: '乌鲁木齐', minutes: 45, text: '抬头看看远处，让眼睛放松一下。',
    warmth: 33, nextReminderSeconds: 754,
    sunrise: '04:33', sunset: '21:07',
    weather: null
  }});
})();
</script>
"""


def find_edge() -> str:
    for p in EDGE_CANDIDATES:
        if os.path.isfile(p):
            return p
    raise SystemExit("找不到 msedge.exe")


def build(html: str, state_js: str, out_path: str) -> None:
    idx = html.rfind("<script>")
    if idx < 0:
        raise SystemExit("index.html 里找不到主 <script> 块")
    patched = html[:idx] + MOCK_BRIDGE + html[idx:]
    patched = patched.replace("</body></html>", state_js + MEASURE + "</body></html>")
    with open(out_path, "w", encoding="utf-8") as f:
        f.write(patched)


def dump_title(edge: str, page: str, profile: str) -> str:
    cmd = [
        edge, "--headless=new", "--disable-gpu", "--no-first-run",
        "--no-default-browser-check", f"--user-data-dir={profile}",
        "--virtual-time-budget=3000", "--dump-dom", page,
    ]
    out = subprocess.run(cmd, capture_output=True, timeout=90).stdout
    m = re.search(rb"<title>(.*?)</title>", out, re.S)
    if not m:
        return "<no title found>"
    return m.group(1).decode("utf-8", "replace").strip()


def main() -> int:
    edge = find_edge()
    html = open(os.path.join(ROOT, "index.html"), encoding="utf-8").read()
    tmp = tempfile.mkdtemp(prefix="wxfirstframe_")
    profile = os.path.join(tmp, "profile")
    ok = True

    try:
        a = os.path.join(tmp, "a.html")
        build(html, "", a)
        title_a = dump_title(edge, "file:///" + a.replace("\\", "/"), profile)
        print("--- 副本 A：未收到 native-state ---")
        print("  " + title_a)

        b = os.path.join(tmp, "b.html")
        build(html, STATE_JS, b)
        title_b = dump_title(edge, "file:///" + b.replace("\\", "/"), profile)
        print("--- 副本 B：已收到 native-state ---")
        print("  " + title_b)

        c = os.path.join(tmp, "c.html")
        build(html, STATE_JS_C, c)
        title_c = dump_title(edge, "file:///" + c.replace("\\", "/"), profile)
        print("--- 副本 C：单次完成后再切到循环 ---")
        print("  " + title_c)

        d = os.path.join(tmp, "d.html")
        build(html, STATE_JS_D, d)
        title_d = dump_title(edge, "file:///" + d.replace("\\", "/"), profile)
        print("--- 副本 D：固定暖度模式（滑块可拖） ---")
        print("  " + title_d)

        # 断言 A：设置相关的全部占位；时钟例外——它不依赖任何设置，直接显示真实时间
        checks_a = [
            ("phase=同步中", "phaseNow 应为「同步中」"),
            ("tl0=--:-- — --:--", "时间轴第 1 段应为占位"),
            ("tl1=--:-- — --:--", "时间轴第 2 段应为占位"),
            ("tl2=--:-- — --:--", "时间轴第 3 段应为占位"),
            ("tl3=--:-- — --:--", "时间轴第 4 段应为占位"),
            ("rise=--:--", "日出应为占位"),
            ("set=--:--", "日落应为占位"),
            ("warmVal=暖度 —", "暖度文案应为占位（模式未知，不写「当前/固定」）"),
            ("warmLbl=—", "暖度标签应为占位"),
            ("sliderDis=1", "首帧未同步时滑块应禁用（默认 value=65 不能可拖）"),
            ("remStatus=—", "提醒状态应为占位（不能闪「已开启」）"),
            ("remToggle=开启", "提醒按钮应为「开启」（默认停用）"),
            ("interval=1", "提醒间隔占位应为 1 分钟（默认值 1）"),
        ]
        # 断言 B：全部真值。时间轴的边界由 sunrise/sunset 推出，与当前时刻无关，可以硬断言
        checks_b = [
            ("rise=04:33", "日出应为 04:33"),
            ("set=21:07", "日落应为 21:07"),
            ("tl0=03:33 — 06:33", "时间轴第 1 段 = 日出-60 → 日出+120"),
            ("tl3=22:07 — 03:33", "时间轴第 4 段 = 日落+60 → 日出-60（跨午夜）"),
            ("warmVal=当前暖度 33%", "日光模式胶囊应写「当前暖度」，不是「暖光强度」"),
            ("warmLbl=轻柔 · 适合办公", "暖度标签应给场景说法（33% 落在 16-35 档）"),
            ("sliderDis=1", "跟随日光下滑块必须锁定"),
            ("city=乌鲁木齐 · 中国标准时间", "城市应已更新"),
            ("remStatus=已暂停", "提醒默认停用时应显示「已暂停」"),
            ("remToggle=开启", "提醒默认停用时按钮应为「开启」"),
            ("msgPreview=抬头看看远处，让眼睛放松一下。", "提醒文案应显示用户设置的真实文案"),
            ("modeOn=仅一次", "提醒方式默认应为「仅一次」，且只有一个按钮高亮"),
        ]
        # 断言 C：切到循环之后不能再残留「已完成」——循环模式没有"完成"这个概念
        checks_c = [
            ("remStatus=已暂停", "切到循环后状态应为「已暂停」，不能还是「已完成」"),
            ("remToggle=开启", "切到循环后按钮应为「开启」，不能还是「再来一次」"),
            ("modeOn=循环", "模式高亮应移到「循环」，且只有一个按钮高亮"),
        ]
        # 断言 D：固定暖度模式——唯一「滑块可拖」的路径，必须单独覆盖
        checks_d = [
            ("warmVal=固定暖度 33%", "固定模式胶囊应写「固定暖度」"),
            ("warmLbl=轻柔 · 适合办公", "固定模式也要给场景说法，不能只说「全天一致」"),
            ("sliderDis=0", "固定模式下滑块必须可拖"),
        ]

        print("\n--- 断言 ---")
        for needle, desc in checks_a:
            hit = needle in title_a
            print(f"  [{'OK ' if hit else 'FAIL'}] {desc}")
            ok = ok and hit
        for needle, desc in checks_b:
            hit = needle in title_b
            print(f"  [{'OK ' if hit else 'FAIL'}] {desc}")
            ok = ok and hit
        for needle, desc in checks_c:
            hit = needle in title_c
            print(f"  [{'OK ' if hit else 'FAIL'}] {desc}")
            ok = ok and hit
        for needle, desc in checks_d:
            hit = needle in title_d
            print(f"  [{'OK ' if hit else 'FAIL'}] {desc}")
            ok = ok and hit

        # 时钟：首帧就应该是真实 HH:MM，不能停在 --:--
        m = re.search(r"clock=(\d{2}:\d{2})", title_a)
        print(f"  [{'OK ' if m else 'FAIL'}] 时钟首帧即真实时间（拿到 {m.group(1) if m else title_a}）")
        ok = ok and bool(m)

        # 阶段：收到 state 后必须不再是「同步中」，且落在四个阶段之一
        PHASES = ["晨光唤醒", "清透日间", "日落过渡", "暖夜模式"]
        mp = re.search(r"phase=([^|]+)", title_b)
        phase_text = mp.group(1) if mp else ""
        good = ("同步中" not in phase_text) and any(p in phase_text for p in PHASES)
        print(f"  [{'OK ' if good else 'FAIL'}] 阶段已按真实日出日落判定（{phase_text}）")
        ok = ok and good

        print("\n结果：" + ("全部通过" if ok else "有失败项"))
        return 0 if ok else 1
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
