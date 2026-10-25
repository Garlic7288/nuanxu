using System.Runtime.InteropServices;
using System.Text;

namespace NuanXu;

/// <summary>
/// 一块活动显示器的运行时状态。
/// <para>
/// <see cref="DeviceName"/>（\\.\DISPLAY1）只用于本次会话写入；
/// <see cref="MonitorId"/>（MONITOR\XXXXXX\{...}\0001）跨重启稳定，用来做快照的键——
/// DISPLAY1/DISPLAY2 的编号在重插线、换显卡后是会互换的。
/// </para>
/// </summary>
internal sealed class DisplaySlot
{
    public string DeviceName = "";
    public string MonitorId = "";
    public string FriendlyName = "";
    public bool Primary;

    /// <summary>本次会话已判定不可用（HDR / 驱动拒绝 / 虚拟显示器），后续跳过，避免反复报错。</summary>
    public bool Failed;

    /// <summary>已做过一次写入回读校验。</summary>
    public bool Verified;

    public uint LastError;

    /// <summary>启动前这块屏的原始 LUT，768 项：R[0..255] G[256..511] B[512..767]。</summary>
    public ushort[]? Original;
}

internal static class NativeGamma
{
    /// <summary>3 通道 × 256 项。</summary>
    public const int RampSize = 768;

    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
    private const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;
    private const uint DISPLAY_DEVICE_MIRRORING_DRIVER = 0x00000008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    /// <summary>
    /// ByValTStr 字段必须是非 null 字符串，否则封送器会写入 0 长度并可能抛 MarshalDirectiveException。
    /// cb 必须等于结构体大小，否则 EnumDisplayDevices 直接返回失败。
    /// </summary>
    public static DISPLAY_DEVICE NewDisplayDevice() => new()
    {
        cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>(),
        DeviceName = "",
        DeviceString = "",
        DeviceID = "",
        DeviceKey = ""
    };

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateDC(string? lpszDriver, string? lpszDevice, string? lpszPort, IntPtr lpdvmInit);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr hdc);

    // 一维 768 项 = 原生 RAMP 的 3×256 WORD，逐字节一致。
    // 不要用 ushort[3,256]：那是靠封送器隐式展平，布局不可控，也没法用 Marshal.Copy 处理。
    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetDeviceGammaRamp(IntPtr hdc, [In] ushort[] ramp);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetDeviceGammaRamp(IntPtr hdc, [Out] ushort[] ramp);

    // 仅供自检做交叉验证：GetDC(0) 拿主屏 DC
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    public static bool AttachedToDesktop(uint flags) => (flags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;
    public static bool IsPrimary(uint flags) => (flags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;
    public static bool IsMirroring(uint flags) => (flags & DISPLAY_DEVICE_MIRRORING_DRIVER) != 0;
}

internal readonly record struct ApplyOutcome(int Applied, int Failed, string? FirstFailed)
{
    public bool AnyApplied => Applied > 0;
    public bool AllFailed => Applied == 0 && Failed > 0;
}

/// <summary>
/// 逐显示器写伽马表。<see cref="IntPtr.Zero"/> 的 GetDC 只能拿到主屏，
/// 双屏用户的副屏会一直是冷白色，所以必须按 device name 逐个 CreateDC。
/// </summary>
internal sealed class DisplayManager
{
    /// <summary>回读校验容差。8bit LUT 的驱动会把 16bit 值量化，单点误差上限约 128，留足余量。</summary>
    private const int VerifyTolerance = 600;

    private static readonly int[] VerifyPoints = [0, 32, 64, 128, 192, 224, 255];

    private readonly object _gate = new();
    private List<DisplaySlot> _slots = [];

    public IReadOnlyList<DisplaySlot> Slots
    {
        get { lock (_gate) return _slots.ToArray(); }
    }

    public int Count { get { lock (_gate) return _slots.Count; } }

    /// <summary>重新枚举显示器。返回 true 表示设备集合变了（数量或 device name 不同），调用方需强制重写。</summary>
    public bool Refresh()
    {
        var fresh = Enumerate();
        lock (_gate)
        {
            var changed = fresh.Count != _slots.Count
                          || !fresh.Select(s => s.DeviceName).SequenceEqual(_slots.Select(s => s.DeviceName));

            // 按 MonitorId 复用上次的原始快照；Failed 重置，因为换分辨率/HDR 之后值得再试一次
            foreach (var slot in fresh)
            {
                var old = _slots.FirstOrDefault(s => s.MonitorId == slot.MonitorId);
                if (old is not null) slot.Original = old.Original;
            }

            _slots = fresh;
            return changed;
        }
    }

    /// <summary>启动时调用：在写入任何暖色之前，逐屏读一次原始 LUT，用于退出时精确还原。</summary>
    public void CaptureOriginals()
    {
        Refresh();
        foreach (var slot in Slots)
        {
            // 读不到就退回线性表。虽然不如原始快照，但总比留着暖色强
            slot.Original = Read(slot) ?? Identity();
        }
    }

    public ApplyOutcome Apply(ushort[] ramp)
    {
        var applied = 0;
        var failed = 0;
        string? firstFailed = null;

        foreach (var slot in Slots)
        {
            if (slot.Failed)
            {
                failed++;
                firstFailed ??= slot.DeviceName;
                continue;
            }

            // 启动之后才插上的显示器，Original 还是空的（Refresh 只按 MonitorId 复用旧快照，
            // 新屏自然没有）。必须在这里、也就是第一次写暖色之前把它读下来——
            // 否则退出时 RestoreAll 只能退回线性表，把用户用校色仪做的校准抹掉；
            // 崩溃恢复路径同理（gamma-original.json 里也没有这块屏）。
            // 此刻它还没被我们写过，读到的就是真正的原始值。
            slot.Original ??= Read(slot);

            if (Write(slot, ramp))
            {
                VerifyOnce(slot, ramp);
                // 回读校验不通过（典型是 HDR 下 Set 返回 TRUE 但根本没生效）
                // 也算失败，否则会误判成「已生效」而永远不提示用户
                if (slot.Failed)
                {
                    failed++;
                    firstFailed ??= slot.DeviceName;
                }
                else applied++;
            }
            else
            {
                slot.Failed = true;
                failed++;
                firstFailed ??= slot.DeviceName;
            }
        }

        return new ApplyOutcome(applied, failed, firstFailed);
    }

    /// <summary>把每块屏恢复成启动前的样子（不是 identity 直线）。返回 false 表示至少一块失败。</summary>
    public bool RestoreAll()
    {
        var allOk = true;
        foreach (var slot in Slots)
        {
            // Original 为空只有一种可能：我们既没读到过它、也没写过它（Apply 会先读再写）。
            // 没碰过就别碰——写一条线性表反而会抹掉显示器自带的校准。
            if (slot.Original is null) continue;
            if (!Write(slot, slot.Original)) allOk = false;
        }
        return allOk;
    }

    /// <summary>
    /// 崩溃/异常路径专用：完全独立于实例，不取锁（UI 线程可能正持着锁），
    /// 现枚举现写，尽力而为。目标只有一个——死之前把屏幕还回去。
    /// </summary>
    public static void EmergencyRestore(IReadOnlyDictionary<string, ushort[]>? backup)
    {
        foreach (var slot in Enumerate())
        {
            var ramp = backup is not null && backup.TryGetValue(slot.MonitorId, out var saved)
                ? saved
                : Identity();
            Write(slot, ramp);
        }
    }

    public static List<DisplaySlot> Enumerate()
    {
        var list = new List<DisplaySlot>();
        for (uint i = 0; ; i++)
        {
            var adapter = NativeGamma.NewDisplayDevice();
            if (!NativeGamma.EnumDisplayDevices(null, i, ref adapter, 0)) break;

            // 不在桌面上（禁用/拔线）和镜像驱动都是幽灵设备，写它们没有任何意义
            if (!NativeGamma.AttachedToDesktop(adapter.StateFlags)) continue;
            if (NativeGamma.IsMirroring(adapter.StateFlags)) continue;

            var slot = new DisplaySlot
            {
                DeviceName = adapter.DeviceName,
                Primary = NativeGamma.IsPrimary(adapter.StateFlags)
            };

            var monitor = NativeGamma.NewDisplayDevice();
            if (NativeGamma.EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, 0))
            {
                slot.MonitorId = string.IsNullOrEmpty(monitor.DeviceID) ? adapter.DeviceName : monitor.DeviceID;
                slot.FriendlyName = monitor.DeviceString;
            }
            else
            {
                slot.MonitorId = adapter.DeviceName;
                slot.FriendlyName = adapter.DeviceString;
            }

            list.Add(slot);
        }
        return list;
    }

    internal static bool Write(DisplaySlot slot, ushort[] ramp)
    {
        var hdc = NativeGamma.CreateDC("DISPLAY", slot.DeviceName, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            // 少数驱动只认 device name 当 driver，回退一次
            hdc = NativeGamma.CreateDC(slot.DeviceName, null, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero)
            {
                slot.LastError = (uint)Marshal.GetLastWin32Error();
                return false;
            }
        }

        try
        {
            if (!NativeGamma.SetDeviceGammaRamp(hdc, ramp))
            {
                slot.LastError = (uint)Marshal.GetLastWin32Error();
                return false;
            }
            return true;
        }
        finally
        {
            NativeGamma.DeleteDC(hdc);   // CreateDC 必须配 DeleteDC，ReleaseDC 是给 GetDC 的
        }
    }

    internal static ushort[]? Read(DisplaySlot slot)
    {
        var hdc = NativeGamma.CreateDC("DISPLAY", slot.DeviceName, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero) return null;
        try
        {
            var buffer = new ushort[NativeGamma.RampSize];
            return NativeGamma.GetDeviceGammaRamp(hdc, buffer) ? buffer : null;
        }
        finally
        {
            NativeGamma.DeleteDC(hdc);
        }
    }

    /// <summary>
    /// 首次写入后回读一次。HDR / 高级颜色模式下 SetDeviceGammaRamp 常常返回 TRUE 却完全不生效，
    /// 只看返回值会把「调了没反应」当成成功。每块屏只做一次，不增加常态开销。
    /// </summary>
    private static void VerifyOnce(DisplaySlot slot, ushort[] expected)
    {
        if (slot.Verified) return;
        slot.Verified = true;

        var actual = Read(slot);
        if (actual is null)
        {
            slot.Failed = true;
            return;
        }

        foreach (var i in VerifyPoints)
        {
            if (Math.Abs(actual[i] - expected[i]) > VerifyTolerance
                || Math.Abs(actual[256 + i] - expected[256 + i]) > VerifyTolerance
                || Math.Abs(actual[512 + i] - expected[512 + i]) > VerifyTolerance)
            {
                slot.Failed = true;
                return;
            }
        }
    }

    /// <summary>暖色曲线。公式沿用 1.7.x，观感是调过的，不要顺手改。</summary>
    public static ushort[] BuildWarmRamp(int warmth)
    {
        var ramp = new ushort[NativeGamma.RampSize];
        var strength = Math.Clamp(warmth, 0, 100) / 100.0;
        for (var i = 0; i < 256; i++)
        {
            var value = i * 257;                       // 0..65535 线性
            ramp[i] = (ushort)value;                   // R 不动
            ramp[256 + i] = (ushort)(value * (1 - strength * 0.09));
            ramp[512 + i] = (ushort)(value * (1 - strength * 0.28));
        }
        return ramp;
    }

    public static ushort[] Identity()
    {
        var ramp = new ushort[NativeGamma.RampSize];
        for (var i = 0; i < 256; i++)
            ramp[i] = ramp[256 + i] = ramp[512 + i] = (ushort)(i * 257);
        return ramp;
    }

    internal static int MaxDiff(ushort[] a, ushort[] b)
    {
        var max = 0;
        for (var i = 0; i < a.Length && i < b.Length; i++)
        {
            var d = Math.Abs(a[i] - b[i]);
            if (d > max) max = d;
        }
        return max;
    }

    /// <summary>
    /// 单屏环境下证明「device name 路径」与「GetDC(0) 路径」写的是同一块硬件 LUT。
    /// 这是多显示器改造能在单屏机器上验收的关键。
    /// </summary>
    internal static (int viaCreateDc, int viaGetDc, int crossDiff) CrossCheck(DisplaySlot slot)
    {
        var probe = new ushort[NativeGamma.RampSize];
        for (var i = 0; i < 256; i++)
        {
            probe[i] = (ushort)(i * 200);
            probe[256 + i] = (ushort)(i * 210);
            probe[512 + i] = (ushort)(i * 220);
        }

        var viaCreateDc = Write(slot, probe) ? 1 : 0;

        var readAfterCreateDc = new ushort[NativeGamma.RampSize];
        var hdc1 = NativeGamma.CreateDC("DISPLAY", slot.DeviceName, null, IntPtr.Zero);
        if (hdc1 != IntPtr.Zero)
        {
            NativeGamma.GetDeviceGammaRamp(hdc1, readAfterCreateDc);
            NativeGamma.DeleteDC(hdc1);
        }

        var hdc0 = NativeGamma.GetDC(IntPtr.Zero);
        var viaGetDc = 0;
        var readAfterGetDc = new ushort[NativeGamma.RampSize];
        if (hdc0 != IntPtr.Zero)
        {
            try
            {
                viaGetDc = NativeGamma.SetDeviceGammaRamp(hdc0, probe) ? 1 : 0;
                NativeGamma.GetDeviceGammaRamp(hdc0, readAfterGetDc);
            }
            finally
            {
                NativeGamma.ReleaseDC(IntPtr.Zero, hdc0);
            }
        }

        return (viaCreateDc, viaGetDc, MaxDiff(readAfterCreateDc, readAfterGetDc));
    }
}

/// <summary>
/// 自检模式（暖序.exe --diag）。WinExe 没有控制台，所以写文件。
/// 单屏机器上也能验证多屏代码路径是否真的等价于原来的 GetDC(0)。
/// </summary>
internal static class Diag
{
    public static string Run()
    {
        var log = new StringBuilder();
        void Line(string text) => log.AppendLine(text);

        // 头部先放环境信息：诊断文件是单独发给开发者的，没有这些就得反复追问
        Line($"[env] 暖序 {Application.ProductVersion} · {Environment.OSVersion.VersionString} · {AppLog.BuildStamp}");
        Line($"[env] 显示器 {Screen.AllScreens.Length} 块 · 64 位进程={Environment.Is64BitProcess} · PID {Environment.ProcessId}");
        Line($"[env] 时间 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line("");

        var manager = new DisplayManager();
        manager.Refresh();
        var slots = manager.Slots;

        Line($"[enum] count={slots.Count}");
        for (var i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            Line($"  #{i} device={s.DeviceName} primary={s.Primary} name={s.FriendlyName}");
            Line($"       monitor={s.MonitorId}");
        }

        if (slots.Count == 0)
        {
            Line("[fatal] 没有枚举到任何活动显示器");
            return log.ToString();
        }

        // 先快照，后面所有写入结束后必须还原
        manager.CaptureOriginals();
        foreach (var s in manager.Slots)
        {
            var o = s.Original;
            Line(o is null
                ? $"[snapshot] {s.DeviceName} 读取失败，将回退线性表"
                : $"[snapshot] {s.DeviceName} R0={o[0]} G128={o[256 + 128]} B255={o[512 + 255]}");
        }

        // 1) 暖色写入 + 回读校验
        var warm = DisplayManager.BuildWarmRamp(72);
        var outcome = manager.Apply(warm);
        Line($"[apply] warm72 applied={outcome.Applied} failed={outcome.Failed}");
        foreach (var s in manager.Slots)
        {
            var actual = DisplayManager.Read(s);
            var diff = actual is null ? -1 : DisplayManager.MaxDiff(actual, warm);
            Line($"  {s.DeviceName} verified={s.Verified} failed={s.Failed} err={s.LastError} readbackMaxDiff={diff}");
        }

        // 2) 交叉验证：device name 路径 vs GetDC(0) 路径
        var primary = manager.Slots.FirstOrDefault(s => s.Primary) ?? manager.Slots[0];
        var (viaCreateDc, viaGetDc, crossDiff) = DisplayManager.CrossCheck(primary);
        Line($"[cross] {primary.DeviceName} viaCreateDC={viaCreateDc} viaGetDC={viaGetDc} diff={crossDiff}");
        Line(crossDiff == 0
            ? "[cross] 一致 —— device name 路径确实命中同一块硬件 LUT，多屏代码在单屏上已验证"
            : $"[cross] 不一致（差值 {crossDiff}）—— 需要排查，可能主屏不是这块");

        // 3) 伪造一个不存在的设备，验证单屏失败不影响其它屏
        var fake = new DisplaySlot { DeviceName = @"\\.\DISPLAY9", MonitorId = "fake" };
        var fakeOk = DisplayManager.Write(fake, warm);
        Line($"[fail-path] 伪造 DISPLAY9 写入={fakeOk} err={fake.LastError}（应为 False，且真实屏不受影响）");

        // 4) 还原
        var restored = manager.RestoreAll();
        Line($"[restore] ok={restored}");
        foreach (var s in manager.Slots)
        {
            var actual = DisplayManager.Read(s);
            var diff = actual is null || s.Original is null ? -1 : DisplayManager.MaxDiff(actual, s.Original);
            Line($"  {s.DeviceName} restoreDiff={diff}（应为 0）");
        }

        Line("[done] 已还原屏幕原始色彩");
        return log.ToString();
    }
}
