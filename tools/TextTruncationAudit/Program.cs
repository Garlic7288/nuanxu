using System.Globalization;

namespace TextTruncationAudit;

/// <summary>
/// 验证 NormalizeReminderText 不会把 emoji 切成半个代理对。
/// 方法体由 extract.py 从 NuanXu/Program.cs 原文抽出（Generated.cs），不是手抄副本。
/// </summary>
internal static class Program
{
    private static int _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"  [{(ok ? "OK " : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
        if (!ok) _fail++;
    }

    /// <summary>文本元素个数（字素簇），用来说明"元素数"和"码元数"是两回事。</summary>
    private static int Elements(string s) => new StringInfo(s).LengthInTextElements;

    /// <summary>末尾是不是一个孤立的代理项（半个 emoji）。</summary>
    private static bool HasLoneSurrogate(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return true;
                i++;   // 成对，跳过低位
            }
            else if (char.IsLowSurrogate(s[i]))
            {
                return true;   // 没有配对的低位
            }
        }
        return false;
    }

    private static void Main()
    {
        const int Max = Truncate.MaxReminderTextLength;
        Console.WriteLine($"MaxReminderTextLength = {Max}\n");

        // ---- 1. 关键用例：第 40 个码元正好是 emoji 的高代理 ----
        var input = new string('字', Max - 1) + "😀" + "尾";
        Console.WriteLine($"用例1  输入：{Max - 1} 汉字 + 1 emoji + 1 汉字");
        Console.WriteLine($"       元素数={Elements(input)}  码元数={input.Length}");

        var output = Truncate.NormalizeReminderText(input);
        Console.WriteLine($"       输出元素数={Elements(output)}  码元数={output.Length}");
        Check($"输出元素数 = {Max}", Elements(output) == Max, $"实际 {Elements(output)}");
        Check("输出没有孤立代理项（没被切成半个 emoji）", !HasLoneSurrogate(output));
        Check("emoji 被完整保留", output.Contains("😀"));

        // 反向对照：旧实现（按码元切）在同一输入上必然切坏，用来证明这次修的是真问题
        var legacy = input[..Max];
        Check("旧实现 value[..40] 确实会切出孤立代理（证明修复有意义）", HasLoneSurrogate(legacy),
            $"legacy 末尾=U+{(int)legacy[^1]:X4}");

        // ---- 2. 不超长时原样返回 ----
        var shortText = new string('暖', Max);
        Check($"正好 {Max} 个汉字原样返回", Truncate.NormalizeReminderText(shortText) == shortText);

        var emojis = string.Concat(Enumerable.Repeat("😀", Max / 2));   // 20 个 emoji = 40 码元
        Check("20 个 emoji（40 码元）原样返回",
            Truncate.NormalizeReminderText(emojis) == emojis,
            $"元素数={Elements(emojis)} 码元数={emojis.Length}");

        // ---- 3. 超长纯汉字 ----
        var longCn = new string('汉', Max + 5);
        var cut = Truncate.NormalizeReminderText(longCn);
        Check($"超长汉字截到 {Max} 个", Elements(cut) == Max, $"实际 {Elements(cut)}");

        // ---- 4. 空 / 纯空白 → 默认文案 ----
        var dflt = Truncate.NormalizeReminderText("   ");
        Check("纯空白回默认文案", dflt.Length > 0 && !dflt.Contains(' '), dflt);
        Check("null 回默认文案", Truncate.NormalizeReminderText(null).Length > 0);

        // ---- 5. 首尾空白被去掉 ----
        Check("首尾空白被 trim", Truncate.NormalizeReminderText("  抬头看远处  ") == "抬头看远处");

        Console.WriteLine();
        Console.WriteLine(_fail == 0 ? "结果：全部通过" : $"结果：{_fail} 项失败");
        Environment.ExitCode = _fail == 0 ? 0 : 1;
    }
}
