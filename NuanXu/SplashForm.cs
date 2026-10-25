using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace NuanXu;

/// <summary>
/// 启动画面：无边框、覆盖在主窗口客户区上，只画一枚 LOGO。
/// 背景色与主界面、index.html 的 body 同为 #f4f4f0，切换时看不出接缝。
/// 显示满 1 秒且界面渲染完成后淡出，随后关闭自己。
/// </summary>
internal sealed class SplashForm : Form
{
    // 与 index.html 的 --paper / --ink / --muted / --green 保持一致，切换时才不会跳色
    private static readonly Color Paper = Color.FromArgb(244, 244, 240);
    private static readonly Color Ink = Color.FromArgb(32, 34, 31);
    private static readonly Color Muted = Color.FromArgb(133, 135, 127);
    private static readonly Color Green = Color.FromArgb(87, 116, 94);
    // 品牌双色：夕阳橙的月牙 + 墨绿的星。启动画面底色是浅 Paper，
    // 所以星必须用深色（与 app.ico 的深底米白星正好相反）。
    private static readonly Color Moon = Color.FromArgb(233, 160, 92);
    private static readonly Color Spark = Color.FromArgb(47, 74, 58);
    private static readonly Color Track = Color.FromArgb(228, 229, 222);

    private const int FadeInMs = 180;
    private const int FadeOutMs = 160;
    // 用户要求「先看 1 秒 LOGO」：淡出也算在这 1 秒里，所以起点提前 FadeOutMs
    private const int MinVisibleMs = 1000 - FadeOutMs;
    // 兜底：WebView2 迟迟不就绪时不能一直卡在启动画面
    private const int MaxVisibleMs = 4500;
    // 进度条走完的时间点，与开始淡出对齐，视觉上「走满 = 可以进场」
    private const int ProgressMs = MinVisibleMs;

    private readonly Stopwatch _clock = new();
    private readonly System.Windows.Forms.Timer _ticker = new() { Interval = 16 };
    private bool _contentReady;
    private bool _closing;
    private long _closeAt;
    private float _progress;

    public SplashForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Paper;
        DoubleBuffered = true;
        // 无子控件，纯自绘，所以 Opacity 动画安全（有子 HWND 时分层窗口不会一并变透明）
        Opacity = 0;
        _ticker.Tick += OnTick;
    }

    /// <summary>覆盖在 owner 的客户区上显示，随 owner 一起居中。</summary>
    public void ShowOver(Form owner)
    {
        Owner = owner;
        Bounds = owner.RectangleToScreen(owner.ClientRectangle);
        TopMost = true;
        Show();
        Begin();
    }

    /// <summary>启动计时与动画（预览工具也走这里，保证与真实启动同一套时序）。</summary>
    internal void Begin()
    {
        _clock.Restart();
        _ticker.Start();
    }

    /// <summary>界面已渲染完成，可以准备退场（仍会保证至少显示满 1 秒）。</summary>
    public void MarkContentReady() => _contentReady = true;

    /// <summary>立即退场，用于启动失败等异常路径。</summary>
    public void ForceClose()
    {
        _ticker.Stop();
        if (!IsDisposed) Close();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var ms = _clock.ElapsedMilliseconds;

        if (!_closing)
        {
            Opacity = Math.Min(1.0, ms / (double)FadeInMs);
            _progress = Math.Clamp(ms / (float)ProgressMs, 0f, 1f);
            Invalidate();

            // 满 1 秒且界面就绪才走；界面慢就等，但最多等到 MaxVisibleMs
            if ((ms >= MinVisibleMs && _contentReady) || ms >= MaxVisibleMs) BeginClose(ms);
            return;
        }

        var k = (ms - _closeAt) / (double)FadeOutMs;
        Opacity = Math.Max(0, 1 - k);
        if (k < 1) return;

        _ticker.Stop();
        Close();
    }

    private void BeginClose(long ms)
    {
        if (_closing) return;
        _closing = true;
        _closeAt = ms;
        _progress = 1f;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using (var back = new SolidBrush(Paper)) g.FillRectangle(back, ClientRectangle);

        // 全部按 96 DPI 设计，再乘当前缩放比（与本项目其它界面一致）
        var s = DeviceDpi / 96f;
        var cx = ClientSize.Width / 2f;

        // 月牙在 64 原稿里只占中间 42，排版按「可见直径」算，否则会凭空多出一圈空白
        var markVisual = 104 * s;
        var boxSize = markVisual * 64f / 42f;
        var wordSize = 32 * s;
        var tagSize = 11.5f * s;
        var gapMark = 20 * s;
        var gapTag = 14 * s;
        var gapBar = 48 * s;
        var barW = 160 * s;
        var barH = 2 * s;

        var wordH = wordSize * 1.18f;
        var tagH = tagSize * 1.5f;
        var groupH = markVisual + gapMark + wordH + gapTag + tagH + gapBar + barH;
        var y = (ClientSize.Height - groupH) / 2f;

        var boxTop = y - (boxSize - markVisual) / 2f;
        DrawMark(g, new RectangleF(cx - boxSize / 2f, boxTop, boxSize, boxSize));
        y += markVisual + gapMark;

        using (var wordFont = new Font("Microsoft YaHei UI", wordSize, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var ink = new SolidBrush(Ink))
        {
            DrawTracked(g, "暖序", wordFont, ink, cx, y, wordSize * 0.14f, wordH);
        }
        y += wordH + gapTag;

        using (var tagFont = new Font("Microsoft YaHei UI", tagSize, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var muted = new SolidBrush(Muted))
        {
            DrawTracked(g, "把昼夜，还给眼睛", tagFont, muted, cx, y, tagSize * 0.2f, tagH);
        }
        y += tagH + gapBar;

        DrawProgress(g, cx - barW / 2f, y, barW, barH);
    }

    /// <summary>月牙：一个实心圆，再挖掉一个同底色的圆；右上角一颗四角星。</summary>
    private static void DrawMark(Graphics g, RectangleF box)
    {
        var s = box.Width / 64f;
        using var moon = new SolidBrush(Moon);
        using var paper = new SolidBrush(Paper);
        using var spark = new SolidBrush(Spark);

        g.FillEllipse(moon, box.X + (30.5f - 21) * s, box.Y + (32.5f - 21) * s, 42 * s, 42 * s);
        g.FillEllipse(paper, box.X + (40.5f - 18.5f) * s, box.Y + (23.5f - 18.5f) * s, 37 * s, 37 * s);

        // 四角星（不是圆点）：与 app.ico 保持同一造型，16px 下都还能看出是颗星
        var cx = box.X + 46f * s;
        var cy = box.Y + 16.5f * s;
        var pts = new PointF[8];
        for (var i = 0; i < 8; i++)
        {
            var ang = (-90 + i * 45) * Math.PI / 180.0;
            var r = (i % 2 == 0 ? 6.2f : 2.3f) * s;
            pts[i] = new PointF((float)(cx + r * Math.Cos(ang)), (float)(cy + r * Math.Sin(ang)));
        }
        g.FillPolygon(spark, pts);
    }

    private void DrawProgress(Graphics g, float x, float y, float width, float height)
    {
        var r = height / 2f;
        using (var track = new SolidBrush(Track))
        using (var path = RoundedRect(new RectangleF(x, y, width, height), r))
        {
            g.FillPath(track, path);
        }

        var filled = width * _progress;
        if (filled <= 0.5f) return;
        using var fill = new SolidBrush(Green);
        using var bar = RoundedRect(new RectangleF(x, y, Math.Max(filled, height), height), r);
        g.FillPath(fill, bar);
    }

    private static GraphicsPath RoundedRect(RectangleF box, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        if (d <= 0 || box.Width <= d || box.Height <= d)
        {
            path.AddRectangle(box);
            return path;
        }

        path.AddArc(box.X, box.Y, d, d, 180, 90);
        path.AddArc(box.Right - d, box.Y, d, d, 270, 90);
        path.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90);
        path.AddArc(box.X, box.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>GDI+ 没有字距参数，逐字测量后手动排布，才能做到「字与字之间留白」。</summary>
    private static void DrawTracked(Graphics g, string text, Font font, Brush brush, float centerX, float top, float tracking, float height)
    {
        using var format = StringFormat.GenericTypographic;
        format.FormatFlags |= StringFormatFlags.NoWrap;

        var widths = new float[text.Length];
        var total = 0f;
        for (var i = 0; i < text.Length; i++)
        {
            widths[i] = g.MeasureString(text[i].ToString(), font, PointF.Empty, format).Width;
            total += widths[i];
        }
        total += tracking * (text.Length - 1);

        var x = centerX - total / 2f;
        // 用 MeasureString 的字高把整行垂直居中到 top..top+height 之间
        var lineH = g.MeasureString(text, font, PointF.Empty, format).Height;
        var dy = top + (height - lineH) / 2f;
        for (var i = 0; i < text.Length; i++)
        {
            g.DrawString(text[i].ToString(), font, brush, x, dy, format);
            x += widths[i] + tracking;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _ticker.Dispose();
        base.Dispose(disposing);
    }
}
