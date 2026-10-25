using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NuanXu;

/// <summary>
/// 全局热键：用 RegisterHotKey 挂在窗口句柄上，WM_HOTKEY 走 WndProc。
///
/// 为什么不用 MenuStrip 的 ShortcutKeys：那个只在窗口有焦点时生效，
/// 而且 WebView2 拿到焦点后会把按键吞掉，传不到 WinForms——办公室用户整天在
/// Excel / 浏览器里，等于这个快捷键根本用不上。
///
/// 键位为什么统一带 Ctrl+Alt：
///   Ctrl+字母 是 Office 的地盘（复制/保存/查找），Alt+字母 是 Ribbon 访问键，
///   Ctrl+Shift+字母 是输入法中英文切换。只剩 Ctrl+Alt+ 这一块是桌面软件的自定义热键区。
/// </summary>
internal static class HotKeys
{
    public const int WmHotKey = 0x0312;

    public const int IdEyeCare = 1;
    public const int IdFollowSun = 2;
    public const int IdPause = 3;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    // 按住不放时不重复触发，否则按住 E 会疯狂开关
    private const uint ModNoRepeat = 0x4000;

    // 显示在菜单右侧的文字。必须和下面注册的键位一致，改一处就要改另一处。
    public const string EyeCareText = "Ctrl+Alt+E";
    public const string FollowSunText = "Ctrl+Alt+F";
    public const string PauseText = "Ctrl+Alt+P";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>注册一组 Ctrl+Alt+ 组合。返回 false 通常意味着这个组合已被别的软件占用。</summary>
    public static bool TryRegister(IntPtr hwnd, int id, uint virtualKey)
    {
        try
        {
            return RegisterHotKey(hwnd, id, ModControl | ModAlt | ModNoRepeat, virtualKey);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }

    /// <summary>注销。句柄还没建或已经销毁时会失败，这里不关心返回值。</summary>
    public static void Unregister(IntPtr hwnd, int id)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            UnregisterHotKey(hwnd, id);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }
}
