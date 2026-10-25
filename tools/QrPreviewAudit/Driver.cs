using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace QrPreviewAudit;

internal static class Driver
{
    [DllImport("shcore.dll")]
    private static extern int SetProcessDpiAwareness(int value);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    private static Form? _frame;
    private static bool _captured;
    private static bool _forced;
    private static int _ticks;
    private static string _outDir = ".";

    [STAThread]
    private static void Main(string[] args)
    {
        // 必须 DPI-aware，否则 CopyFromScreen 按逻辑坐标截物理窗口，截出来内容只填左上角
        SetProcessDpiAwareness(2);
        _outDir = args.Length > 0 ? args[0] : AppContext.BaseDirectory;
        Directory.CreateDirectory(_outDir);

        ApplicationConfiguration.Initialize();

        var qrPath = Path.Combine(AppContext.BaseDirectory, "donate-qr.png");
        using var image = Image.FromFile(qrPath);

        var timer = new System.Windows.Forms.Timer { Interval = 600 };
        timer.Tick += OnTick;
        timer.Start();

        var sw = Stopwatch.StartNew();
        Extracted.Run(image);          // 内部 ShowDialog，会阻塞到窗口关闭
        sw.Stop();
        timer.Stop();

        if (!_captured)
        {
            Console.WriteLine("FAIL 预览窗口没有出现");
            return;
        }
        if (_forced)
        {
            Console.WriteLine("FAIL 点击后窗口没关闭，由测试台强制关闭");
            return;
        }
        Console.WriteLine($"CLICK CLOSED OK 弹窗存活 {sw.ElapsedMilliseconds}ms（含 600ms 轮询延迟）");
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        _ticks++;

        if (!_captured)
        {
            _frame = Application.OpenForms.Cast<Form>()
                .FirstOrDefault(f => f.Visible && f.FormBorderStyle == FormBorderStyle.None);
            if (_frame is null) return;   // 还没弹出来，下一拍再看
            _captured = true;
            Inspect();
            Capture();
            Click();
            return;
        }

        // 已经点过但窗口还在：超过 3 秒就判定失败，强制收场避免测试台挂死
        if (_ticks > 5)
        {
            _forced = true;
            _frame?.Close();
        }
    }

    private static void Inspect()
    {
        var f = _frame!;
        Console.WriteLine($"预览窗口 client={f.ClientSize.Width}x{f.ClientSize.Height} " +
                          $"bounds={f.Bounds.Width}x{f.Bounds.Height} topmost={f.TopMost} " +
                          $"居中={f.StartPosition}");
        foreach (Control c in f.Controls)
        {
            var text = c is Label lb ? $" '{lb.Text}'" : "";
            var img = c is PictureBox pb && pb.Image is not null
                ? $" img={pb.Image.Width}x{pb.Image.Height} mode={pb.SizeMode}"
                : "";
            Console.WriteLine($"  {c.GetType().Name,-11} b={c.Width}x{c.Height} at=({c.Left},{c.Top}){img}{text}");
        }

        // 提示条不能压到码上：图片底边 <= 提示条顶边
        var pic = f.Controls.OfType<PictureBox>().FirstOrDefault();
        var tip = f.Controls.OfType<Label>().FirstOrDefault();
        if (pic is not null && tip is not null)
        {
            var overlap = pic.Bottom - tip.Top;
            Console.WriteLine(overlap <= 0
                ? $"  OK 提示条在码下方，间隙 {-overlap}px"
                : $"  BAD 提示条压住码 {overlap}px");
        }
    }

    private static void Capture()
    {
        var f = _frame!;
        var rect = f.Bounds;
        using var bmp = new Bitmap(rect.Width, rect.Height);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(rect.Location, Point.Empty, rect.Size);
        var path = Path.Combine(_outDir, "qr-preview.png");
        bmp.Save(path, ImageFormat.Png);
        Console.WriteLine($"  截图 -> {path}");
    }

    private static void Click()
    {
        // 用真实光标 + mouse_event，而不是往句柄投消息：
        // 投消息绕过了子控件命中测试，测不出「点在图上是关不掉」这类问题
        var f = _frame!;
        var center = new Point(f.Left + f.ClientSize.Width / 2, f.Top + f.ClientSize.Height / 2);
        SetCursorPos(center.X, center.Y);
        Thread.Sleep(60);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(40);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
    }
}
