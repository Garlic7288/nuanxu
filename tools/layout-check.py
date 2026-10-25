"""量「暖序」界面在各视口下是否垂直溢出。

为什么要单独写一个：这次的问题是「底部被截断、要拉滚动条」，而 index.html 有 6 段
互相覆盖的高度媒体查询，靠肉眼推演一定会算错。这里直接用 headless Edge 把页面渲染出来，
读 .shell 的 scrollHeight 与 clientHeight 的差值——差值 > 0 就是溢出。

两个关键前提（都踩过坑）：
  1. 必须等天气卡拿到 4 天数据再量。首帧占位只有 112px，4 天数据态是 222px，
     差 110px。测占位态会假性显示「不溢出」。
     天气是真实网络请求，headless 里不一定回来，所以这里在测量前把天气行补足到 4 行，
     模拟最坏情况。
  2. 不能带 mock 桥接。有桥接的页面 scrollHeight 会多出约 100px 的假数据。

用法：python layout-check.py
"""

import os
import re
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

EDGE_CANDIDATES = [
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
]

# 窗口外框尺寸（headless 下视口 = 窗口高 - 95 左右，脚本会读回真实值，不靠假设）
WINDOWS = [
    # 内容自适应的默认尺寸：客户区 1280×920 CSS（Program.cs 的 ContentWidthCss/ContentHeightCss）。
    # headless 的 window-size 与真实视口差 (30, 95)，所以传 1310×1015。
    (1310, 1015, "内容自适应 1280×920"),
    (1324, 1154, "最大化 1324×1059"),
    (1324, 1178, "最大化偏矮 1324×1083"),
    # 这一条是 1.8.2 实测出来的真实默认尺寸：
    # settings.json 存的 1400×1000 是物理像素，150% 缩放下 WebView2 客户区只有
    # 1378×916 物理 = 919×611 CSS（再扣掉 28px 菜单栏）。
    # 以前这里写的是 (1018, 702) → 视口 988×607，宽度虚高 69px，
    # 窄 69px 会让右列卡片换行变高，于是「测着不溢出、实际溢出」。
    (949, 706, "默认 1400×1000 物理 → 919×611 CSS"),
    (1294, 749, "最大化 1920×1080@150% → 1264×654"),
    (1294, 796, "边界 1264×701（中间档下沿）"),
    (1294, 856, "边界 1264×761（760 档上沿）"),
    (1294, 995, "边界 1264×900（900 档上沿）"),
    (1495, 1005, "中等 1400×910"),
    (1324, 1055, "紧凑 1324×960"),
]

MEASURE = """<script>
(function () {
  function ensureWeatherRows() {
    // 天气没回来时只有占位行，高度比 4 天数据态矮 110px，会假性显示不溢出。
    // 这里把行数补到 4 行，测最坏情况。
    var list = document.getElementById('weatherList');
    if (!list) return 0;
    var rows = list.querySelectorAll('.wrow');
    if (rows.length === 0) return 0;
    while (list.querySelectorAll('.wrow').length < 4) {
      list.appendChild(rows[rows.length - 1].cloneNode(true));
    }
    return list.querySelectorAll('.wrow').length;
  }
  function measure() {
    var rows = ensureWeatherRows();
    // 判据：内容真实底边 vs 视口高。
    //
    // 为什么不是 .shell.scrollHeight：.shell 高度随内容走，它的 scrollHeight
    // 永远等于 clientHeight，「溢出 0」恒成立——这个检查曾经整版假通过（1.8.2 才发现）。
    // 为什么不是 documentElement.scrollHeight：矮屏档给 html,body 设了
    // height:100% + overflow:hidden，scrollHeight 会被钳到视口高，同样恒等于 0。
    //
    // 只有直接取各内容块的 getBoundingClientRect().bottom 才是真话。
    var hero = document.querySelector('.hero');
    var right = document.querySelector('.rightcol');
    var foot = document.querySelector('.footer');
    var bottom = 0;
    [hero, right, foot].forEach(function (el) {
      if (!el) return;
      var r = el.getBoundingClientRect();
      if (r.height > 0) bottom = Math.max(bottom, r.bottom);
    });
    var over = Math.round(bottom) - window.innerHeight;
    // 天气行横向裁切：加了「晴/多云」这类文字标签后，最怕温度被挤出格子。
    // 纵向溢出测不出来，必须单独看每行的 scrollWidth。
    var labels = document.querySelectorAll('.wrow .wlabel').length;
    var clipped = 0;
    var rowsAll = document.querySelectorAll('.wrow');
    for (var i = 0; i < rowsAll.length; i++) {
      if (rowsAll[i].scrollWidth > rowsAll[i].clientWidth + 1) clipped++;
    }
    // 暖度胶囊：文案由「暖光强度 33%」改成「当前暖度 33%」，长度接近但仍要防它顶出卡片。
    // 胶囊是 .level-row 里的右对齐元素，右边界超出容器就说明被裁了。
    var pill = document.getElementById('warmValue');
    var levelRow = document.querySelector('.level-row');
    var pillOver = 0;
    if (pill && levelRow) {
      var pr = pill.getBoundingClientRect(), lr = levelRow.getBoundingClientRect();
      pillOver = Math.max(0, Math.round(pr.right - lr.right));
    }
    var parts = [
      'vw=' + window.innerWidth,
      'vh=' + window.innerHeight,
      'bottom=' + Math.round(bottom),
      'over=' + over,
      'wrows=' + rows,
      'wlabel=' + labels,
      'wclip=' + clipped,
      'pillOver=' + pillOver
    ];
    document.title = 'RESULT|' + parts.join('|');
  }
  if (document.readyState === 'complete') setTimeout(measure, 1200);
  else window.addEventListener('load', function () { setTimeout(measure, 1200); });
})();
</script>
"""


def find_edge() -> str:
    for p in EDGE_CANDIDATES:
        if os.path.isfile(p):
            return p
    raise SystemExit("找不到 msedge.exe")


def build(html: str, out_path: str) -> None:
    if "</body></html>" not in html:
        raise SystemExit("index.html 结构变了，找不到 </body></html>")
    patched = html.replace("</body></html>", MEASURE + "</body></html>")
    with open(out_path, "w", encoding="utf-8") as f:
        f.write(patched)


def dump_title(edge: str, page: str, profile: str, w: int, h: int) -> str:
    cmd = [
        edge, "--headless=new", "--disable-gpu", "--no-first-run",
        "--no-default-browser-check", f"--user-data-dir={profile}",
        f"--window-size={w},{h}",
        "--virtual-time-budget=6000", "--dump-dom", page,
    ]
    out = subprocess.run(cmd, capture_output=True, timeout=120).stdout
    m = re.search(rb"<title>(.*?)</title>", out, re.S)
    if not m:
        return "<no title>"
    return m.group(1).decode("utf-8", "replace").strip()


def main() -> int:
    edge = find_edge()
    html = open(os.path.join(ROOT, "index.html"), encoding="utf-8").read()
    tmp = tempfile.mkdtemp(prefix="wxlayout_")
    profile = os.path.join(tmp, "profile")
    page = os.path.join(tmp, "measure.html")
    build(html, page)
    url = "file:///" + page.replace("\\", "/")

    print("=" * 68)
    print("暖序界面垂直溢出检查（over = 页面内容高 - 视口高，>0 即需要拉滚动条）")
    print("=" * 68)

    failed = []
    try:
        for w, h, label in WINDOWS:
            title = dump_title(edge, url, profile, w, h)
            if not title.startswith("RESULT|"):
                print(f"  [ERR ] {label}  标题异常: {title[:60]}")
                failed.append(label)
                continue
            kv = dict(p.split("=", 1) for p in title.split("|")[1:])
            over = int(kv.get("over", "0"))
            wclip = int(kv.get("wclip", "0"))
            pill_over = int(kv.get("pillOver", "0"))
            ok = over <= 0 and wclip == 0 and pill_over == 0
            print(f"  [{'OK  ' if ok else 'BAD '}] {label}"
                  f"  实际视口 {kv['vw']}×{kv['vh']}"
                  f"  内容底边 {kv['bottom']}"
                  f"  溢出 {over}px  天气行 {kv['wrows']}"
                  f"  标签 {kv.get('wlabel', '?')}"
                  f"  横向裁切 {wclip}"
                  f"  胶囊越界 {pill_over}px")
            if over > 0:
                failed.append(f"{label} 溢出 {over}px")
            if wclip:
                failed.append(f"{label} 有 {wclip} 行天气横向裁切")
            if pill_over:
                failed.append(f"{label} 暖度胶囊越界 {pill_over}px")
    finally:
        import shutil
        shutil.rmtree(tmp, ignore_errors=True)

    print("=" * 68)
    if failed:
        print(f"仍有 {len(failed)} 处溢出：")
        for f in failed:
            print("  - " + f)
        return 1
    print("全部视口无溢出")
    return 0


if __name__ == "__main__":
    sys.exit(main())
