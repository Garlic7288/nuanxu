using System.Diagnostics;

namespace NuanXu;

/// <summary>
/// 「请已有实例优雅退出」的协议。
/// <para>
/// 为什么不用 WM_CLOSE：安装器的 Restart Manager 只会发顶层窗口 WM_CLOSE，而我们的
/// <c>OnFormClosing</c> 里「用户点 X 最小化到托盘」和「安装器要求关闭」在 API 层面完全无法区分。
/// 要修就得牺牲点 X 收托盘这个产品行为，不划算。
/// </para>
/// <para>
/// 命名事件语义明确、安装器不需要知道 exe 路径与 PID，且对象随进程销毁自动清理，
/// 不会留下「上一次的残留事件导致误判」。
/// </para>
/// </summary>
internal static class ExitRequest
{
    public const string EventName = @"Local\NuanXu.Exit";

    /// <summary>
    /// 通知已有实例退出，并等它真的退出（默认 15 秒）。没在运行也返回 true。
    /// 这也是 <c>暖序.exe --quit</c> 的实现。
    /// </summary>
    public static bool SignalExistingInstance(int timeoutMs = 15_000)
    {
        if (!IsRunning()) return true;

        try
        {
            if (EventWaitHandle.TryOpenExisting(EventName, out var signal))
            {
                signal.Set();
                signal.Dispose();
            }
            else
            {
                // 进程在、事件却开不出来：可能是旧版本（还没有这个事件）
                return false;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return false;
        }

        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (!IsRunning()) return true;
            Thread.Sleep(150);
        }
        return !IsRunning();
    }

    /// <summary>互斥体对象随进程销毁而消失，比按进程名找更可靠（不受重命名/多用户影响）。</summary>
    private static bool IsRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(Program.MutexName, out var mutex))
            {
                mutex.Dispose();
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        return false;
    }
}
