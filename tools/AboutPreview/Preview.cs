using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace AboutPreview;

internal static class Preview
{
    public static Form BuildAboutForm(Icon appIcon)
    {
        // 版式：居中品牌区 → 版本胶囊 → 分隔线 → 两列信息 → 分隔线 → 二维码卡片 → 主按钮
        // 坐标一律按 96 DPI 设计，再乘实际缩放比。字体用磅值（物理单位，与 DPI 无关），不能跟着放大，
        // 否则 125%/150% 缩放的屏幕上字体变大、容器没变大，文字会被裁掉。
        float scale;
        var __env = Environment.GetEnvironmentVariable("PREVIEW_SCALE");
        if (float.TryParse(__env, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var __s) && __s > 0) scale = __s;
        else using (var screen = Graphics.FromHwnd(IntPtr.Zero)) scale = screen.DpiX / 96f;
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
        var version = Application.ProductVersion;
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
        y += S(14);

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
        // 产品代码在这里还挂了「点一下弹大图」的 Click 事件；本工具只验版式，
        // 点击行为由 tools/QrPreviewAudit（从 Program.cs 抽原文编译）单独验证，不在这里镜像。

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

    private static Image? LoadQrImage(string fileName, string resourceName)
    {
        // 测试钩子：强制走「缺图」分支，验证占位符版式（仅本预览工具使用，产品代码没有这段）
        if (Environment.GetEnvironmentVariable("PREVIEW_NO_QR") == "1") return null;

        // 外部文件优先：把 donate-qr.png 放到 exe 同目录即可换码，不用重新编译
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
            using var stream = typeof(Preview).Assembly.GetManifestResourceStream(resourceName);
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
}

internal static class Entry
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [STAThread]
    private static void Main(string[] args)
    {
        // DPI-unaware 模式下系统按 96 DPI 上报，可用来验证 100% 缩放下的版式
        if (Environment.GetEnvironmentVariable("PREVIEW_UNAWARE") == "1")
            SetProcessDpiAwarenessContext(new IntPtr(-1));

        ApplicationConfiguration.Initialize();
        var icon = Icon.ExtractAssociatedIcon(Path.Combine(AppContext.BaseDirectory, "app.ico"))
                   ?? SystemIcons.Application;
        var form = Preview.BuildAboutForm(icon);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(200, 200);
        form.TopMost = true;   // 抓图前压到最前，避免被别的窗口盖住截出黑屏
        form.Show();
        Application.DoEvents();
        System.Threading.Thread.Sleep(500);
        Application.DoEvents();

        var outPath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "about.png");
        var screen = form.Bounds;
        using (var bmp = new Bitmap(screen.Width, screen.Height))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                try
                {
                    // 真实屏幕抓图，避免 DrawToBitmap 对居中/右对齐文本的裁剪缺陷
                    g.CopyFromScreen(screen.Location, Point.Empty, screen.Size);
                }
                catch
                {
                    form.DrawToBitmap(bmp, new Rectangle(0, 0, screen.Width, screen.Height));
                }
            }
            bmp.Save(outPath, ImageFormat.Png);
        }
        Console.WriteLine($"bounds={screen.Width}x{screen.Height} client={form.ClientSize.Width}x{form.ClientSize.Height} -> {outPath}");

        var log = new System.Text.StringBuilder();
        int bad = 0;
        log.AppendLine($"DPI={form.DeviceDpi} scale={form.ClientSize.Width / 420.0:0.###}");
        void Walk(Control parent, int depth)
        {
            foreach (Control c in parent.Controls)
            {
                var txt = c.Text.Replace("\n", "\\n");
                if (txt.Length > 14) txt = txt[..14] + "…";
                var need = c is Label or Button
                    ? TextRenderer.MeasureText(c.Text, c.Font, new Size(int.MaxValue, int.MaxValue))
                    : Size.Empty;
                var ok = need.IsEmpty || (c.Width >= need.Width && c.Height >= need.Height);
                if (!ok) bad++;
                log.AppendLine($"{new string(' ', depth * 2)}{(need.IsEmpty ? "  -  " : ok ? "  OK " : " CUT ")} {c.GetType().Name,-11} b={c.Width}x{c.Height} need={need.Width}x{need.Height} '{txt}'");
                Walk(c, depth + 1);
            }
        }
        Walk(form, 0);
        log.AppendLine(bad == 0 ? "结论：无裁切" : $"结论：{bad} 处尺寸不足");
        File.WriteAllText(Path.ChangeExtension(outPath, ".txt"), log.ToString());
        Console.WriteLine(bad == 0 ? "LAYOUT OK" : $"LAYOUT {bad} CUT");
        form.Close();
    }
}
