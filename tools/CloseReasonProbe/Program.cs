using System.Text;

namespace CloseReasonProbe;

/// <summary>
/// 探针：外部进程投递 WM_CLOSE 时，WinForms 给出的 CloseReason 到底是什么？
///
/// 背景：暖序的 OnFormClosing 里，只有 UserClosing 才会「取消关闭 + 收进托盘」，
/// 而 WindowsShutDown / TaskManagerClosing / ApplicationExitCall 三个分支会真的关掉。
/// 所以「用 PostMessage(WM_CLOSE) 模拟点 X」这个测试方法是否成立，
/// 完全取决于 WinForms 把它归成哪一类。
///
/// 窗口放在屏幕外，不干扰用户。
/// </summary>
internal static class Program
{
    private static readonly string LogPath = @"C:\wxaudit\close-reason.txt";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        Directory.CreateDirectory(@"C:\wxaudit");
        File.WriteAllText(LogPath, "started\n", Encoding.UTF8);

        var form = new Form
        {
            Text = "CloseReasonProbe",
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),   // 屏幕外，不打扰用户
            Size = new Size(320, 180),
            ShowInTaskbar = false,
        };

        form.FormClosing += (_, e) =>
        {
            // 模拟暖序的行为：UserClosing 就取消关闭并隐藏
            var line = $"CloseReason={e.CloseReason}\n";
            if (e.CloseReason == CloseReason.UserClosing)
            {
                line += "action=cancel+hide\n";
                File.AppendAllText(LogPath, line, Encoding.UTF8);
                e.Cancel = true;
                form.Hide();
                return;
            }
            line += "action=close\n";
            File.AppendAllText(LogPath, line, Encoding.UTF8);
        };

        Application.Run(form);
        File.AppendAllText(LogPath, "exited\n", Encoding.UTF8);
    }
}
