using System.Diagnostics;
using System.Text.Json;

namespace NuanXu;

/// <summary>原子写：先写临时文件再替换，避免断电/崩溃留下半截 JSON。</summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);

        if (!File.Exists(path))
        {
            File.Move(tmp, path);
            return;
        }

        try
        {
            // File.Replace 是原子替换，并且顺手留一份 .bak
            File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            // 跨卷、只读属性等情况下 Replace 会失败，退化成覆盖写（至少不会丢文件）
            File.Copy(tmp, path, overwrite: true);
            TryDelete(tmp);
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 删不掉就算了，下次覆盖写
        }
    }
}

/// <summary>
/// 伽马表的生命周期保护。
/// <para>
/// 分层兜底：正常退出 → 全局异常 → 注销关机 → 睡眠 → 脏退出标记（下次启动补救）。
/// <c>taskkill /F</c> 走 TerminateProcess，进程一行代码都执行不到，这在进程内无解；
/// 能保证的是「下次启动时先把屏幕还回去」，而不是让偏色一直累积。
/// </para>
/// </summary>
internal static class GammaGuard
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nuánxù");

    /// <summary>运行期间存在，干净退出时删除。存在即代表上次是异常退出。</summary>
    private static readonly string LockPath = Path.Combine(Dir, "session.lock");

    /// <summary>启动前各显示器的原始 LUT，异常退出后靠它还原。</summary>
    private static readonly string BackupPath = Path.Combine(Dir, "gamma-original.json");

    private static DisplayManager? _displays;
    private static int _restored;

    public static void Attach(DisplayManager displays) => _displays = displays;

    /// <summary>
    /// 启动第一步。返回 true 表示检测到上次是异常退出，已先把屏幕复位。
    /// 这一步必须在写入任何暖色之前。
    /// </summary>
    public static bool RecoverFromDirtyExit()
    {
        try
        {
            if (!File.Exists(LockPath)) return false;
            DisplayManager.EmergencyRestore(LoadBackup());
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }

    public static void MarkRunning()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(LockPath, $"{Environment.ProcessId}|{DateTime.Now:O}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    /// <summary>只有确认已成功复位才删标记；否则留着让下次启动补救。</summary>
    public static void MarkCleanExit(bool restoredOk)
    {
        if (!restoredOk) return;
        AtomicFile.TryDelete(LockPath);
    }

    /// <summary>
    /// 终止路径的统一入口（FormClosed / 全局异常 / ProcessExit），幂等。
    /// 注意：Suspend、锁屏这类「还要继续运行」的场景不要用这个，直接调 RestoreAll，
    /// 否则闩锁会把后续的正常复位挡掉。
    /// </summary>
    public static bool RestoreOnce()
    {
        if (Interlocked.Exchange(ref _restored, 1) != 0) return true;
        try
        {
            return _displays?.RestoreAll() ?? true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }

    public static void SaveBackup(IReadOnlyList<DisplaySlot> slots)
    {
        try
        {
            var map = new Dictionary<string, string>();
            foreach (var slot in slots)
            {
                if (slot.Original is not null) map[slot.MonitorId] = Encode(slot.Original);
            }
            if (map.Count == 0) return;
            AtomicFile.WriteAllText(BackupPath, JsonSerializer.Serialize(map));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private static Dictionary<string, ushort[]>? LoadBackup()
    {
        try
        {
            if (!File.Exists(BackupPath)) return null;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(BackupPath));
            if (map is null) return null;

            var result = new Dictionary<string, ushort[]>();
            foreach (var (key, value) in map)
            {
                var ramp = Decode(value);
                if (ramp is not null) result[key] = ramp;
            }
            return result.Count > 0 ? result : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return null;
        }
    }

    private static string Encode(ushort[] ramp)
    {
        var bytes = new byte[ramp.Length * 2];
        Buffer.BlockCopy(ramp, 0, bytes, 0, bytes.Length);   // win-x64 小端，与 ushort 内存布局一致
        return Convert.ToBase64String(bytes);
    }

    private static ushort[]? Decode(string value)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length != NativeGamma.RampSize * 2) return null;
            var ramp = new ushort[NativeGamma.RampSize];
            Buffer.BlockCopy(bytes, 0, ramp, 0, bytes.Length);
            return ramp;
        }
        catch
        {
            return null;
        }
    }
}
