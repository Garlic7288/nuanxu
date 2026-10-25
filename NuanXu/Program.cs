using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace NuanXu;

internal static class Program
{
    // 单实例锁：Local\ 前缀限定在当前登录会话内，多用户各自一份互不影响
    internal const string MutexName = @"Local\NuanXu.SingleInstance";
    internal const string ActivateEventName = @"Local\NuanXu.Activate";

    // 开源仓库地址：关于对话框、README、安装包说明都以它为准，只在这里改一次
    internal const string RepoUrl = "https://github.com/Garlic7288/nuanxu";

    [STAThread]
    private static void Main(string[] args)
    {
        // 自检模式：枚举显示器、写一条暖色、回读比对、还原，结果写文件（WinExe 没有控制台）
        if (args.Any(a => a.Equals("--diag", StringComparison.OrdinalIgnoreCase)))
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nuánxù");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "diag.txt");
            File.WriteAllText(path, Diag.Run(), new UTF8Encoding(false));
            return;
        }

        // 开机自启：注册表 Run 值带这个参数，表示「静默进托盘，不弹窗口」
        var autoStart = args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));

        // 请已有实例优雅退出：安装器、卸载器、售后脚本都走这条路。
        // 必须放在建互斥体之前，否则自己会先被单实例检查挡下来。
        if (args.Any(a => a.Equals("--quit", StringComparison.OrdinalIgnoreCase)))
        {
            ExitRequest.SignalExistingInstance();
            return;
        }

        using var instanceLock = new Mutex(true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // 开机自启撞上残留实例（上次没退干净）：静默退出。
            // 刻意不发 Activate 事件——那会把窗口拉起来，与「开机静默」直接矛盾。
            // 手动双击时仍然唤醒窗口并提示，用户才知道程序已经开着了。
            if (autoStart) return;

            // 已有实例在运行：唤醒它的窗口，然后提示并退出，不再起第二个进程
            try
            {
                if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var activate))
                {
                    activate.Set();
                    activate.Dispose();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }

            MessageBox.Show(
                "暖序已经在运行了，不需要重复打开。\n\n已为你切换到正在运行的窗口。",
                "暖序",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        // 路径漂移修复：搬过目录或换了安装模式后，Run 值还指着旧路径，
        // 开机会启动旧版本甚至启动失败。搬目录是正常操作，这里静默改掉。
        AutoStart.RepairIfStale();

        ApplicationConfiguration.Initialize();

        // 全局兜底必须装在任何窗口创建之前。
        // 目标不是「救活进程」——非 UI 线程的未处理异常在 .NET Core 上一定会终止进程——
        // 而是「死之前把屏幕还回去」，否则用户会看到屏幕永久偏暖，还以为显示器坏了。
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += OnUiThreadException;
        AppDomain.CurrentDomain.UnhandledException += OnFatalException;
        // 双保险：Main 正常返回 / Environment.Exit 会触发；被 TerminateProcess 时不会
        AppDomain.CurrentDomain.ProcessExit += (_, _) => GammaGuard.RestoreOnce();

        // 上次是异常退出（被强杀 / 崩溃）：先把屏幕复位，再开始正常流程
        if (GammaGuard.RecoverFromDirtyExit())
            AppLog.Write("检测到上次异常退出（session.lock 残留），已先把屏幕复位");
        GammaGuard.MarkRunning();
        AppLog.WriteEnvironment("启动");

        try
        {
            Application.Run(new MainWindow(startHidden: autoStart));
        }
        finally
        {
            GammaGuard.MarkCleanExit(GammaGuard.RestoreOnce());
        }
    }

    private static void OnUiThreadException(object sender, ThreadExceptionEventArgs e)
    {
        var restored = GammaGuard.RestoreOnce();
        AppLog.Error("UI 线程未处理异常", e.Exception);
        try
        {
            MessageBox.Show(
                "暖序遇到了一个内部错误，已恢复屏幕原始色彩。\n\n" + e.Exception.Message,
                "暖序", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        GammaGuard.MarkCleanExit(restored);
        Environment.Exit(1);
    }

    private static void OnFatalException(object sender, UnhandledExceptionEventArgs e)
    {
        // 这里只能做一件事：一次 P/Invoke 复位。
        // 不弹框（UI 线程可能已死）、不加锁（可能死锁）、不 await。
        // 日志只做「尽力而为」的一行：AppLog 内部全是 try/catch，写不进去就跳过，
        // 不会影响下面这次必须成功的复位。
        if (e.ExceptionObject is Exception ex) AppLog.Error("非 UI 线程未处理异常", ex);
        GammaGuard.RestoreOnce();
    }
}

internal sealed class UserSettings
{
    // 护眼总开关：关掉就完全不做色温调整
    public bool Enabled { get; set; } = true;
    // 跟随日光：开启时按日出日落曲线自动变化，关闭时用滑块值作为固定暖度
    public bool FollowSun { get; set; } = true;
    // 提醒默认关闭：护眼色温是装上就该生效的主功能，而定时弹窗属于打扰，
    // 必须由用户自己确认要，才在设置里打开。首次运行就弹提醒会被当成骚扰软件。
    public bool RemindersEnabled { get; set; } = false;
    public int ReminderMinutes { get; set; } = 1;
    /// <summary>
    /// 提醒方式：true = 循环（每隔 N 分钟一次），false = 仅提醒一次。
    /// 1.8.3 起默认「仅一次」——对大多数白领用户来说，要的是「到点提醒我一下」，
    /// 而不是被长期反复打断；需要规律打断的人在卡片上切一下即可。
    /// 用 bool 不用 enum：老配置里没有这个字段时，反序列化会保留这里的初始值 false，
    /// 于是「新装默认单次」和「老配置升级」自然一致，不需要任何迁移代码。
    /// </summary>
    public bool ReminderRepeat { get; set; } = false;
    public string ReminderText { get; set; } = "抬头看看远处，让眼睛放松一下。";
    /// <summary>「今天跳过」的截止时间（本地时间，次日 00:00）。null 表示没跳过。</summary>
    public DateTime? SkipUntil { get; set; }
    public int WarmthStrength { get; set; } = 65;
    public bool ReminderSound { get; set; } = true;
    /// <summary>
    /// 全局快捷键总开关。默认开——快捷键就是给"不想找托盘"的人用的，
    /// 默认关等于没做。远程桌面、录屏、游戏里碍事时可以从菜单关掉。
    /// 老配置没有这个字段，反序列化会保留这里的初始值 true，不需要迁移。
    /// </summary>
    public bool HotKeysEnabled { get; set; } = true;
    /// <summary>
    /// 上次退出时是不是最大化。默认 false——一上来就铺满整个屏幕，
    /// 在 3840 宽的屏上会看到 1260px 宽的内容孤零零浮在中间，四周全是空白。
    /// </summary>
    public bool StartMaximized { get; set; } = false;
    /// <summary>上次退出时的窗口尺寸（设备像素）。0 = 还没记过，按界面内容自适应。</summary>
    public int WindowWidth { get; set; } = 0;
    public int WindowHeight { get; set; } = 0;
    /// <summary>
    /// 「窗口尺寸已按内容自适应」迁移标记。
    /// 老配置里的 StartMaximized=true 不是用户的选择，而是旧版默认值被 SaveWindowState 写下去的，
    /// 没法区分，只能做一次性迁移：第一次跑新版时清掉，之后用户自己最大化再关，就按他的意思记。
    /// </summary>
    public bool WindowFitContent { get; set; } = false;
    /// <summary>当前地区，决定日出日落曲线与天气预报的坐标。</summary>
    public string City { get; set; } = CityCatalog.DefaultCity;
}

/// <summary>城市及其经纬度。中国全境统一 UTC+8，但日出日落按经度真实计算（乌鲁木齐与上海差 4 小时）。</summary>
internal sealed record CityInfo(string Name, double Latitude, double Longitude, string Region);

internal static class CityCatalog
{
    /// <summary>默认城市：未选择或选择失效时回落到广州。</summary>
    public const string DefaultCity = "广州";

    // 按大区顺序排列，GroupBy 会沿用这个出现顺序，菜单分组即是这个顺序
    public static readonly CityInfo[] All =
    [
        new("北京", 39.9042, 116.4074, "华北"),
        new("天津", 39.3434, 117.3616, "华北"),
        new("石家庄", 38.0428, 114.5149, "华北"),
        new("太原", 37.8706, 112.5489, "华北"),
        new("呼和浩特", 40.8414, 111.7519, "华北"),
        new("唐山", 39.6304, 118.1804, "华北"),
        new("保定", 38.8740, 115.4646, "华北"),

        new("沈阳", 41.8057, 123.4315, "东北"),
        new("大连", 38.9140, 121.6147, "东北"),
        new("长春", 43.8171, 125.3235, "东北"),
        new("哈尔滨", 45.8038, 126.5349, "东北"),
        new("吉林", 43.8378, 126.5495, "东北"),
        new("大庆", 46.5891, 125.1038, "东北"),

        new("上海", 31.2304, 121.4737, "华东"),
        new("南京", 32.0603, 118.7969, "华东"),
        new("苏州", 31.2989, 120.5853, "华东"),
        new("杭州", 30.2741, 120.1551, "华东"),
        new("宁波", 29.8683, 121.5440, "华东"),
        new("合肥", 31.8206, 117.2272, "华东"),
        new("福州", 26.0745, 119.2965, "华东"),
        new("厦门", 24.4798, 118.0894, "华东"),
        new("南昌", 28.6820, 115.8579, "华东"),
        new("济南", 36.6512, 117.1201, "华东"),
        new("青岛", 36.0671, 120.3826, "华东"),

        new("郑州", 34.7466, 113.6254, "华中"),
        new("武汉", 30.5928, 114.3055, "华中"),
        new("长沙", 28.2282, 112.9388, "华中"),
        new("洛阳", 34.6197, 112.4540, "华中"),
        new("宜昌", 30.6919, 111.2865, "华中"),

        new("广州", 23.1291, 113.2644, "华南"),
        new("深圳", 22.5431, 114.0579, "华南"),
        new("珠海", 22.2707, 113.5767, "华南"),
        new("佛山", 23.0215, 113.1216, "华南"),
        new("东莞", 23.0207, 113.7518, "华南"),
        new("南宁", 22.8170, 108.3665, "华南"),
        new("桂林", 25.2736, 110.2900, "华南"),
        new("海口", 20.0444, 110.1999, "华南"),
        new("三亚", 18.2528, 109.5119, "华南"),

        new("重庆", 29.5630, 106.5516, "西南"),
        new("成都", 30.5728, 104.0668, "西南"),
        new("贵阳", 26.6470, 106.6302, "西南"),
        new("昆明", 25.0389, 102.7183, "西南"),
        new("拉萨", 29.6520, 91.1721, "西南"),
        new("绵阳", 31.4675, 104.6796, "西南"),

        new("西安", 34.3416, 108.9398, "西北"),
        new("兰州", 36.0611, 103.8343, "西北"),
        new("西宁", 36.6171, 101.7782, "西北"),
        new("银川", 38.4872, 106.2309, "西北"),
        new("乌鲁木齐", 43.8256, 87.6168, "西北"),
        new("克拉玛依", 45.5798, 84.8892, "西北"),

        new("中国香港", 22.3193, 114.1694, "港澳台"),
        new("中国澳门", 22.1987, 113.5439, "港澳台"),
        // 台湾并成一条：本岛跨 21.9–25.3N、120.0–122.0E，取全岛中心算日出日落，
        // 比取台北更接近全岛平均（台北会让日出/日落各偏早十几分钟）
        new("中国台湾", 23.7000, 120.9600, "港澳台")
    ];

    /// <summary>
    /// 已合并/改名的旧城市名 → 现名。不写这层映射的话，老配置里选过「中国台北」的用户
    /// 会因为查不到而静默回落到广州，等于把他的城市改掉了。
    /// </summary>
    private static readonly Dictionary<string, string> LegacyAliases = new()
    {
        ["中国台北"] = "中国台湾",
        ["中国高雄"] = "中国台湾",
    };

    public static CityInfo Resolve(string? name)
    {
        if (name is not null && LegacyAliases.TryGetValue(name, out var mapped)) name = mapped;
        return All.FirstOrDefault(c => c.Name == name) ?? All.First(c => c.Name == DefaultCity);
    }

    public static IEnumerable<IGrouping<string, CityInfo>> Grouped() => All.GroupBy(c => c.Region);
}

/// <summary>某一天的预报，字段名与前端 JSON 契约一致。</summary>
internal sealed class WeatherDay
{
    public string Date { get; set; } = "";
    public int Code { get; set; }
    public int Max { get; set; }
    public int Min { get; set; }
}

internal sealed class WeatherSnapshot
{
    public bool Ok { get; set; }
    public WeatherDay[] Days { get; set; } = [];
    /// <summary>最近一次成功拉取的时间，失败时用来判断是否还有旧数据可显示。</summary>
    public DateTime FetchedAt { get; set; }
}

internal sealed class MainWindow : Form
{
    // 提醒文案上限：与前端 textarea 的 maxlength 保持一致
    internal const int MaxReminderTextLength = 40;

    private readonly WebView2 _browser = new() { Dock = DockStyle.Fill };
    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 30_000 };
    private readonly System.Windows.Forms.Timer _reminderClock = new() { Interval = 1_000 };
    // 天气每小时刷一次就够，接口是免费公益服务，没必要勤快
    private readonly System.Windows.Forms.Timer _weatherClock = new() { Interval = 60 * 60 * 1000 };
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Icon _appIcon;

    // ---- 图标按当前 DPI 取档（1.8.2） --------------------------------------
    // app.ico 里编了 16/24/32/48/64/128/256 七档，但 Icon.ExtractAssociatedIcon
    // 固定只返回 32×32。150% 缩放下托盘与标题栏要的是 24px，靠系统降采样会发虚。
    // ExtractIconEx 传 nIconIndex = -1 时，Windows 会按 SM_CXSMICON / SM_CXICON
    // 自己从多尺寸 ico 里挑最合适的一档——这才是多尺寸 ico 存在的意义。
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, int count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>
    /// 取应用图标，优先按当前 DPI 从多尺寸 ico 里挑；失败回退到旧的 32×32 取法。
    /// 图标不显示比发虚严重得多，所以这里任何异常都吞掉走回退。
    /// </summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            var large = new IntPtr[1];
            var small = new IntPtr[1];
            if (ExtractIconEx(Application.ExecutablePath, -1, large, small, 1) > 0 && small[0] != IntPtr.Zero)
            {
                // 必须先 Clone 再 DestroyIcon：FromHandle 返回的 Icon 只是包了句柄，
                // 句柄一销毁它就不能用了。
                using (var raw = Icon.FromHandle(small[0]))
                {
                    var icon = (Icon)raw.Clone();
                    DestroyIcon(small[0]);
                    if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
                    return icon;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
    }

    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nuánxù", "settings.json");
    private UserSettings _settings = new();
    private ToolStripMenuItem? _eyeCareMenu;
    private ToolStripMenuItem? _followSunMenu;
    private ToolStripMenuItem? _remindersMenu;
    private ToolStripMenuItem? _soundMenu;
    private ToolStripMenuItem? _hotKeysMenu;
    private ToolStripMenuItem? _pauseMenu;
    // 开机自启的状态存在注册表里，这两项只是视图，勾选由 SyncAutoStartMenus() 从注册表刷新
    private ToolStripMenuItem? _autoStartMenu;
    private ToolStripMenuItem? _trayAutoStartMenu;
    private ToolStripMenuItem? _trayEyeCareMenu;
    private ToolStripMenuItem? _trayFollowSunMenu;
    private ToolStripMenuItem? _trayRemindersMenu;
    // 暂停项的文案会随剩余时间变（「暂停 30 分钟」/「取消暂停（剩余 N 分钟）」），
    // 所以托盘那份也要留字段，不能像别的项那样就地 new 完丢掉
    private ToolStripMenuItem? _trayPauseMenu;
    // 主菜单与托盘各有一份地区菜单，切换后两处勾选要一起同步
    private readonly List<(string City, ToolStripMenuItem Item)> _cityMenuItems = [];
    private readonly List<ToolStripMenuItem> _cityMenuRoots = [];
    private EventWaitHandle? _activateEvent;
    private EventWaitHandle? _exitEvent;
    private readonly ManualResetEvent _shutdown = new(false);
    private Thread? _watcher;
    // 显示器/电源事件会连发，去抖后统一重写一次
    private readonly System.Windows.Forms.Timer _reapplyTimer = new() { Interval = 1200 };
    private SoundPlayer? _reminderChime;
    private DateTime _nextReminder;
    private WeatherSnapshot? _weather;
    // 本地页面地址，同时用于加载、导航白名单和消息来源校验
    private Uri? _pageUri;
    private bool _bridgeDropLogged;
    // 天气请求代次：回包时代次不匹配就丢弃，避免旧城市的响应覆盖新城市
    private int _weatherRequestId;
    // 天气失败日志只记一次，避免断网时每 10 分钟刷一行
    private bool _weatherFailureLogged;
    private bool _exitRequested;
    private bool _webReady;
    private bool _started;
    // 开机自启：窗口一次都不显示（不闪、不弹启动画面），靠覆写 SetVisibleCore 拦住
    private bool _startHidden;
    // 与 _startHidden 的区别：这个全程不变，用来抑制「开机时不该弹的提示」
    // （_startHidden 在第一次拦截后就被清掉了，那时再判断已经晚了）
    private bool _quietStart;
    // 注册热键时记下的窗口句柄。退出时用这个值注销，不要去读 Handle 属性——那会触发表单重建句柄
    private IntPtr _hotKeyHwnd;
    // 句柄每次重建（DPI 变化、切换主题）都会重新注册热键，冲突提示只该弹一次
    private bool _hotKeyWarned;
    // 「暂停 30 分钟」：到点自动恢复护眼。刻意不落盘——退出程序就等于结束暂停，
    // 否则用户暂停完直接关程序，下次启动会发现护眼一直关着，还找不到原因。
    private DateTime _pauseUntil;
    private readonly System.Windows.Forms.Timer _pauseTimer = new();
    private SplashForm? _splash;
    private bool _gammaWarningShown;
    private bool _reminderDialogOpen;
    private TimeSpan _remainingOnPause;
    // 单次提醒已经弹过并收工。只存在内存里：重启后开关本来就是关的，
    // 显示「已暂停」比显示「已完成」更不容易让人误会。
    private bool _onceCompleted;
    // 「今天跳过」到什么时候为止（次日 00:00）。只跳过今天，不关提醒开关。
    private DateTime? _skipUntil;
    private int _lastSentSeconds = -1;
    private DateTime _lastHiddenSend = DateTime.MinValue;
    // 设置文件损坏、从 .bak 恢复时给用户一次提示，别让设置"神秘变化"
    private bool _settingsRestoredFromBackup;
    // NormalizeSettings 做了一次性迁移，需要立刻落盘，否则每次启动都会重跑一遍
    private bool _settingsMigrated;
    // 内容自适应的目标尺寸（CSS 像素）。句柄建好之前算不出非客户区，
    // 所以先记在这里，OnHandleCreated 里再换算成真实客户区。
    private Size? _pendingContentSize;

    // 逐显示器调色。GetDC(0) 只能拿到主屏，双屏用户的副屏会一直是冷白色。
    private readonly DisplayManager _displays = new();
    // 当前硬件 LUT 里躺着的是不是「我们写的值」。只有我们写过的才由我们负责恢复，
    // 没写过就绝对不碰——否则会把 Windows 自带「夜间模式」反复打回原色。
    private bool _rampWritten;
    // 上次写入的暖度，用于去重：白天日照曲线平缓时几乎不产生写入
    private int _lastAppliedWarmth = -1;

    public MainWindow(bool startHidden = false)
    {
        _startHidden = startHidden;
        // 单独留一份：_startHidden 在第一次 SetVisibleCore 拦截后就被清掉，
        // 之后再判断「是不是开机启动」已经晚了（热键注册提示、设置恢复提示都要用到）
        _quietStart = startHidden;
        LoadSettings();
        NormalizeSettings();
        // 迁移结果立刻落盘：否则每次启动都重跑一遍，会把用户后来自己拉大的窗口又清掉
        if (_settingsMigrated) SaveSettings();
        Text = "暖序";
        ApplyInitialWindowBounds();
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(244, 244, 240);
        _appIcon = LoadAppIcon();
        Icon = _appIcon;
        // WebView2 默认把缓存写在 exe 同级目录（暖序.exe.WebView2，约 80MB）。
        // 装进 Program Files 后普通用户对安装目录没有写权限，初始化会直接抛异常导致启动失败。
        // 统一放到 LocalAppData，安装版与绿色版行为一致，也不会污染安装目录。
        _browser.CreationProperties = new CoreWebView2CreationProperties
        {
            UserDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Nuánxù",
                "WebView2")
        };
        Controls.Add(_browser);
        MainMenuStrip = BuildMenu();
        Controls.Add(MainMenuStrip);
        _nextReminder = DateTime.Now.AddMinutes(_settings.ReminderMinutes);
        ConfigureTray();
        _clock.Tick += (_, _) => UpdateColor();
        _clock.Start();
        _reapplyTimer.Tick += (_, _) =>
        {
            _reapplyTimer.Stop();
            ReapplyAfterDisplayChange();
        };
        _weatherClock.Tick += (_, _) => _ = RefreshWeatherAsync();
        _weatherClock.Start();
        // 暂停到点：EndPause 里会判断「用户中途有没有自己开回来」，
        // 所以这里无条件调，不需要额外守卫
        _pauseTimer.Tick += (_, _) => EndPause("暂停结束，护眼已恢复");
        // 先记下每块屏启动前的原始 LUT，退出时才能精确还原（而不是写一条 identity 直线，
        // 那会抹掉用户用校色仪做的显示器校准）
        _displays.CaptureOriginals();
        GammaGuard.Attach(_displays);
        GammaGuard.SaveBackup(_displays.Slots);
        // 护眼开着且不跟随日光：启动即恢复固定暖度，否则滑块显示 52% 屏幕却是原色
        if (_settings.Enabled && !_settings.FollowSun) UpdateColor(force: true);
        StartWatchers();
        SubscribeSystemEvents();
        _reminderClock.Tick += (_, _) => TickReminder();
        _reminderClock.Start();
        Shown += async (_, _) =>
        {
            // 从托盘恢复也会触发 Shown，启动画面与 WebView2 初始化只能跑一次
            if (_started) return;
            _started = true;
            ShowSplash();
            await InitializeBrowserAsync();
        };
        FormClosing += OnFormClosing;
        FormClosed += (_, _) =>
        {
            AppLog.Write("窗口已关闭，开始释放资源");
            // 热键挂在窗口句柄上，句柄销毁后系统仍会保留注册（表现为「组合键被占、但暖序没反应」），
            // 必须显式注销。用 _hotKeyHwnd 而不是 Handle：读 Handle 属性会触发表单重建句柄。
            HotKeys.Unregister(_hotKeyHwnd, HotKeys.IdEyeCare);
            HotKeys.Unregister(_hotKeyHwnd, HotKeys.IdFollowSun);
            HotKeys.Unregister(_hotKeyHwnd, HotKeys.IdPause);
            _pauseTimer.Dispose();
            UnsubscribeSystemEvents();
            // 顺序不能改：先让监听线程退出 WaitAny，Join 等它真的走，最后才 Dispose 句柄
            _shutdown.Set();
            _watcher?.Join(1000);
            _activateEvent?.Dispose();
            _exitEvent?.Dispose();
            _shutdown.Dispose();
            _splash?.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _clock.Dispose();
            _reminderClock.Dispose();
            _weatherClock.Dispose();
            _reapplyTimer.Dispose();
            _http.Dispose();
            _reminderChime?.Dispose();
            _appIcon.Dispose();
        };
    }

    /// <summary>
    /// 开机自启时让窗口「从未可见」。
    /// 不用 Shown 里 Hide()：那会先真实显示一帧（开机闪一下），而且 Shown 已经触发、
    /// 启动画面会跟着弹出来。这里在 base 之前 return，窗口句柄不创建、Shown 不触发，
    /// 首次点托盘恢复时才走正常流程（启动画面、WebView2 初始化照旧，与冷启动一致）。
    /// </summary>
    protected override void SetVisibleCore(bool value)
    {
        if (value && _startHidden)
        {
            _startHidden = false;
            // 句柄必须在这里建出来：托盘恢复、--quit、第二实例唤醒全都走 Post→BeginInvoke，
            // 没有句柄 BeginInvoke 会一直抛 InvalidOperationException，重试 2 秒后静默丢弃，
            // 表现为「托盘点了没反应」「--quit 杀不掉」。
            // 建句柄 ≠ 显示窗口：WS_VISIBLE 没设，IsWindowVisible 仍为 false，也不进任务栏。
            _ = Handle;
            return;
        }
        base.SetVisibleCore(value);
    }

    /// <summary>
    /// 全局热键挂在窗口句柄上，句柄一建就得注册。
    /// 放构造函数里不行：那时 Handle 还是 Zero，读一下会提前建句柄、绕开 SetVisibleCore 的静默拦截。
    /// 放 Shown 里也不行：开机自启根本不会触发 Shown，热键就永远不生效。
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyHotKeys();
        ApplyPendingContentSize();
    }

    // 界面内容的设计尺寸，单位是 CSS 像素（WebView2 里 1 CSS px = 1 逻辑像素 = 96dpi）。
    // 依据 index.html 实测：.shell 最大宽度 1260px，4 天天气数据态下内容底边 890px，
    // 留一点余量取 1280×920。窗口按这个尺寸开，内容刚好铺满——以前默认最大化，
    // 在 3840 宽的屏上只看到 1260px 的内容孤零零浮在中间，四周全是空白。
    private const int ContentWidthCss = 1280;
    private const int ContentHeightCss = 920;
    private const int MinWidthCss = 1024;
    private const int MinHeightCss = 700;

    /// <summary>屏幕缩放比。用 GetDC(0) 而不是 DeviceDpi：构造函数里句柄还没建，DeviceDpi 一律返回 96。</summary>
    private static float ScreenScale()
    {
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        return g.DpiX / 96f;
    }

    private static int Scaled(int cssPx, float scale) => (int)Math.Round(cssPx * scale);

    /// <summary>
    /// 启动时的窗口尺寸。优先级：用户上次最大化 &gt; 用户上次调过的尺寸 &gt; 按界面内容自适应。
    /// 所有设计值都写逻辑像素再乘缩放比——旧代码把 1024/700 这些阈值直接和设备像素比，
    /// 在 150% 屏上等于要求 683 逻辑宽，判断全错，这才是"每次启动都最大化"的真正原因。
    /// </summary>
    private void ApplyInitialWindowBounds()
    {
        var scale = ScreenScale();
        var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);

        MinimumSize = new Size(
            Math.Min(Scaled(MinWidthCss, scale), workArea.Width),
            Math.Min(Scaled(MinHeightCss, scale), workArea.Height));

        if (_settings.StartMaximized)
        {
            Size = MinimumSize;
            WindowState = FormWindowState.Maximized;
            return;
        }

        // 存的尺寸同样是设备像素，和 MinimumSize（已换算过）比就是同一把尺子
        if (_settings.WindowWidth >= MinimumSize.Width && _settings.WindowHeight >= MinimumSize.Height)
        {
            Size = new Size(
                Math.Min(_settings.WindowWidth, workArea.Width),
                Math.Min(_settings.WindowHeight, workArea.Height));
            return;
        }

        // 内容自适应：这里算不出边框和菜单栏高度，先记下目标，句柄建好后再换算
        _pendingContentSize = new Size(ContentWidthCss, ContentHeightCss);
        Size = MinimumSize;
    }

    /// <summary>
    /// 把「页面内容尺寸」换算成窗口客户区。
    /// 菜单栏是客户区里的一个控件（不是非客户区），所以客户区高 = 页面高 + 菜单高，
    /// 少算这一条，页面底部会被切掉一个菜单的高度。
    /// </summary>
    private void ApplyPendingContentSize()
    {
        if (_pendingContentSize is not { } css) return;
        _pendingContentSize = null;

        var scale = DeviceDpi / 96f;
        var menu = MainMenuStrip?.Height ?? 0;
        ClientSize = new Size(Scaled(css.Width, scale), Scaled(css.Height, scale) + menu);

        // 屏幕装不下就缩到工作区，让页面自己滚动；刻意不退化成最大化——
        // 用户要的是"按内容开窗"，不是"反正放不下就铺满"
        var work = Screen.FromHandle(Handle).WorkingArea;
        if (Width > work.Width || Height > work.Height)
            Size = new Size(Math.Min(Width, work.Width), Math.Min(Height, work.Height));

        // 必须重新居中：StartPosition=CenterScreen 是按构造函数里的占位尺寸算的，
        // 这里尺寸一变，窗口就偏到右下方去了（实测偏 (203,209)）
        Location = new Point(
            work.Left + Math.Max(0, (work.Width - Width) / 2),
            work.Top + Math.Max(0, (work.Height - Height) / 2));
    }

    /// <summary>
    /// 全局快捷键的入口。不能用 MenuStrip.ShortcutKeys 或 ProcessCmdKey：
    /// 那两个都要求窗体有焦点，而用户按热键时前台通常是 Excel、PPT 或浏览器。
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == HotKeys.WmHotKey)
        {
            switch (m.WParam.ToInt32())
            {
                case HotKeys.IdEyeCare:
                    ToggleEyeCareHotKey();
                    return;
                case HotKeys.IdFollowSun:
                    ToggleFollowSunHotKey();
                    return;
                case HotKeys.IdPause:
                    TogglePause();
                    return;
            }
        }

        // Explorer 重启（崩溃、Windows 更新）后系统会广播 TaskbarCreated，
        // 但 NotifyIcon 不会自己回来：托盘图标从此消失，用户再也点不到菜单。
        // 重新赋值一次 Visible 就是逼 Shell_NotifyIcon 重新注册图标。
        if (TaskbarCreatedMessage != 0 && m.Msg == (int)TaskbarCreatedMessage)
        {
            try
            {
                _tray.Visible = false;
                _tray.Visible = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
            base.WndProc(ref m);
            return;
        }

        base.WndProc(ref m);
    }

    /// <summary>系统广播「任务栏已重建」的消息号。必须先 RegisterWindowMessage 拿到。</summary>
    private static readonly int TaskbarCreatedMessage = (int)RegisterWindowMessage("TaskbarCreated");

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    /// <summary>
    /// 后台监听两个事件：另一个实例被启动（唤醒窗口）、外部请求退出（安装器/卸载器/--quit）。
    /// 用 WaitAny 而不是裸 WaitOne：退出时先 Set(_shutdown) 让线程自己走，再 Join，最后才 Dispose，
    /// 否则 Dispose 时线程正阻塞在 WaitOne 上，会抛 ObjectDisposedException——
    /// 那是在非 UI 线程、且在 try 之外，进程直接终止，绕过了伽马复位。
    /// </summary>
    private void StartWatchers()
    {
        try
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ActivateEventName);
            _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitRequest.EventName);

            _watcher = new Thread(() =>
            {
                var handles = new WaitHandle[] { _activateEvent, _exitEvent, _shutdown };
                while (true)
                {
                    int index;
                    try
                    {
                        index = WaitHandle.WaitAny(handles);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;   // 兜底，正常路径走不到
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex);
                        return;
                    }

                    if (index == 2) return;          // _shutdown：立刻走，不再碰任何句柄
                    if (index == 1)
                    {
                        _exitRequested = true;
                        Post(ExitApplication);
                        return;
                    }
                    if (_exitRequested) return;
                    Post(RestoreWindow);
                }
            })
            { IsBackground = true, Name = "暖序事件监听" };
            _watcher.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    /// <summary>跨线程回 UI 线程的安全投递。SystemEvents 在自己的线程上抛事件，句柄可能还没建好。</summary>
    private void Post(Action action)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke(action);
                return;
            }
            catch (ObjectDisposedException)
            {
                // ObjectDisposedException 继承自 InvalidOperationException，必须排在前面
                return;
            }
            catch (InvalidOperationException)
            {
                Thread.Sleep(100);   // 句柄还没创建，等一会儿再试
            }
        }
    }

    private void SubscribeSystemEvents()
    {
        SystemEvents.SessionEnding += OnSessionEnding;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void UnsubscribeSystemEvents()
    {
        // 必须反订阅：SystemEvents 是静态事件，会一直持有这个窗体的引用，
        // 不解除就是窗体泄漏 + 退出后仍被回调
        SystemEvents.SessionEnding -= OnSessionEnding;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        // 注销/关机：驱动稍后会重建桌面，但先把色彩还回去，成功就清掉脏退出标记
        GammaGuard.MarkCleanExit(_displays.RestoreAll());
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            // 睡前先还原：万一唤醒后会话/驱动异常，屏幕也不会一直偏暖
            _displays.RestoreAll();
            _rampWritten = false;
            _lastAppliedWarmth = -1;
        }
        else if (e.Mode == PowerModes.Resume)
        {
            // 显示器和驱动要几百毫秒才就绪，延时重写
            DebouncedReapply();
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock
            or SessionSwitchReason.ConsoleConnect
            or SessionSwitchReason.RemoteConnect)
        {
            DebouncedReapply();
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => DebouncedReapply();

    /// <summary>改分辨率会连发好几次 DisplaySettingsChanged，去抖一下避免重复枚举与写入。</summary>
    private void DebouncedReapply()
    {
        Post(() =>
        {
            _reapplyTimer.Stop();
            _reapplyTimer.Start();
        });
    }

    private void ReapplyAfterDisplayChange()
    {
        // 旧的 device name 可能已经失效（换线、关掉一块屏），必须重新枚举
        var changed = _displays.Refresh();
        if (changed) _lastAppliedWarmth = -1;
        UpdateColor(force: changed);
        // 显示配置变了：新插上的屏刚刚在 Apply 里补读了原始 LUT，快照必须跟着更新。
        // 不更新的话，下次异常退出恢复时找不到这块屏，只能给它写线性表——校准就丢了。
        if (changed) GammaGuard.SaveBackup(_displays.Slots);
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip { BackColor = Color.FromArgb(250, 250, 247), ForeColor = Color.FromArgb(45, 49, 44), Font = new Font("Microsoft YaHei UI", 9) };
        var file = new ToolStripMenuItem("文件");
        file.DropDownItems.Add("最小化到托盘", null, (_, _) => { SaveWindowState(); Hide(); });
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("退出暖序", null, (_, _) => ExitApplication());

        var settings = new ToolStripMenuItem("设置");
        _eyeCareMenu = new ToolStripMenuItem("护眼模式")
        {
            CheckOnClick = true,
            Checked = _settings.Enabled,
            // 只是把键位显示在右侧，真正的注册走 RegisterHotKey——
            // 这里的 ShortcutKeys 属性在 WebView2 抢焦点时会失效，不能依赖
            ShortcutKeyDisplayString = HotKeys.EyeCareText
        };
        _eyeCareMenu.Click += (_, _) => SetEyeCare(_eyeCareMenu!.Checked);
        settings.DropDownItems.Add(_eyeCareMenu);
        _followSunMenu = new ToolStripMenuItem("跟随日光")
        {
            CheckOnClick = true,
            Checked = _settings.FollowSun,
            ShortcutKeyDisplayString = HotKeys.FollowSunText
        };
        _followSunMenu.Click += (_, _) => SetFollowSun(_followSunMenu!.Checked);
        settings.DropDownItems.Add(_followSunMenu);
        // 投屏/演讲场景：按一下关掉、30 分钟后自己回来，不用记着再按一次
        _pauseMenu = new ToolStripMenuItem("暂停 30 分钟")
        {
            ShortcutKeyDisplayString = HotKeys.PauseText
        };
        _pauseMenu.Click += (_, _) => TogglePause();
        settings.DropDownItems.Add(_pauseMenu);
        _remindersMenu = new ToolStripMenuItem("休息提醒") { CheckOnClick = true, Checked = _settings.RemindersEnabled };
        _remindersMenu.Click += (_, _) => SetRemindersEnabled(_remindersMenu!.Checked);
        settings.DropDownItems.Add(_remindersMenu);

        _soundMenu = new ToolStripMenuItem("提醒音") { CheckOnClick = true, Checked = _settings.ReminderSound };
        _soundMenu.Click += (_, _) =>
        {
            _settings.ReminderSound = _soundMenu!.Checked;
            SaveSettings();
        };
        settings.DropDownItems.Add(_soundMenu);

        settings.DropDownItems.Add("休息提醒设置…", null, async (_, _) =>
        {
            if (_webReady && _browser.CoreWebView2 is not null)
                await _browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('#editReminder').click()");
        });

        settings.DropDownItems.Add(new ToolStripSeparator());
        // 全局快捷键：远程桌面、录屏、游戏里 Ctrl+Alt+ 会碍事，给一个总闸
        _hotKeysMenu = new ToolStripMenuItem("启用全局快捷键") { CheckOnClick = true, Checked = _settings.HotKeysEnabled };
        _hotKeysMenu.Click += (_, _) =>
        {
            _settings.HotKeysEnabled = _hotKeysMenu!.Checked;
            SaveSettings();
            ApplyHotKeys();
        };
        settings.DropDownItems.Add(_hotKeysMenu);
        // 开机自启属于「程序行为」，和上面的护眼选项分开一组。
        // 勾选状态来自注册表，不来自 settings.json——展开菜单时再刷一次，感知外部改动。
        _autoStartMenu = new ToolStripMenuItem("开机自动启动") { CheckOnClick = true, Checked = AutoStart.IsEnabled() };
        _autoStartMenu.Click += (_, _) => SetAutoStart(_autoStartMenu!.Checked);
        settings.DropDownItems.Add(_autoStartMenu);

        settings.DropDownItems.Add(new ToolStripSeparator());
        settings.DropDownItems.Add(BuildCityMenu());
        settings.DropDownOpening += (_, _) =>
        {
            SyncAutoStartMenus();
            // 剩余分钟要现算，展开时刷一次就够（不需要每秒刷）
            SyncPauseMenus();
        };

        var help = new ToolStripMenuItem("帮助");
        help.DropDownItems.Add("关于暖序", null, (_, _) => ShowAboutDialog());
        // 公测支持入口：用户报「不生效 / 打不开」时，先让他点这里把诊断文件发过来
        help.DropDownItems.Add("诊断信息…", null, (_, _) => ShowDiagnostics());
        menu.Items.AddRange([file, settings, help]);
        return menu;
    }

    /// <summary>构建「地区」二级菜单。主菜单和托盘各建一份，两处勾选由 SyncCityMenu 统一同步。</summary>
    private ToolStripMenuItem BuildCityMenu()
    {
        var root = new ToolStripMenuItem("地区 · " + _settings.City);
        _cityMenuRoots.Add(root);
        foreach (var group in CityCatalog.Grouped())
        {
            var region = new ToolStripMenuItem(group.Key);
            foreach (var city in group)
            {
                var item = new ToolStripMenuItem(city.Name)
                {
                    Checked = city.Name == _settings.City,
                    Tag = city.Name
                };
                item.Click += (s, _) => SetCity((string)((ToolStripMenuItem)s!).Tag!);
                region.DropDownItems.Add(item);
                _cityMenuItems.Add((city.Name, item));
            }
            root.DropDownItems.Add(region);
        }
        return root;
    }

    private void SetCity(string name)
    {
        var city = CityCatalog.Resolve(name);
        if (_settings.City == city.Name)
        {
            SyncCityMenu();
            SendState();
            return;
        }

        _settings.City = city.Name;
        SaveSettings();
        SyncCityMenu();

        // 换了地区，旧城市的预报立刻作废，否则会短暂显示错城市的数据
        _weather = null;
        _ = RefreshWeatherAsync();

        // 日出日落变了，暖度曲线必须跟着重算
        UpdateColor();
        SendState();

        _tray.ShowBalloonTip(3000, "暖序", $"已切换到{city.Name}，暖度曲线与天气都按当地重新计算。", ToolTipIcon.None);
    }

    // 地区是单选，点击后必须手动把其余项取消勾选
    private void SyncCityMenu()
    {
        foreach (var (city, item) in _cityMenuItems)
            item.Checked = city == _settings.City;
        // 标题带上当前城市，收起菜单时也能一眼看到选的是哪儿
        foreach (var root in _cityMenuRoots)
            root.Text = "地区 · " + _settings.City;
        UpdateTrayText();
    }

    /// <summary>托盘悬停提示跟随城市与当前状态。NotifyIcon.Text 上限 127 字符，中文城市名不会超。</summary>
    private void UpdateTrayText()
    {
        // 刻意只写「状态」不写剩余分钟：暂停计时器是「到点响一次」的单次定时器，没有秒级 Tick，
        // 写进去的分钟数会一直停在按下那一刻，悬停看到「剩余 30 分钟」反而是误导。
        string state;
        if (!_settings.Enabled) state = "护眼已关";
        else if (_pauseUntil > DateTime.Now) state = "暂停中";
        else state = _settings.FollowSun ? "跟随日光" : "固定暖度";

        var text = $"暖序 · {_settings.City} · {state}";
        if (_tray.Text != text) _tray.Text = text;
    }

    private void SetEyeCare(bool enabled)
    {
        // 手动开关一律视为「结束暂停」。不这么做的话：用户暂停后自己把护眼开回来，
        // 30 分钟到点时 EndPause 又会来一句「暂停结束，护眼已恢复」——状态没变，话却说错了。
        // 更糟的是暂停后手动关一次，计时器到点会把护眼强行打开，与用户刚做的操作相反。
        CancelPause();
        _settings.Enabled = enabled;
        SyncToggleMenus();
        SaveSettings();
        // 开和关都走 UpdateColor：关掉时它只在「确实是我们写过的」前提下还原一次，
        // 之后定时器不会再碰 LUT
        UpdateColor(force: true);
    }

    private void SetFollowSun(bool enabled)
    {
        _settings.FollowSun = enabled;
        SyncToggleMenus();
        SaveSettings();
        // 开：交给日照曲线（曲线 × 内部增益）；关：滑块值作为固定暖度。
        // 刻意不把当前曲线值写回 WarmthStrength——那会静默吞掉用户存过的固定偏好，
        // 切换时滑块会跳到用户自己的值，跳变是可见的（滑块与胶囊同时变），比偷偷改数据好。
        UpdateColor(force: true);
    }

    /// <summary>
    /// 暂停时长。默认 30 分钟；测试可以用 NUANXU_PAUSE_SECONDS 覆盖——
    /// 真机验证不可能等半小时，留一个只有测试会用的环境变量比改代码安全。
    /// </summary>
    private static TimeSpan PauseDuration
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("NUANXU_PAUSE_SECONDS")
                      ?? Environment.GetEnvironmentVariable("WARMSEQUENCE_PAUSE_SECONDS");
            if (int.TryParse(raw, out var seconds) && seconds > 0) return TimeSpan.FromSeconds(seconds);
            return TimeSpan.FromMinutes(30);
        }
    }

    /// <summary>
    /// 暂停 / 立即恢复。给投屏、全屏演讲这类「现在必须还原原色」的场景用：
    /// 按一下关掉，到点自己回来，不用记着再按一次。
    /// </summary>
    private void TogglePause()
    {
        if (_pauseUntil > DateTime.Now)
        {
            EndPause("已提前恢复护眼");
            return;
        }
        StartPause();
    }

    private void StartPause()
    {
        // 护眼本来就是关的：再「暂停」一次毫无意义，更糟的是到点会把护眼打开——
        // 用户是自己关的（做色彩校准、投屏），30 分钟后屏幕自己变暖，还找不到原因。
        // 这里直接拒绝并说明，不武装计时器，也就不会留下一次多余的「恢复」。
        if (!_settings.Enabled)
        {
            _tray.ShowBalloonTip(4000, "暖序", "护眼当前已是关闭状态，无需暂停。", ToolTipIcon.Info);
            return;
        }

        var span = PauseDuration;
        // 顺序不能反：SetEyeCare 内部会 CancelPause 清掉旧计时，
        // 先写 _pauseUntil 再调它，刚设好的结束时间会被自己清掉，暂停永不恢复。
        // SetEyeCare(false) 会把 Enabled 落盘；暂停期间被强杀，下次启动也是原色，
        // 用户至少不会看到「说好暂停却还是暖的」
        SetEyeCare(false);
        _pauseUntil = DateTime.Now.Add(span);
        _pauseTimer.Interval = (int)Math.Clamp(span.TotalMilliseconds, 1000, int.MaxValue);
        _pauseTimer.Start();
        SyncPauseMenus();
        var text = span.TotalMinutes >= 1
            ? $"已暂停护眼 {Math.Round(span.TotalMinutes)} 分钟，到点自动恢复。"
            : $"已暂停护眼 {span.TotalSeconds:0} 秒，到点自动恢复。";
        _tray.ShowBalloonTip(4000, "暖序", text, ToolTipIcon.Info);
    }

    private void EndPause(string message)
    {
        _pauseTimer.Stop();
        _pauseUntil = default;
        if (!_settings.Enabled) SetEyeCare(true);
        SyncPauseMenus();
        if (!string.IsNullOrEmpty(message)) _tray.ShowBalloonTip(4000, "暖序", message, ToolTipIcon.Info);
    }

    /// <summary>只清计时状态，不动 Enabled——手动开关护眼时用这个，避免多绕一次 SetEyeCare。</summary>
    private void CancelPause()
    {
        _pauseTimer.Stop();
        _pauseUntil = default;
        SyncPauseMenus();
    }

    /// <summary>
    /// 「暂停」这一项的文字跟随状态：暂停中改成「取消暂停（剩余 N 分钟）」，
    /// 否则用户想提前结束只能干等，或者去猜再按一次会不会恢复。
    /// </summary>
    private void SyncPauseMenus()
    {
        var left = _pauseUntil - DateTime.Now;
        string text;
        if (left > TimeSpan.Zero)
        {
            text = left.TotalMinutes >= 1
                ? $"取消暂停（剩余 {Math.Ceiling(left.TotalMinutes):0} 分钟）"
                : $"取消暂停（剩余 {Math.Max(1, Math.Ceiling(left.TotalSeconds)):0} 秒）";
        }
        else
        {
            var span = PauseDuration;
            text = span.TotalMinutes >= 1
                ? $"暂停 {span.TotalMinutes:0} 分钟"
                : $"暂停 {span.TotalSeconds:0} 秒";
        }
        if (_pauseMenu is not null && _pauseMenu.Text != text) _pauseMenu.Text = text;
        if (_trayPauseMenu is not null && _trayPauseMenu.Text != text) _trayPauseMenu.Text = text;
        // 暂停开始/结束都会走到这里，托盘提示的「暂停中」也就跟着变
        UpdateTrayText();
    }

    /// <summary>Ctrl+Alt+E：投屏前按一下还原原色，讲完再按一下恢复。</summary>
    private void ToggleEyeCareHotKey()
    {
        // 暂停中按这个键 = 立即恢复，和菜单里「暂停 30 分钟」的语义保持一致
        if (_pauseUntil > DateTime.Now)
        {
            EndPause("已恢复护眼");
            return;
        }
        SetEyeCare(!_settings.Enabled);
    }

    /// <summary>Ctrl+Alt+F：切换跟随日光。护眼关着时它没有意义，必须给反馈，否则就是「按了没反应」。</summary>
    private void ToggleFollowSunHotKey()
    {
        if (!_settings.Enabled)
        {
            _tray.ShowBalloonTip(3000, "暖序", "护眼已关闭，跟随日光不生效。", ToolTipIcon.Info);
            return;
        }
        SetFollowSun(!_settings.FollowSun);
    }

    /// <summary>
    /// 注册 / 注销全局热键。句柄重建后（DPI 变化、主题切换）会再走一次，所以每次都先注销。
    /// 注册失败最常见的原因是这个组合已被别的软件占用——写进 hotkeys.log，
    /// 用户报「快捷键不生效」时能一眼看出是哪一组。
    /// </summary>
    private void ApplyHotKeys()
    {
        if (Handle == IntPtr.Zero) return;
        // 存下句柄值：退出时不能再读 Handle 属性，那会触发表单重建句柄
        _hotKeyHwnd = Handle;

        HotKeys.Unregister(_hotKeyHwnd, HotKeys.IdEyeCare);
        HotKeys.Unregister(_hotKeyHwnd, HotKeys.IdFollowSun);
        HotKeys.Unregister(_hotKeyHwnd, HotKeys.IdPause);

        if (!_settings.HotKeysEnabled)
        {
            ClearHotKeyLog();
            return;
        }

        var failed = new List<string>();
        if (!HotKeys.TryRegister(_hotKeyHwnd, HotKeys.IdEyeCare, (uint)Keys.E)) failed.Add(HotKeys.EyeCareText);
        if (!HotKeys.TryRegister(_hotKeyHwnd, HotKeys.IdFollowSun, (uint)Keys.F)) failed.Add(HotKeys.FollowSunText);
        if (!HotKeys.TryRegister(_hotKeyHwnd, HotKeys.IdPause, (uint)Keys.P)) failed.Add(HotKeys.PauseText);
        WriteHotKeyLog(failed);

        // 开机自启是静默进托盘的，这时候弹提示只会让人以为程序出错了
        if (failed.Count > 0 && !_quietStart && !_hotKeyWarned)
        {
            _hotKeyWarned = true;
            _tray.ShowBalloonTip(6000, "暖序",
                "这些快捷键已被其他程序占用：" + string.Join("、", failed) + "。可在「设置」里关闭全局快捷键。",
                ToolTipIcon.Warning);
        }
    }

    private static string HotKeyLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nuánxù", "hotkeys.log");

    private static void WriteHotKeyLog(List<string> failed)
    {
        try
        {
            if (failed.Count == 0)
            {
                if (File.Exists(HotKeyLogPath)) File.Delete(HotKeyLogPath);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(HotKeyLogPath)!);
            File.WriteAllText(HotKeyLogPath,
                "以下快捷键注册失败（通常已被其他程序占用）：" + Environment.NewLine +
                string.Join(Environment.NewLine, failed) + Environment.NewLine,
                new UTF8Encoding(false));
            AppLog.Write("[快捷键] 注册失败：" + string.Join("、", failed));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private static void ClearHotKeyLog() => WriteHotKeyLog([]);

    /// <summary>
    /// 开机自启的唯一写入口。状态只看注册表，两处菜单勾选都从它刷新。
    /// CheckOnClick 会先把勾选翻过来，所以失败时按注册表重刷一遍（而不是再翻回去）——
    /// 这样主菜单与托盘一次对齐，也不会和外部改动打架。
    /// </summary>
    private void SetAutoStart(bool enabled)
    {
        var ok = enabled ? AutoStart.Enable() : AutoStart.Disable();
        SyncAutoStartMenus();
        if (ok) return;
        _tray.ShowBalloonTip(4000, "暖序",
            "无法写入开机自启设置，可能被安全软件或组策略拦截。", ToolTipIcon.Warning);
    }

    private void SyncAutoStartMenus()
    {
        var on = AutoStart.IsEnabled();
        if (_autoStartMenu is not null) _autoStartMenu.Checked = on;
        if (_trayAutoStartMenu is not null) _trayAutoStartMenu.Checked = on;
    }

    // 菜单的勾选状态始终以设置为准，避免菜单/托盘两个入口各说各话
    private void SyncToggleMenus()
    {
        if (_eyeCareMenu is not null) _eyeCareMenu.Checked = _settings.Enabled;
        if (_trayEyeCareMenu is not null) _trayEyeCareMenu.Checked = _settings.Enabled;
        if (_followSunMenu is not null) _followSunMenu.Checked = _settings.FollowSun;
        if (_trayFollowSunMenu is not null) _trayFollowSunMenu.Checked = _settings.FollowSun;
        // 护眼关掉时「跟随日光」没有意义，置灰
        if (_followSunMenu is not null) _followSunMenu.Enabled = _settings.Enabled;
        if (_trayFollowSunMenu is not null) _trayFollowSunMenu.Enabled = _settings.Enabled;
        // 状态变了托盘提示也要跟着变，否则悬停还写着上一个状态
        UpdateTrayText();
    }

    /// <summary>主菜单与托盘菜单里的「休息提醒」勾选同步。两处必须一起改，漏一处就会自相矛盾。</summary>
    private void SyncReminderMenus(bool enabled)
    {
        if (_remindersMenu is not null) _remindersMenu.Checked = enabled;
        if (_trayRemindersMenu is not null) _trayRemindersMenu.Checked = enabled;
    }

    private void SetRemindersEnabled(bool enabled)
    {
        SyncReminderMenus(enabled);

        // 状态没变（双击、按钮与菜单两个入口重复通知）时必须原样返回：
        // 否则「恢复」分支会因 _remainingOnPause 已清零而重开一整轮，表现为"按开启又从满额开始"
        if (_settings.RemindersEnabled == enabled)
        {
            SendState();
            return;
        }

        _settings.RemindersEnabled = enabled;

        if (enabled)
        {
            // 用户主动开启（含单次完成后的「再来一次」）要清掉完成态，否则前端一直显示「已完成」
            _onceCompleted = false;
            // 恢复：接着暂停时冻结的剩余时间走；没有冻结值才重开一整轮
            var remaining = _remainingOnPause > TimeSpan.Zero ? _remainingOnPause : TimeSpan.FromMinutes(_settings.ReminderMinutes);
            _nextReminder = DateTime.Now.Add(remaining);
            _remainingOnPause = TimeSpan.Zero;
        }
        else
        {
            // 暂停：冻结当前剩余时间（已过期则按整轮算）
            var left = _nextReminder - DateTime.Now;
            _remainingOnPause = left > TimeSpan.Zero ? left : TimeSpan.FromMinutes(_settings.ReminderMinutes);
            // 用户主动暂停时清掉「今天跳过」的尾巴，否则明天恢复后还会再跳一天
            _skipUntil = null;
            _settings.SkipUntil = null;
        }

        SaveSettings();
        SendState();
    }

    /// <summary>
    /// 单次提醒完成：关掉开关，但把状态标成「已完成」而不是「已暂停」。
    ///
    /// 为什么不复用 SetRemindersEnabled(false)：那条「暂停」分支会把剩余时间冻结下来供下次续走，
    /// 而单次完成根本没有"下次接着走"的概念——冻结值只会让用户下次点开启时莫名其妙。
    /// 这里直接把冻结值清零，下次开启走满额一整轮。
    ///
    /// 不在这里 SendState：唯一的调用方 ShowRestReminder 末尾本来就会发一次。
    /// </summary>
    private void FinishOnceReminder()
    {
        _onceCompleted = true;
        _remainingOnPause = TimeSpan.Zero;
        _settings.RemindersEnabled = false;
        // 单次模式下「今天跳过」等同于取消，不该留下跳过尾巴
        _skipUntil = null;
        _settings.SkipUntil = null;
        SyncReminderMenus(false);
        SaveSettings();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        SaveWindowState();
        _tray.Visible = false;
        Close();
    }

    private void ShowAboutDialog()
    {
        using var about = BuildAboutForm(_appIcon);
        if (Visible) about.ShowDialog(this);
        else about.ShowDialog();
    }

    /// <summary>
    /// 公测支持入口：把显示器自检结果和应用日志落到数据目录，然后打开那个文件夹。
    /// 用户报「调了没反应 / 打不开界面」时，让他点一下这里，把 diag.txt 与 app.log 发过来——
    /// 比来回问「你什么系统、几块屏、是不是开了 HDR」高效得多。
    /// </summary>
    private void ShowDiagnostics()
    {
        var dir = Path.GetDirectoryName(_settingsPath)!;
        var diagPath = Path.Combine(dir, "diag.txt");
        string summary;
        try
        {
            File.WriteAllText(diagPath, Diag.Run(), new UTF8Encoding(false));
            AppLog.Write("已生成诊断报告");
            summary = "诊断文件已生成：\n" + diagPath + "\n" + AppLog.FilePath;
        }
        catch (Exception ex)
        {
            AppLog.Error("生成诊断报告失败", ex);
            summary = "生成诊断报告失败：" + ex.Message;
        }

        try
        {
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("打开数据目录失败", ex);
        }

        MessageBox.Show(this, summary + "\n\n把这两个文件发给开发者即可定位问题。",
            "暖序诊断", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    internal static Form BuildAboutForm(Icon appIcon)
    {
        // 版式：居中品牌区 → 版本胶囊 → 分隔线 → 两列信息 → 分隔线 → 二维码卡片 → 主按钮
        // 坐标一律按 96 DPI 设计，再乘实际缩放比。字体用磅值（物理单位，与 DPI 无关），不能跟着放大，
        // 否则 125%/150% 缩放的屏幕上字体变大、容器没变大，文字会被裁掉。
        // 仓库地址统一定义在 Program 里；这里是主窗体类，取不到那个常量，本地引一次
        const string RepoUrl = Program.RepoUrl;
        float scale;
        using (var screen = Graphics.FromHwnd(IntPtr.Zero)) scale = screen.DpiX / 96f;
        int S(int value) => (int)Math.Round(value * scale);

        int width = S(420);
        int padX = S(30);
        int contentW = width - padX * 2;
        var ink = Color.FromArgb(43, 55, 44);        // 主文字
        var sub = Color.FromArgb(123, 127, 117);     // 次要文字
        var line = Color.FromArgb(230, 231, 224);    // 分隔线 / 描边

        var about = new Form
        {
            Text = "关于暖序",
            ClientSize = new Size(width, S(422)),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.None,   // 已手动按 DPI 换算，禁止再自动缩放
            BackColor = Color.FromArgb(250, 250, 247),
            Font = new Font("Microsoft YaHei UI", 9),
            Icon = appIcon
        };

        var controls = new List<Control>();
        int y = S(20);

        // 1) 应用图标，水平居中。按 64px 原生尺寸取图，直接 ToBitmap() 只有 32px，放大后会发虚
        int iconSide = S(40);
        Image iconImage;
        using (var sizedIcon = new Icon(appIcon, new Size(64, 64))) iconImage = sizedIcon.ToBitmap();
        var iconBox = new PictureBox
        {
            Image = iconImage,
            Size = new Size(iconSide, iconSide),
            SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point((width - iconSide) / 2, y)
        };
        controls.Add(iconBox);
        y += iconSide + S(6);

        // 2) 产品名
        int nameH = S(34);
        controls.Add(new Label
        {
            Text = "暖序",
            Font = new Font("Microsoft YaHei UI", 17, FontStyle.Bold),
            ForeColor = ink,
            AutoSize = false,
            Size = new Size(width, nameH),
            Location = new Point(0, y),
            TextAlign = ContentAlignment.MiddleCenter
        });
        y += nameH;

        // 3) 副标题
        int taglineH = S(20);
        controls.Add(new Label
        {
            Text = "把昼夜，还给眼睛",
            ForeColor = sub,
            AutoSize = false,
            Size = new Size(width, taglineH),
            Location = new Point(0, y),
            TextAlign = ContentAlignment.MiddleCenter
        });
        y += taglineH + S(8);

        // 4) 版本胶囊：从程序集读取，避免改了代码界面还显示旧号
        //    .NET 8 在有提交记录的仓库里构建时会把 InformationalVersion 拼成「1.0.0+e36」，
        //    e36 是短提交号。csproj 里已经关掉了这个追加，这里再兜一刀：
        //    万一换了构建方式又冒出来，也别让用户看到一串十六进制。
        var version = Application.ProductVersion;
        int plusAt = version.IndexOf('+');
        if (plusAt > 0) version = version.Substring(0, plusAt);
        int chipW = S(66), chipH = S(22);
        var chip = new Label
        {
            Text = "v" + version,
            Font = new Font("Microsoft YaHei UI", 8),
            ForeColor = Color.FromArgb(96, 110, 96),
            BackColor = Color.FromArgb(236, 239, 231),
            AutoSize = false,
            Size = new Size(chipW, chipH),
            Location = new Point((width - chipW) / 2, y),
            TextAlign = ContentAlignment.MiddleCenter,
            UseMnemonic = false
        };
        controls.Add(chip);
        y += chipH + S(14);

        int hairline = Math.Max(1, S(1));

        // 5) 分隔线
        controls.Add(new Panel { BackColor = line, Location = new Point(padX, y), Size = new Size(contentW, hairline) });
        y += hairline + S(14);

        // 6) 两列信息：标签右对齐、值左对齐，整块水平居中
        int rowH = S(22);
        int labelW = S(76), gapW = S(12), valueW = S(132);
        int blockX = (width - (labelW + gapW + valueW)) / 2;

        void AddRow(string label, string value)
        {
            controls.Add(new Label
            {
                Text = label,
                ForeColor = Color.FromArgb(138, 142, 133),
                AutoSize = false,
                Size = new Size(labelW, rowH),
                Location = new Point(blockX, y),
                TextAlign = ContentAlignment.MiddleRight,
                UseMnemonic = false
            });
            controls.Add(new Label
            {
                Text = value,
                ForeColor = Color.FromArgb(62, 74, 63),
                AutoSize = false,
                Size = new Size(valueW, rowH),
                Location = new Point(blockX + labelW + gapW, y),
                TextAlign = ContentAlignment.MiddleLeft,
                UseMnemonic = false
            });
            y += rowH;
        }

        AddRow("设计人员", "So先森");

        // 项目主页单独占一行居中：两列信息块的值列只有 132px，
        // 放不下 30 个字符的 URL（会被裁掉尾巴）。
        // 用 LinkLabel 而不是普通 Label，点一下直接开浏览器。
        int repoLinkH = S(20);
        var repoLink = new LinkLabel
        {
            Text = RepoUrl.Replace("https://", ""),
            AutoSize = false,
            Size = new Size(width, repoLinkH),
            Location = new Point(0, y),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft YaHei UI", 8),
            LinkColor = Color.FromArgb(96, 110, 96),
            ActiveLinkColor = Color.FromArgb(194, 121, 79),
            VisitedLinkColor = Color.FromArgb(96, 110, 96),
            LinkBehavior = LinkBehavior.HoverUnderline,
            UseMnemonic = false
        };
        repoLink.LinkClicked += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                // 没有默认浏览器、或被组策略拦掉时不能让关于窗口崩掉
                Debug.WriteLine(ex);
            }
        };
        controls.Add(repoLink);
        y += repoLinkH + S(10);

        // 7) 分隔线
        controls.Add(new Panel { BackColor = line, Location = new Point(padX, y), Size = new Size(contentW, hairline) });
        y += hairline + S(12);

        // 8) 赞赏码卡片
        int cardH = S(112), qrSide = S(96);
        var qrImage = LoadQrImage("donate-qr.png", "NuanXu.donate-qr.png");
        var card = new Panel
        {
            Location = new Point(padX, y),
            Size = new Size(contentW, cardH),
            BackColor = Color.White
        };
        int cardRadius = S(10);
        using (var regionPath = RoundedRect(new Rectangle(0, 0, card.Width, card.Height), cardRadius))
            card.Region = new Region(regionPath);
        card.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var borderPath = RoundedRect(new Rectangle(0, 0, card.Width - 1, card.Height - 1), cardRadius);
            using var pen = new Pen(line);
            e.Graphics.DrawPath(pen, borderPath);
        };

        // 二维码与文案作为一个整体在卡片内居中，避免左挤右空
        var fontTitle = new Font("Microsoft YaHei UI", 9);
        var fontNote = new Font("Microsoft YaHei UI", 8);
        // 测量用默认 flags（含 GDI 内边距）；NoPadding 会低估宽度，导致文案被裁掉末尾字符
        int Measure(string text, Font font) => TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue)).Width;
        int textW = Math.Max(Measure("微信扫码赞赏", fontTitle), Measure("感谢支持，随缘就好", fontNote)) + S(6);
        int gapX = S(14);
        int groupX = (contentW - (qrSide + gapX + textW)) / 2;

        var qrBox = new PictureBox
        {
            Size = new Size(qrSide, qrSide),
            Location = new Point(groupX, (cardH - qrSide) / 2),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.White,
            Image = qrImage
        };
        card.Controls.Add(qrBox);

        // 96px 在 100% 缩放的屏上，赞赏码每个模块只剩 3px 左右，手机不好扫。
        // 点一下弹大图，扫码就稳了；图缺失时不挂事件，避免点了没反应。
        if (qrImage is not null)
        {
            var clickable = qrImage;
            qrBox.Cursor = Cursors.Hand;
            qrBox.Click += (_, _) => ShowQrPreview(clickable);
        }

        if (qrImage is null)
        {
            // 找不到图时给个明确提示，而不是留一块空白。
            // 用一个自带浅灰底色的 Label，不再叠一层 Panel——WinForms 里 Controls.Add 是往集合末尾追加，
            // 后加的控件 z 序更靠后，叠 Panel 会把文字整个盖住（实测只看到灰块、看不到「二维码缺失」）。
            qrBox.Visible = false;
            card.Controls.Add(new Label
            {
                Text = "二维码缺失",
                ForeColor = Color.FromArgb(160, 164, 155),
                BackColor = Color.FromArgb(246, 247, 242),
                Font = fontNote,
                AutoSize = false,
                Size = new Size(qrSide, qrSide),
                Location = new Point(groupX, (cardH - qrSide) / 2),
                TextAlign = ContentAlignment.MiddleCenter,
                UseMnemonic = false
            });
        }

        int textX = groupX + qrSide + gapX;
        card.Controls.Add(new Label
        {
            Text = "微信扫码赞赏",
            Font = fontTitle,
            ForeColor = ink,
            AutoSize = false,
            Size = new Size(textW, S(20)),
            Location = new Point(textX, S(38)),
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false
        });
        card.Controls.Add(new Label
        {
            Text = "感谢支持，随缘就好",
            Font = fontNote,
            ForeColor = sub,
            AutoSize = false,
            Size = new Size(textW, S(18)),
            Location = new Point(textX, S(60)),
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false
        });

        controls.Add(card);
        y += cardH + S(12);

        // 9) 主按钮：居中，避免孤零零挂在右下角
        int btnW = S(96), btnH = S(32);
        var close = new Button
        {
            Text = "确定",
            DialogResult = DialogResult.OK,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(87, 116, 94),
            ForeColor = Color.White,
            Size = new Size(btnW, btnH),
            Location = new Point((width - btnW) / 2, y),
            UseVisualStyleBackColor = false
        };
        close.FlatAppearance.BorderSize = 0;
        controls.Add(close);
        y += btnH + S(16);

        about.ClientSize = new Size(width, y);
        about.FormClosed += (_, _) =>
        {
            iconBox.Image?.Dispose();
            qrImage?.Dispose();
        };
        about.Controls.AddRange(controls.ToArray());
        about.AcceptButton = close;
        return about;
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }
        int d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // 外部文件优先：把 donate-qr.png 放到 exe 同目录即可换码，不用重新编译
    private static Image? LoadQrImage(string fileName, string resourceName)
    {
        try
        {
            var external = Path.Combine(AppContext.BaseDirectory, fileName);
            if (File.Exists(external))
            {
                using var stream = new MemoryStream(File.ReadAllBytes(external));
                using var img = Image.FromStream(stream);
                return new Bitmap(img);   // 复制一份，避免流释放后图失效
            }
        }
        catch
        {
            // 外部图损坏时退回内置资源
        }

        try
        {
            using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream(resourceName);
            if (stream is not null)
            {
                using var img = Image.FromStream(stream);
                return new Bitmap(img);
            }
        }
        catch
        {
        }

        return null;
    }

    /// <summary>
    /// 点击赞赏码弹出大图。卡片里只有 96px，100% 缩放的屏幕上每个模块约 3px，手机很难对焦；
    /// 放大到 340px 后模块有 11px 左右，一扫就中。点任意处、按 Esc、或点到别处都会关掉。
    /// </summary>
    private static void ShowQrPreview(Image image)
    {
        float scale;
        using (var screen = Graphics.FromHwnd(IntPtr.Zero)) scale = screen.DpiX / 96f;
        int S(int value) => (int)Math.Round(value * scale);

        int side = S(340), pad = S(12), tipH = S(20);
        var frame = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.CenterScreen,
            BackColor = Color.White,
            ShowInTaskbar = false,
            TopMost = true,
            KeyPreview = true,
            ClientSize = new Size(side + pad * 2, side + pad * 2 + tipH)
        };

        var pic = new PictureBox
        {
            Location = new Point(pad, pad),
            Size = new Size(side, side),
            // 源图 600×600 缩到 340，用 Zoom 的高质量插值比 NearestNeighbor 更清楚
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.White,
            Image = image
        };
        frame.Controls.Add(pic);

        // 提示放在图片下方，不压在码上——压上去会挡扫
        var tip = new Label
        {
            Text = "点击任意处关闭",
            Font = new Font("Microsoft YaHei UI", 8),
            ForeColor = Color.FromArgb(150, 154, 146),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Size = new Size(side, tipH),
            Location = new Point(pad, pad + side),
            UseMnemonic = false
        };
        frame.Controls.Add(tip);

        pic.Click += (_, _) => frame.Close();
        tip.Click += (_, _) => frame.Close();
        frame.Click += (_, _) => frame.Close();
        frame.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) frame.Close(); };
        frame.Deactivate += (_, _) => frame.Close();

        try { frame.ShowDialog(); }
        finally { frame.Dispose(); }
    }

    // 启动画面只是锦上添花：任何异常都吞掉，绝不能让 LOGO 拖累主界面
    private void ShowSplash()
    {
        try
        {
            _splash = new SplashForm();
            _splash.ShowOver(this);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            _splash?.Dispose();
            _splash = null;
        }
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            await _browser.EnsureCoreWebView2Async();
            // 从托盘恢复后立刻退出（或安装器请退）时，continuation 可能在窗体已释放之后才跑到这里。
            // 再去碰 CoreWebView2 会抛 ObjectDisposedException，被兜底处理器接住后 Environment.Exit(1)，
            // 表现为「点一下托盘马上退出，进程退出码 1」。
            if (IsDisposed || _exitRequested) return;

            var settings = _browser.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            // 生产环境不该让用户（或任何页面脚本）打开 DevTools 直接驱动原生桥
            settings.AreDevToolsEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;   // 禁 F12 / Ctrl+P / Ctrl+F / Ctrl+R
            settings.IsZoomControlEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.IsWebMessageEnabled = true;                 // 桥接依赖它，必须保留

            _pageUri = new Uri(Path.Combine(AppContext.BaseDirectory, "index.html"));

            _browser.CoreWebView2.WebMessageReceived += HandleWebMessage;
            _browser.CoreWebView2.NavigationStarting += (_, e) =>
            {
                // 只允许本地页面。页面里万一出现外链、或被拖入一个 URL，
                // 不能让 WebView2 带着原生桥跑到外部站点上去
                if (IsFromLocalPage(e.Uri)) return;
                if (string.Equals(e.Uri, "about:blank", StringComparison.OrdinalIgnoreCase)) return;
                Debug.WriteLine($"已拦截导航：{e.Uri}");
                e.Cancel = true;
            };
            _browser.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess)
                {
                    // index.html 缺失 / 被杀软隔离 / 被策略拦截：不能装作没事，
                    // 否则启动画面正常退场、主窗口一片空白，用户完全不知道发生了什么
                    _splash?.ForceClose();
                    MessageBox.Show(this,
                        $"界面加载失败（{e.WebErrorStatus}）。\n\n请检查程序目录下的 index.html 是否完整，或重新安装暖序。",
                        "暖序启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    _exitRequested = true;
                    _tray.Visible = false;
                    Close();
                    return;
                }
                _webReady = true;
                AppLog.Write("界面加载完成");
                SendState();
                UpdateColor();
                // 界面就绪后再去拉天气：网络慢不能拖住窗口显示
                if (_weather is null) _ = RefreshWeatherAsync();
                // 页面已经画出来了，启动画面可以退场（它自己会保证至少显示满 1 秒）
                _splash?.MarkContentReady();
            };
            _browser.Source = _pageUri;
        }
        catch (Exception ex)
        {
            // 已经进入退出流程 / 窗体已释放：这时候再弹错误框本身就会抛（父窗体没了），
            // 会盖掉真正的原因，还让退出码变成 1
            if (IsDisposed || _exitRequested) return;
            AppLog.Error("WebView2 初始化失败", ex);
            // 先撤掉启动画面，否则错误框会被它压在下面
            _splash?.ForceClose();
            // 缺运行时是全新机器上最常见的一类启动失败，必须直接给下载地址，
            // 否则用户只看到一句英文异常，不知道要去装什么
            MessageBox.Show(this,
                "暖序需要 Microsoft Edge WebView2 运行时才能显示界面。\n\n" +
                "请先安装它（微软官方，免费），装好后重新打开暖序：\n" +
                "https://go.microsoft.com/fwlink/p/?LinkId=2124703\n\n" +
                "技术信息：" + ex.Message,
                "暖序启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _exitRequested = true;
            _tray.Visible = false;
            Close();
        }
    }

    private void HandleWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // 只接受我们自己加载的那个页面发来的消息。
        // 原生桥本身只暴露改设置的命令，影响有限，但没理由留这个口子。
        if (!IsFromLocalPage(e.Source))
        {
            LogBridgeDropOnce(e.Source);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var command = root.GetProperty("command").GetString();
            switch (command)
            {
                case "toggle-light":
                    // 护眼总开关：与菜单、托盘走同一个入口，避免状态各说各话
                    SetEyeCare(root.GetProperty("enabled").GetBoolean());
                    break;
                case "follow-sun":
                    SetFollowSun(root.GetProperty("enabled").GetBoolean());
                    break;
                case "warmth":
                    // 跟随日光时暖度由曲线决定，界面上滑块是锁定的，这条消息不该出现。
                    // 但旧页面缓存、或别的入口误发时仍然可能到这儿——直接忽略并回推状态，
                    // 免得把「固定暖度」写进日光模式（1.0.0 的双语义 bug 就是这么来的）。
                    if (_settings.FollowSun)
                    {
                        SendState();
                        break;
                    }
                    _settings.WarmthStrength = Math.Clamp(root.GetProperty("value").GetInt32(), 0, 100);
                    SaveSettings();
                    // 护眼关闭时绝不碰 LUT——那是别人的（系统夜间模式）值
                    if (_settings.Enabled) UpdateColor(force: true);
                    else SendState();
                    break;
                case "reminder-settings":
                    _settings.ReminderMinutes = Math.Clamp((int)Math.Round(root.GetProperty("minutes").GetDouble()), 1, 180);
                    _settings.ReminderText = NormalizeReminderText(root.GetProperty("text").GetString());
                    // 只更新间隔和文案，不改启用状态：暂停中调整间隔不应把提醒偷偷重新打开
                    if (_settings.RemindersEnabled)
                        _nextReminder = DateTime.Now.AddMinutes(_settings.ReminderMinutes);
                    else
                    {
                        // 暂停中改间隔：保留已冻结的剩余时间，只按新间隔封顶，避免"改一下间隔就把冻结值冲掉"
                        var cap = TimeSpan.FromMinutes(_settings.ReminderMinutes);
                        _remainingOnPause = _remainingOnPause > TimeSpan.Zero && _remainingOnPause < cap ? _remainingOnPause : cap;
                    }
                    SaveSettings();
                    SendState();
                    break;
                case "reminder-enabled":
                    // 复用统一入口，保证「暂停冻结 / 恢复续走」与菜单、托盘、弹窗完全一致
                    SetRemindersEnabled(root.GetProperty("enabled").GetBoolean());
                    break;
                case "reminder-mode":
                    // 只改「循环 / 仅一次」，不动当前这一轮的计时：
                    // 循环改单次时，正在等的那一次就是最后一次，弹完自然收工。
                    _settings.ReminderRepeat = root.GetProperty("repeat").GetBoolean();
                    // 「已完成」只对单次模式有意义。切到循环后提醒是关的，
                    // 继续报「已完成」会和模式高亮（循环）自相矛盾；清掉后落到正常的「已暂停」。
                    // 切回「仅一次」不清：那时"已弹过"仍是事实，且提醒本来就没开。
                    if (_settings.ReminderRepeat) _onceCompleted = false;
                    SaveSettings();
                    SendState();
                    break;
                case "app-ready":
                    // 页面脚本就绪的握手：立刻推一帧状态，别等定时器兜底
                    SendState();
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    /// <summary>
    /// 判断消息来源是不是我们自己的页面。
    /// 先按完整 URI 比，再按本地路径比——WebView2 在不同版本里对中文路径、空格的转义形式
    /// 可能和 Uri.AbsoluteUri 不一致，只做字符串相等会误伤正常消息。
    /// </summary>
    private bool IsFromLocalPage(string? source)
    {
        if (_pageUri is null || string.IsNullOrEmpty(source)) return false;
        if (string.Equals(source, _pageUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            var uri = new Uri(source);
            return uri.IsFile && string.Equals(uri.LocalPath, _pageUri.LocalPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// 第一次丢弃消息时落一行日志。如果来源校验写错了，这里会立刻出现内容，
    /// 而不是让用户面对「拖滑块没反应」这种查不出来的现象。
    /// </summary>
    private void LogBridgeDropOnce(string? source)
    {
        Debug.WriteLine($"丢弃来源不明的消息：{source}");
        if (_bridgeDropLogged) return;
        _bridgeDropLogged = true;
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(_settingsPath)!, "bridge.log");
            File.WriteAllText(path, $"dropped={source}{Environment.NewLine}expected={_pageUri?.AbsoluteUri}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void SendState()
    {
        // IsDisposed 必须排在 _browser.CoreWebView2 之前：控件释放后再取这个属性会抛，
        // 而本方法会被定时器和事件回调反复调用，退出瞬间很容易撞上
        if (!_webReady || _exitRequested || IsDisposed || _browser.CoreWebView2 is null) return;
        var now = DateTime.Now;
        var (sunrise, sunset) = SolarState(now);
        var skippedToday = _skipUntil is { } until && now < until;
        var json = JsonSerializer.Serialize(new
        {
            command = "native-state",
            enabled = _settings.Enabled,
            followSun = _settings.FollowSun,
            remindersEnabled = _settings.RemindersEnabled,
            reminderRepeat = _settings.ReminderRepeat,
            onceCompleted = _onceCompleted,
            minutes = _settings.ReminderMinutes,
            text = _settings.ReminderText,
            // 推的是「当前真实暖度」而不是设置里的固定值：
            // 日光模式下这两个数不一样，前端滑块是只读指示器，必须拿到真实值才不会骗人
            warmth = ComputeWarmth(now, (sunrise, sunset)),
            skippedToday,
            nextReminderSeconds = Math.Max(0, (int)Math.Ceiling((_nextReminder - now).TotalSeconds)),
            sunrise = sunrise.ToString(@"hh\:mm"),
            sunset = sunset.ToString(@"hh\:mm"),
            city = _settings.City,
            weather = _weather is null ? null : new
            {
                ok = _weather.Ok,
                days = _weather.Days.Select(d => new { date = d.Date, code = d.Code, max = d.Max, min = d.Min }).ToArray()
            }
        });
        try
        {
            _browser.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            // WebView2 正在销毁 / 渲染进程已崩溃时会抛 COMException 或 ObjectDisposedException。
            // 这里在 Timer 回调里，不接住就会弹「未处理异常」对话框，用户点退出又会绕过伽马复位。
            Debug.WriteLine(ex);
        }
    }

    /// <summary>
    /// 拉取未来几天天气预报。数据源是 Open-Meteo 的免费接口，不需要 Key。
    /// 任何异常都只让卡片退化成"取不到"，绝不冒泡影响色温、提醒等主功能。
    /// </summary>
    private async Task RefreshWeatherAsync()
    {
        // 代次校验：切城市时上一个城市的请求可能还在飞行中（Timeout 10 秒，窗口很大），
        // 它的回包会把新城市的天气覆盖掉，最长错一小时。
        var requestId = ++_weatherRequestId;
        var requestCity = _settings.City;
        try
        {
            var city = CityCatalog.Resolve(requestCity);
            var url = "https://api.open-meteo.com/v1/forecast"
                    + "?latitude=" + city.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "&longitude=" + city.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "&daily=weather_code,temperature_2m_max,temperature_2m_min"
                    + "&timezone=Asia%2FShanghai&forecast_days=4";
            var payload = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("daily", out var daily)) throw new InvalidDataException("daily 缺失");

            var dates = daily.GetProperty("time");
            var codes = daily.GetProperty("weather_code");
            var maxs = daily.GetProperty("temperature_2m_max");
            var mins = daily.GetProperty("temperature_2m_min");

            var count = Math.Min(dates.GetArrayLength(), Math.Min(codes.GetArrayLength(), Math.Min(maxs.GetArrayLength(), mins.GetArrayLength())));
            var days = new List<WeatherDay>(count);
            for (var i = 0; i < count; i++)
            {
                // Open-Meteo 偶发某天缺测返回 null，逐项兜底，别让一条坏数据毁掉整张卡
                if (!dates[i].TryGetDateTime(out var date)) continue;
                if (!codes[i].TryGetInt32(out var code)) continue;
                if (!maxs[i].TryGetDouble(out var max)) continue;
                if (!mins[i].TryGetDouble(out var min)) continue;

                days.Add(new WeatherDay
                {
                    Date = date.ToString("yyyy-MM-dd"),
                    Code = code,
                    // 四舍五入成整数：卡片位置窄，小数只会挤成一团
                    Max = (int)Math.Round(max),
                    Min = (int)Math.Round(min)
                });
            }
            if (days.Count == 0) throw new InvalidDataException("预报为空");

            // 已经有更新的请求在路上 / 城市已经被切走：这份结果直接丢弃，连刷新间隔都不要改
            if (requestId != _weatherRequestId || requestCity != _settings.City) return;

            _weather = new WeatherSnapshot { Ok = true, Days = days.ToArray(), FetchedAt = DateTime.Now };
            _weatherFailureLogged = false;
            // 成功就回到一小时一刷
            _weatherClock.Interval = 60 * 60 * 1000;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            if (requestId != _weatherRequestId || requestCity != _settings.City) return;
            // 一次失败只记一行：断网时每 10 分钟重试一次，每次都记会把日志刷满
            if (!_weatherFailureLogged)
            {
                _weatherFailureLogged = true;
                AppLog.Write($"[天气] 请求失败（{requestCity}）：{ex.GetType().Name}: {ex.Message}");
            }
            // 失败后 10 分钟就重试，别让用户等一小时才看到天气
            _weatherClock.Interval = 10 * 60 * 1000;
            // 之前成功过就保留旧数据，失败一次不至于让卡片空掉
            if (_weather is null || _weather.Days.Length == 0)
                _weather = new WeatherSnapshot { Ok = false, FetchedAt = DateTime.MinValue };
        }
        SendState();
    }

    private void UpdateColor(bool force = false)
    {
        if (!_settings.Enabled)
        {
            // 关键：只有我们写过才还原，而且只在这一瞬间写一次。
            // 1.7.x 的做法是每 30 秒无条件 ApplyWarmth(0)，会把 Windows「夜间模式」
            // 写进同一张硬件 LUT 的值反复冲掉，表现为夜间模式时有时无、反复跳变。
            if (_rampWritten)
            {
                _displays.RestoreAll();
                _rampWritten = false;
            }
            _lastAppliedWarmth = -1;
            SendState();
            return;
        }

        var warmth = ComputeWarmth();
        var ramp = DisplayManager.BuildWarmRamp(warmth);
        // 暖度没变就直接返回：白天日照曲线平缓时几乎不产生写入。
        // 注意：这里「不重写」是有意的，别改成定期回读、发现被覆盖就夺回。
        // 实测过——屏幕上的暖色被抹回原色，元凶是 f.lux 这类色温软件在抢写
        // 同一张硬件 LUT（关掉 f.lux 后暖色 16 秒纹丝不动）。若我们每 30 秒
        // 回读并夺回，两边就会周期性互斗，用户看到的是色温来回跳，比让步更糟。
        // 全屏游戏、驱动重置丢 LUT 的场景另由 DisplaySettingsChanged /
        // PowerModeChanged.Resume 的 DebouncedReapply 负责，不靠这里兜底。
        if (!force && warmth == _lastAppliedWarmth)
        {
            SendState();
            return;
        }

        var outcome = _displays.Apply(ramp);
        if (outcome.AnyApplied)
        {
            _rampWritten = true;
            _lastAppliedWarmth = warmth;
        }
        if (outcome.AllFailed && !_gammaWarningShown)
        {
            _gammaWarningShown = true;
            // 记日志：用户报「不生效」时，先看是驱动拒绝、HDR 还是虚拟显示器
            AppLog.Write($"[色温] 所有显示器写入失败，首个={outcome.FirstFailed}");
            _tray.ShowBalloonTip(5000, "暖序无法调整当前显示器",
                "当前显示驱动或 HDR 模式可能不支持色温调节。可尝试关闭 HDR 或检查显示器设置。",
                ToolTipIcon.Warning);
        }

        // 收进托盘时页面不可见，没必要每 30 秒序列化一次完整 state 往看不见的页面里塞。
        // 隐藏态的同步由 MaybeSendCountdown 的 30 秒分支负责；恢复窗口时
        // RestoreWindow 会立刻补推一次，所以不会出现"回来看到 30 秒前的倒计时"。
        if (Visible) SendState();
    }

    /// <summary>
    /// 日光曲线的固定增益。曲线本身是 17~72，乘这个系数后夜间 47、白天 11——
    /// 这是这款软件一直以来的默认观感，必须保持不变。
    /// 它是内部标定值，**不暴露给界面**：滑块在日光模式下是锁定的，
    /// 1.0.0 那种「滑块值当曲线倍率」的写法会让用户误拖出来的 17 把整条曲线压到 3%。
    /// </summary>
    private const double AutoWarmthGain = 0.65;

    /// <summary>
    /// 当前应该用的暖度。跟随日光时只由日照曲线决定，否则就是滑块的固定值。
    /// 两种模式语义互斥：日光模式不接受任何手动强度，滑块在那里只负责显示。
    /// </summary>
    private int ComputeWarmth()
    {
        var now = DateTime.Now;
        return ComputeWarmth(now, SolarState(now));
    }

    /// <summary>
    /// 带上下文的重载：SendState 每秒都要推一次状态，日出日落它已经算过，
    /// 复用同一份结果，避免每秒重复跑一遍 NOAA 公式。
    /// </summary>
    private int ComputeWarmth(DateTime now, (TimeSpan sunrise, TimeSpan sunset) solar)
    {
        if (!_settings.FollowSun) return Math.Clamp(_settings.WarmthStrength, 0, 100);

        var hour = now.TimeOfDay.TotalHours;
        var rise = solar.sunrise.TotalHours;
        var set = solar.sunset.TotalHours;
        double warmth;
        if (hour < rise - 1 || hour >= set + 1) warmth = 72;
        else if (hour < rise + 2) warmth = 72 - Smooth((hour - (rise - 1)) / 3) * 55;
        else if (hour < set - 1.5) warmth = 17;
        else warmth = 17 + Smooth((hour - (set - 1.5)) / 2.5) * 55;
        return (int)Math.Round(warmth * AutoWarmthGain);
    }

    private void TickReminder()
    {
        var now = DateTime.Now;

        if (_skipUntil is { } until)
        {
            if (now < until)
            {
                MaybeSendCountdown(now);
                return;
            }
            // 跳过到期：从此刻重新起一整轮，而不是在 00:00 立刻弹一次
            _skipUntil = null;
            _settings.SkipUntil = null;
            _nextReminder = now.AddMinutes(_settings.ReminderMinutes);
            SaveSettings();
        }

        // !_onceCompleted 是双保险：完成时开关已经置 false，但万一将来某条路径漏了，
        // 单次模式也不会因为 _nextReminder 一直过期而反复弹窗。
        if (!_reminderDialogOpen && _settings.RemindersEnabled && !_onceCompleted && now >= _nextReminder)
        {
            // 投屏做汇报、全屏放 PPT 时弹一个置顶窗口出来是灾难（弹窗是 TopMost + ShowDialog，
            // 会直接抢走焦点）。这类场景不弹，顺延一整轮——退出全屏后下一轮照常提醒。
            if (IsFullScreenOrPresenting())
            {
                _nextReminder = now.AddMinutes(_settings.ReminderMinutes);
                AppLog.Write("[提醒] 检测到全屏/演示模式，本次提醒顺延");
            }
            else ShowRestReminder();
        }
        MaybeSendCountdown(now);
    }

    // SHQueryUserNotificationState 的返回值（shellapi.h）
    private const int QunsBusy = 2;                   // 全屏应用（含演示设置）
    private const int QunsRunningD3DFullScreen = 3;   // D3D 全屏（游戏/播放器）
    private const int QunsPresentationMode = 4;       // 演示模式
    private const int QunsApp = 7;                    // 全屏 UWP/商店应用

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    /// <summary>
    /// 当前是不是全屏 / 演示中。Windows 自己就知道（它用这个值决定要不要弹通知），
    /// 不用我们自己去猜窗口尺寸。查不到就当作「不是」——宁可多弹一次，也别把提醒吞掉。
    /// </summary>
    private static bool IsFullScreenOrPresenting()
    {
        try
        {
            if (SHQueryUserNotificationState(out var state) != 0) return false;
            return state is QunsBusy or QunsRunningD3DFullScreen or QunsPresentationMode or QunsApp;
        }
        catch (Exception ex)
        {
            AppLog.Error("SHQueryUserNotificationState", ex);
            return false;
        }
    }

    /// <summary>
    /// 倒计时推送：窗口可见时按秒推（数字在变），收进托盘后降到 30 秒一次——
    /// 托盘态没人看数字，每秒一次 PostWebMessageAsJson 只会白白耗电，
    /// 还会阻止 WebView2 渲染进程进入节流状态。
    /// </summary>
    private void MaybeSendCountdown(DateTime now)
    {
        var seconds = Math.Max(0, (int)Math.Ceiling((_nextReminder - now).TotalSeconds));
        if (Visible)
        {
            if (seconds != _lastSentSeconds)
            {
                _lastSentSeconds = seconds;
                SendState();
            }
        }
        else if ((now - _lastHiddenSend).TotalSeconds >= 30)
        {
            _lastHiddenSend = now;
            _lastSentSeconds = seconds;
            SendState();
        }
    }

    // 日出日落跟着当前地区走：同样是北京时间，乌鲁木齐的日出比上海晚两个多小时
    private (TimeSpan sunrise, TimeSpan sunset) SolarState(DateTime date)
    {
        var city = CityCatalog.Resolve(_settings.City);
        return SunTimes(date, city.Latitude, city.Longitude);
    }

    private static double Smooth(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value * (3 - 2 * value);
    }

    private static (TimeSpan sunrise, TimeSpan sunset) SunTimes(DateTime date, double latitude, double longitude)
    {
        var day = date.DayOfYear;
        var gamma = 2 * Math.PI / 365 * (day - 1);
        var equation = 229.18 * (0.000075 + 0.001868 * Math.Cos(gamma) - 0.032077 * Math.Sin(gamma) - 0.014615 * Math.Cos(2 * gamma) - 0.040849 * Math.Sin(2 * gamma));
        var declination = 0.006918 - 0.399912 * Math.Cos(gamma) + 0.070257 * Math.Sin(gamma) - 0.006758 * Math.Cos(2 * gamma) + 0.000907 * Math.Sin(2 * gamma) - 0.002697 * Math.Cos(3 * gamma) + 0.00148 * Math.Sin(3 * gamma);
        var lat = latitude * Math.PI / 180;
        var cosHourAngle = Math.Cos(90.833 * Math.PI / 180) / (Math.Cos(lat) * Math.Cos(declination)) - Math.Tan(lat) * Math.Tan(declination);
        var hourAngle = Math.Acos(Math.Clamp(cosHourAngle, -1, 1)) * 180 / Math.PI;
        var noon = 720 - 4 * longitude - equation + 480;
        return (TimeSpan.FromMinutes(noon - 4 * hourAngle), TimeSpan.FromMinutes(noon + 4 * hourAngle));
    }

    private void ShowRestReminder()
    {
        if (_reminderDialogOpen) return;
        _reminderDialogOpen = true;
        var result = DialogResult.Cancel;
        try
        {
            result = ShowRestReminderCore();
        }
        finally
        {
            // 必须放 finally：中途抛异常时标志若永久为 true，休息提醒从此再也不弹
            _reminderDialogOpen = false;
        }

        // 单次模式下「休息好了 / 今天跳过 / 直接关掉」都等于收工；
        // 「5 分钟后」不算收工——那一次还没真正提醒到，继续等 5 分钟再弹。
        var repeat = _settings.ReminderRepeat;

        if (result == DialogResult.OK)
        {
            if (repeat) _nextReminder = DateTime.Now.AddMinutes(_settings.ReminderMinutes);
            else FinishOnceReminder();
        }
        else if (result == DialogResult.Retry)
            _nextReminder = DateTime.Now.AddMinutes(5);
        else if (result == DialogResult.Ignore)
        {
            if (repeat)
            {
                // 真正的「今天跳过」：只跳过今天，不动提醒开关。
                // 1.7.x 这里调的是 SetRemindersEnabled(false)，等于把提醒永久关掉，和文案完全不符。
                _skipUntil = DateTime.Today.AddDays(1);
                _settings.SkipUntil = _skipUntil;
                SaveSettings();
            }
            else
            {
                // 单次模式下本来就只有一次，跳过 = 取消
                FinishOnceReminder();
            }
        }
        else
        {
            if (repeat) _nextReminder = DateTime.Now.AddMinutes(_settings.ReminderMinutes);
            else FinishOnceReminder();
        }

        SendState();
    }

    private DialogResult ShowRestReminderCore()
    {
        // 按钮文字精简，并按实际字体测量宽度：任何 DPI 下都不会被截断
        const string doneText = "休息好了";
        const string snoozeText = "5 分钟后";
        const string skipText = "今天跳过";
        const int buttonPad = 26, gap = 9, margin = 28;

        using var buttonFont = new Font("Microsoft YaHei UI", 10);
        int Measure(string text) => TextRenderer.MeasureText(text, buttonFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width + buttonPad;

        var wDone = Measure(doneText);
        var wSnooze = Measure(snoozeText);
        var wSkip = Measure(skipText);
        var clientWidth = Math.Max(470, wSnooze + wSkip + wDone + gap * 2 + margin * 2);

        // 副标题必须跟模式一致：单次模式下还写「每 N 分钟提醒一次」就是在骗人。
        // 单次文案比循环文案长，AutoSize 的 Label 撑出客户区就会被裁，所以宽度也要参与计算。
        // 按项目约定：MeasureText 不加 NoPadding（会低估），再留 6px 余量。
        // ⚠ 必须放在创建 dialog 之前——message 与「休息好了」按钮的坐标都依赖 clientWidth。
        var hintText = _settings.ReminderRepeat
            ? $"每 {_settings.ReminderMinutes} 分钟提醒一次"
            : "只提醒这一次，关闭后自动停用";
        using (var hintFont = new Font("Microsoft YaHei UI", 9))
        {
            var hintNeed = TextRenderer.MeasureText(hintText, hintFont, new Size(int.MaxValue, int.MaxValue)).Width + 6;
            clientWidth = Math.Max(clientWidth, hintNeed + margin * 2);
        }

        using var dialog = new Form
        {
            Text = "休息一下 · 暖序",
            ClientSize = new Size(clientWidth, 278),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false,
            TopMost = true,
            BackColor = Color.FromArgb(250, 250, 247),
            Font = new Font("Microsoft YaHei UI", 10),
            Icon = _appIcon
        };
        var title = new Label { Text = "给眼睛一点留白", Font = new Font("Segoe UI Semibold", 16), ForeColor = Color.FromArgb(40, 47, 40), AutoSize = true, Location = new Point(margin - 2, 24) };
        var message = new Label { Text = _settings.ReminderText, Font = new Font("Microsoft YaHei UI", 11), ForeColor = Color.FromArgb(97, 103, 94), Location = new Point(margin, 72), Size = new Size(clientWidth - margin * 2, 70), UseMnemonic = false, AutoEllipsis = true };
        // 副标题必须跟模式一致：单次模式下还写「每 N 分钟提醒一次」就是在骗人
        var hint = new Label { Text = hintText, Font = new Font("Microsoft YaHei UI", 9), ForeColor = Color.FromArgb(158, 161, 152), Location = new Point(margin, 150), AutoSize = true };

        var buttonTop = 196;
        var buttonHeight = 40;
        var skip = new Button { Text = skipText, Location = new Point(margin, buttonTop), Size = new Size(wSkip, buttonHeight), FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Color.FromArgb(73, 82, 72), DialogResult = DialogResult.Ignore };
        skip.FlatAppearance.BorderColor = Color.FromArgb(226, 227, 220);
        var snooze = new Button { Text = snoozeText, Location = new Point(skip.Right + gap, buttonTop), Size = new Size(wSnooze, buttonHeight), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(238, 240, 234), ForeColor = Color.FromArgb(73, 82, 72), DialogResult = DialogResult.Retry };
        snooze.FlatAppearance.BorderSize = 0;
        var done = new Button { Text = doneText, Location = new Point(clientWidth - margin - wDone, buttonTop), Size = new Size(wDone, buttonHeight), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(87, 116, 94), ForeColor = Color.White, DialogResult = DialogResult.OK };
        done.FlatAppearance.BorderSize = 0;
        dialog.Controls.AddRange([title, message, hint, skip, snooze, done]);
        dialog.AcceptButton = done;
        dialog.CancelButton = snooze;

        // 置顶窗口不会自动抢焦点，这里主动激活一次，让它像闹钟一样出现在眼前
        dialog.Shown += (_, _) => { dialog.Activate(); dialog.BringToFront(); };

        DialogResult result;
        if (_settings.ReminderSound) PlayReminderChime();
        try
        {
            result = dialog.ShowDialog(Visible ? this : null);
        }
        finally
        {
            StopReminderChime();
        }
        return result;
    }

    private void PlayReminderChime()
    {
        try
        {
            _reminderChime ??= new SoundPlayer(BuildReminderChime());
            _reminderChime.PlayLooping();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void StopReminderChime()
    {
        try
        {
            _reminderChime?.Stop();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    // 现场合成一段三音上行的提示音（WAV 内存流），不依赖任何外部音频文件
    private static Stream BuildReminderChime()
    {
        const int rate = 44100;
        const double cycleSeconds = 2.4;
        var total = (int)(rate * cycleSeconds);
        var samples = new short[total];

        // (起始秒, 时长秒, 频率Hz)：A5 → C#6 → E6，清亮但不刺耳
        (double Start, double Duration, double Freq)[] notes =
        [
            (0.00, 0.34, 880.00),
            (0.40, 0.34, 1108.73),
            (0.80, 0.55, 1318.51)
        ];

        foreach (var note in notes)
        {
            var offset = (int)(note.Start * rate);
            var length = (int)(note.Duration * rate);
            for (var i = 0; i < length && offset + i < total; i++)
            {
                var t = (double)i / rate;
                var attack = Math.Min(1.0, t / 0.012);           // 12ms 起音，避免爆音
                var decay = Math.Exp(-3.2 * t / note.Duration);   // 指数衰减，听感更像敲击
                var wave = Math.Sin(2 * Math.PI * note.Freq * t) * 0.72
                         + Math.Sin(2 * Math.PI * note.Freq * 2 * t) * 0.18;   // 一点二次谐波
                var value = samples[offset + i] + (int)(wave * attack * decay * 0.50 * short.MaxValue);
                samples[offset + i] = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
            }
        }

        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            var dataBytes = samples.Length * 2;
            writer.Write("RIFF"u8);
            writer.Write(36 + dataBytes);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);      // PCM
            writer.Write((short)1);      // 单声道
            writer.Write(rate);
            writer.Write(rate * 2);      // 每秒字节数
            writer.Write((short)2);      // 块对齐
            writer.Write((short)16);     // 位深
            writer.Write("data"u8);
            writer.Write(dataBytes);
            foreach (var sample in samples) writer.Write(sample);
        }
        stream.Position = 0;
        return stream;
    }

    private void ConfigureTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开暖序", null, (_, _) => RestoreWindow());
        _trayEyeCareMenu = new ToolStripMenuItem("护眼模式")
        {
            CheckOnClick = true,
            Checked = _settings.Enabled,
            ShortcutKeyDisplayString = HotKeys.EyeCareText
        };
        _trayEyeCareMenu.Click += (_, _) => SetEyeCare(_trayEyeCareMenu!.Checked);
        menu.Items.Add(_trayEyeCareMenu);
        _trayFollowSunMenu = new ToolStripMenuItem("跟随日光")
        {
            CheckOnClick = true,
            Checked = _settings.FollowSun,
            ShortcutKeyDisplayString = HotKeys.FollowSunText
        };
        _trayFollowSunMenu.Click += (_, _) => SetFollowSun(_trayFollowSunMenu!.Checked);
        menu.Items.Add(_trayFollowSunMenu);
        _trayPauseMenu = new ToolStripMenuItem("暂停 30 分钟", null, (_, _) => TogglePause())
        {
            ShortcutKeyDisplayString = HotKeys.PauseText
        };
        menu.Items.Add(_trayPauseMenu);
        _trayRemindersMenu = new ToolStripMenuItem("休息提醒") { CheckOnClick = true, Checked = _settings.RemindersEnabled };
        _trayRemindersMenu.Click += (_, _) => SetRemindersEnabled(_trayRemindersMenu.Checked);
        menu.Items.Add(_trayRemindersMenu);
        menu.Items.Add(new ToolStripSeparator());
        _trayAutoStartMenu = new ToolStripMenuItem("开机自动启动") { CheckOnClick = true, Checked = AutoStart.IsEnabled() };
        _trayAutoStartMenu.Click += (_, _) => SetAutoStart(_trayAutoStartMenu!.Checked);
        menu.Items.Add(_trayAutoStartMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(BuildCityMenu());
        menu.Items.Add("关于暖序", null, (_, _) => ShowAboutDialog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出暖序", null, (_, _) => ExitApplication());
        _tray.Icon = _appIcon;
        _tray.Text = "暖序";
        UpdateTrayText();   // 跟随当前城市，不要写死
        _tray.ContextMenuStrip = menu;
        menu.Opening += (_, _) =>
        {
            SyncAutoStartMenus();
            SyncPauseMenus();
        };
        _tray.DoubleClick += (_, _) => RestoreWindow();
        _tray.Visible = true;
        // 开机自启是静默进托盘的，这时候弹「设置已恢复」只会让人一头雾水
        if (_settingsRestoredFromBackup && !_startHidden)
        {
            _settingsRestoredFromBackup = false;
            _tray.ShowBalloonTip(4000, "暖序", "设置文件损坏，已从备份恢复。", ToolTipIcon.Warning);
        }
        SyncToggleMenus();
        SyncPauseMenus();
    }

    private void RestoreWindow()
    {
        WindowState = _settings.StartMaximized ? FormWindowState.Maximized : FormWindowState.Normal;
        Show();
        Activate();
        // 隐藏期间 UpdateColor 不再推送 state（页面看不见，推了也没人用），
        // 所以从托盘回来的第一件事就是把最新状态补上，否则倒计时会停在旧值
        SendState();
    }

    private void SaveWindowState()
    {
        if (WindowState == FormWindowState.Maximized)
        {
            _settings.StartMaximized = true;
        }
        else if (WindowState == FormWindowState.Normal)
        {
            _settings.StartMaximized = false;
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }
        SaveSettings();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing or CloseReason.ApplicationExitCall)
        {
            _exitRequested = true;
            _tray.Visible = false;
            _displays.RestoreAll();
            return;
        }
        if (_exitRequested)
        {
            _displays.RestoreAll();
            return;
        }
        SaveWindowState();
        e.Cancel = true;
        Hide();
        _tray.ShowBalloonTip(1200, "暖序仍在运行", "点击托盘图标可重新打开设置。", ToolTipIcon.Info);
    }

    private void LoadSettings()
    {
        // 主文件坏了就退回 .bak，两个都坏才回默认——而不是像 1.7.x 那样一声不响把设置全清掉
        foreach (var path in new[] { _settingsPath, _settingsPath + ".bak" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var loaded = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path));
                if (loaded is null) continue;
                _settings = loaded;
                _settingsRestoredFromBackup = path != _settingsPath;
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }
        _settings = new();
    }

    /// <summary>读到的设置统一做一次收敛：字段缺失、越界、城市失效都在这里兜住。</summary>
    private void NormalizeSettings()
    {
        _settings.ReminderMinutes = Math.Clamp(_settings.ReminderMinutes, 1, 180);
        _settings.WarmthStrength = Math.Clamp(_settings.WarmthStrength, 0, 100);
        // 旧版本允许 160 字，读取时统一收敛到新上限，避免弹窗里挤成一团
        _settings.ReminderText = NormalizeReminderText(_settings.ReminderText);
        // 城市名失效（配置被改坏或来自更老的版本）时回落到默认城市
        _settings.City = CityCatalog.Resolve(_settings.City).Name;
        // 一次性迁移：旧版默认最大化，而 SaveWindowState 会把「当前是最大化」写进配置，
        // 于是老用户的配置里都是 StartMaximized=true，看不出是他自己拉大的还是默认值。
        // 统一按「没选过」处理，清成内容自适应尺寸。
        if (!_settings.WindowFitContent)
        {
            _settings.WindowFitContent = true;
            _settings.StartMaximized = false;
            _settings.WindowWidth = 0;
            _settings.WindowHeight = 0;
            _settingsMigrated = true;
        }
        _skipUntil = _settings.SkipUntil;
    }

    /// <summary>
    /// 提醒文案的统一入口：去空白、空则回默认、超长则按上限截断。
    /// 前端 maxlength 只约束新输入，历史配置和 IPC 都需要在这里兜底。
    /// 截断必须按「文本元素」而不是 UTF-16 code unit ——
    /// <c>value[..40]</c> 在第 40 位正好是 emoji 的高代理时，
    /// 会留下半个代理对，界面上显示成方块。
    /// </summary>
    private static string NormalizeReminderText(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0) return "抬头看看远处，让眼睛放松一下。";

        // .NET 5+ 的 StringInfo 按扩展字素簇切分，代理对与组合序列都不会被切开
        var info = new StringInfo(value);
        return info.LengthInTextElements <= MaxReminderTextLength
            ? value
            : info.SubstringByTextElements(0, MaxReminderTextLength);
    }

    private void SaveSettings()
    {
        try
        {
            // 原子写：先写 .tmp 再替换，断电/崩溃不会留下半截 JSON
            AtomicFile.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings));
        }
        catch (Exception ex)
        {
            // 磁盘满、目录被占、杀软短暂锁文件都会走到这里。
            // 绝不能冒泡——菜单点击里抛出去就是「点一下护眼模式程序就崩」。
            Debug.WriteLine(ex);
        }
    }
}
