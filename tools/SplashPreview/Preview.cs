using System.Drawing.Imaging;
using System.Windows.Forms;
using NuanXu;

namespace SplashPreview;

/// <summary>
/// 启动画面渲染预览：按真实客户区尺寸画出 SplashForm，
/// 在进度走到一半和走满两个时刻各存一张图，方便核对版式。
/// 用法：SplashPreview.exe <宽> <高> <输出目录>
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var width = args.Length > 0 ? int.Parse(args[0]) : 1148;
        var height = args.Length > 1 ? int.Parse(args[1]) : 1082;
        var outDir = args.Length > 2 ? args[2] : @"C:\wxshot";

        ApplicationConfiguration.Initialize();

        var splash = new SplashForm
        {
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(0, 0, width, height),
            TopMost = false
        };
        splash.Show();
        splash.Begin();

        var shots = 0;
        var timer = new System.Windows.Forms.Timer { Interval = 40 };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var planned = new Queue<(long At, string Name)>();
        planned.Enqueue((520, "mid"));
        planned.Enqueue((900, "full"));

        timer.Tick += (_, _) =>
        {
            if (planned.Count == 0)
            {
                timer.Stop();
                splash.Close();
                Application.Exit();
                return;
            }

            var (at, name) = planned.Peek();
            if (clock.ElapsedMilliseconds < at) return;
            planned.Dequeue();

            using var bmp = new Bitmap(splash.Width, splash.Height);
            splash.DrawToBitmap(bmp, new Rectangle(0, 0, splash.Width, splash.Height));
            var path = Path.Combine(outDir, $"splash-{width}x{height}-{name}.png");
            bmp.Save(path, ImageFormat.Png);
            shots++;
            Console.WriteLine($"{name}: {path}");
        };
        timer.Start();

        Application.Run();
        Console.WriteLine($"完成，共 {shots} 张");
    }
}
