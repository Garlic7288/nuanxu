using System.Text;

namespace NuanXu;

/// <summary>
/// 极简滚动日志，写 <c>%LOCALAPPDATA%\Nuánxù\app.log</c>，单文件超过 512KB 就轮转一份 .1。
/// <para>
/// 公测阶段必须有一个「用户出问题时能拿到的东西」。之前只有 hotkeys.log 和 bridge.log，
/// 都是单点用途；崩溃、WebView2 初始化失败、色温写不进去这些最需要现场信息的情况反而没有记录。
/// </para>
/// <para>
/// 三条硬约束：<b>绝不抛异常</b>（写日志失败不能连累主流程）、<b>绝不同步阻塞太久</b>
/// （只有几行小字符串，不做网络/大文件）、<b>不记录隐私内容</b>（只写版本、显示器、错误类型，
/// 不写提醒文案、城市以外的用户数据）。
/// </para>
/// </summary>
internal static class AppLog
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nuánxù");

    private static readonly object Gate = new();
    private const long MaxBytes = 512 * 1024;

    public static string FilePath { get; } = Path.Combine(Dir, "app.log");

    /// <summary>
    /// 构建标识（exe 的写入时间）。版本号长期固定在 1.0.0，光看版本号分不清是哪次构建，
    /// 排查「你装的是哪一版」时这一行最管用。
    /// </summary>
    public static string BuildStamp { get; } = ComputeBuildStamp();

    private static string ComputeBuildStamp()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null && File.Exists(exe))
                return "构建 " + File.GetLastWriteTime(exe).ToString("yyyy-MM-dd HH:mm");
        }
        catch
        {
            // 读不到就算了，标识不是关键信息
        }
        return "构建 未知";
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                RotateIfNeeded();
                File.AppendAllText(
                    FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // 磁盘满、目录被占、杀软锁文件——日志写不进去也必须让程序继续跑
        }
    }

    public static void Error(string context, Exception ex) =>
        Write($"[ERR] {context}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    private static void RotateIfNeeded()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists || info.Length < MaxBytes) return;

        var old = FilePath + ".1";
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(FilePath, old);
        }
        catch
        {
            // 轮转失败就继续往原文件追加，宁可文件大一点也不要丢日志
        }
    }

    /// <summary>启动时打一行环境信息，排查「某台机器才复现」的问题时最关键的就是这些。</summary>
    public static void WriteEnvironment(string trigger)
    {
        try
        {
            float scale;
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            var screens = Screen.AllScreens.Length;
            Write($"[{trigger}] 暖序 {Application.ProductVersion}（{BuildStamp}）· {Environment.OSVersion.VersionString} · " +
                  $"DPI {scale * 96:0}（{scale:P0}）· 显示器 {screens} 块 · PID {Environment.ProcessId}");
        }
        catch (Exception ex)
        {
            Error("WriteEnvironment", ex);
        }
    }
}
