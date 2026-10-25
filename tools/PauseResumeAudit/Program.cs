using System;

namespace PauseResumeAudit;

public sealed class Settings
{
    public bool RemindersEnabled { get; set; } = true;
    public int ReminderMinutes { get; set; } = 30;
}

public sealed class FakeMenu { public bool Checked { get; set; } }

public sealed class Shell
{
    public Settings _settings = new();
    public DateTime _nextReminder;
    public TimeSpan _remainingOnPause;
    // 1.8.3：单次提醒已完成标记，SetRemindersEnabled(true) 里会清掉
    public bool _onceCompleted;
    public FakeMenu _remindersMenu;
    public FakeMenu _trayRemindersMenu;
    public int SaveCount;
    public int SendCount;

    public void SaveSettings() => SaveCount++;
    public void SendState() => SendCount++;

    private void SetRemindersEnabled(bool enabled)
    {
        if (_remindersMenu is not null) _remindersMenu.Checked = enabled;
        if (_trayRemindersMenu is not null) _trayRemindersMenu.Checked = enabled;

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
            // 1.8.3：用户主动开启（含单次完成后的「再来一次」）要清掉完成态
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
        }

        SaveSettings();
        SendState();
    }

    // 测试转发：方法体保持原文不动，仅借同类作用域调用 private 成员
    public void Invoke(bool enabled) => SetRemindersEnabled(enabled);
}

public static class Program
{
    static int failures = 0;

    static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name + "  ->  " + detail);
        if (!ok) failures++;
    }

    static string Fmt(TimeSpan t) => t.TotalSeconds.ToString("0.0") + "s";

    public static int Main()
    {
        Console.WriteLine("=== 暖序 · 暂停/恢复 计时审计（使用 Program.cs 第124-146行原文） ===");
        Console.WriteLine();

        // 场景1：间隔 1 分钟，倒计时剩 35 秒时暂停，10 秒后恢复
        var s = new Shell { _remindersMenu = new FakeMenu(), _trayRemindersMenu = new FakeMenu() };
        s._settings.ReminderMinutes = 1;
        s._settings.RemindersEnabled = true;
        s._nextReminder = DateTime.Now.AddSeconds(35);   // 模拟"还剩 35 秒"

        s.Invoke(false);                    // 用户点【暂停】
        var frozen = s._remainingOnPause;
        Check("1 暂停时冻结剩余时间", Math.Abs(frozen.TotalSeconds - 35) < 1.5, "冻结值 " + Fmt(frozen) + "（期望约 35s）");

        System.Threading.Thread.Sleep(1100);             // 暂停期间过了约 1 秒

        s.Invoke(true);                     // 用户点【开启】
        var afterResume = s._nextReminder - DateTime.Now;
        Check("2 恢复后接着剩余时间走", Math.Abs(afterResume.TotalSeconds - 35) < 2.5,
              "恢复后剩余 " + Fmt(afterResume) + "（期望约 35s，若为 60s 即为 bug）");
        Check("3 冻结值已清零", s._remainingOnPause == TimeSpan.Zero, "剩余冻结 " + Fmt(s._remainingOnPause));

        // 场景2：重复点【开启】（双击 / 两个入口重复通知）
        var before = s._nextReminder - DateTime.Now;
        s.Invoke(true);
        var after = s._nextReminder - DateTime.Now;
        Check("4 重复点【开启】不应重置倒计时", Math.Abs((after - before).TotalSeconds) < 1.5,
              "重置前 " + Fmt(before) + " -> 重置后 " + Fmt(after) + "（若跳回 60s 即为 bug）");

        // 场景2b：连按两次【暂停】，冻结值不应被后一次覆盖
        s.Invoke(false);
        var f1 = s._remainingOnPause;
        System.Threading.Thread.Sleep(1100);
        s.Invoke(false);
        var f2 = s._remainingOnPause;
        Check("6 重复点【暂停】不应改写冻结值", Math.Abs((f2 - f1).TotalSeconds) < 0.5,
              "第一次冻结 " + Fmt(f1) + " -> 第二次 " + Fmt(f2));

        // 场景3：暂停中改间隔
        var s2 = new Shell { _remindersMenu = new FakeMenu(), _trayRemindersMenu = new FakeMenu() };
        s2._settings.ReminderMinutes = 30;
        s2._settings.RemindersEnabled = true;
        s2._nextReminder = DateTime.Now.AddSeconds(40);
        s2.Invoke(false);
        var frozen2 = s2._remainingOnPause;
        Check("5 暂停中冻结值保留", Math.Abs(frozen2.TotalSeconds - 40) < 1.5, "冻结值 " + Fmt(frozen2));

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "结论：全部通过" : ("结论：" + failures + " 项未通过"));
        return failures;
    }
}
