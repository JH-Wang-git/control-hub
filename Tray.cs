using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace ControlHub;

/// <summary>
/// 托盘图标 + 小状态窗口。
/// 双击图标显示窗口；右键菜单可以打开手机界面 / 设置界面 / 退出。
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly StatusForm _form;
    private readonly string _appUrl;
    private readonly string _adminUrl;
    private readonly Action _exit;

    public TrayApp(string appUrl, string adminUrl, string token, string configPath, Action exit, bool showWindow)
    {
        _appUrl = appUrl;
        _adminUrl = adminUrl;
        _exit = exit;

        _form = new StatusForm(appUrl, adminUrl, token, configPath,
            onOpenApp: () => OpenUrl(_appUrl),
            onOpenAdmin: () => OpenUrl(_adminUrl),
            onHide: HideWindow,
            onExit: Quit);

        var menu = new ContextMenuStrip { Font = new Font("Microsoft YaHei UI", 9F) };
        menu.Items.Add("打开用户界面", null, (_, _) => OpenUrl(_appUrl));
        menu.Items.Add("打开设置界面", null, (_, _) => OpenUrl(_adminUrl));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("显示窗口", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出程序", null, (_, _) => Quit());

        _icon = new NotifyIcon
        {
            Text = "ControlHub 局域网控制台",
            Icon = LoadIcon(),
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => ShowWindow();

        if (showWindow) ShowWindow();

        // 留一行启动痕迹，方便排查“图标到底有没有建起来”
        try
        {
            var dir = Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory;
            File.AppendAllText(Path.Combine(dir, "startup.log"),
                $"{DateTime.Now:HH:mm:ss} 托盘图标已就绪（可见={_icon.Visible}）\r\n");
        }
        catch { /* ignore */ }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "ControlHub.ico");
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                return new Icon(stream);
            }
        }
        catch { /* 回退到 exe 自带图标 */ }

        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            var ico = Icon.ExtractAssociatedIcon(exe);
            if (ico is not null) return ico;
        }
        return SystemIcons.Application;
    }

    private void ShowWindow()
    {
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
        _form.BringToFront();
    }

    private void HideWindow() => _form.Hide();

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch { /* 没有默认浏览器就忽略 */ }
    }

    private void Quit()
    {
        _icon.Visible = false;
        _icon.Dispose();
        try { _form.Hide(); } catch { }
        ExitThread();          // 结束 WinForms 消息循环
        _exit();               // 停掉 Web 服务并退出进程
    }
}

/// <summary>点托盘图标双击后弹出的那个小窗口。</summary>
public sealed class StatusForm : Form
{
    private readonly string _appUrl;

    public StatusForm(string appUrl, string adminUrl, string token, string configPath,
        Action onOpenApp, Action onOpenAdmin, Action onHide, Action onExit)
    {
        _appUrl = appUrl;

        Text = "ControlHub";
        ClientSize = new Size(604, 292);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(16, 22, 30);
        ForeColor = Color.FromArgb(232, 238, 245);

        var title = new Label
        {
            Text = "ControlHub  局域网控制台",
            Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold),
            ForeColor = Color.FromArgb(96, 165, 250),
            AutoSize = true,
            Location = new Point(18, 14)
        };
        Controls.Add(title);

        var tip = new Label
        {
            Text = "手机和电脑连同一个 WiFi，用手机浏览器打开下面的地址。",
            AutoSize = true,
            ForeColor = Color.FromArgb(150, 165, 185),
            Location = new Point(20, 48)
        };
        Controls.Add(tip);

        AddRow("手机访问", appUrl, 78);
        AddRow("设置页面", adminUrl, 108);
        AddRow("令牌", string.IsNullOrEmpty(token) ? "（已关闭验证）" : token, 138);
        AddRow("配置文件", configPath, 168);

        var hint = new Label
        {
            Text = "双击托盘图标可再打开；点右上角 × 只是收进托盘，不退出。",
            AutoSize = true,
            ForeColor = Color.FromArgb(120, 136, 156),
            Location = new Point(20, 204)
        };
        Controls.Add(hint);

        var bar = new TableLayoutPanel
        {
            Location = new Point(18, 232),
            Size = new Size(566, 40),
            ColumnCount = 5,
            RowCount = 1,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        for (int i = 0; i < 5; i++) bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20F));
        bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        bar.Controls.Add(MakeButton("用户界面", onOpenApp, primary: true));
        bar.Controls.Add(MakeButton("设置界面", onOpenAdmin));
        bar.Controls.Add(MakeButton("复制地址", () =>
        {
            try { Clipboard.SetText(_appUrl); } catch { }
        }));
        bar.Controls.Add(MakeButton("隐藏", onHide));
        bar.Controls.Add(MakeButton("退出程序", onExit));
        Controls.Add(bar);

        FormClosing += (_, e) =>
        {
            // 点 × 不退出程序，只收进托盘
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    private void AddRow(string caption, string value, int top)
    {
        var cap = new Label
        {
            Text = caption,
            AutoSize = false,
            Width = 88,
            Height = 20,
            Location = new Point(20, top + 3),
            ForeColor = Color.FromArgb(140, 155, 175)
        };
        var val = new Label
        {
            Text = value,
            AutoSize = false,
            Width = 472,
            Height = 20,
            Location = new Point(112, top + 3),
            ForeColor = Color.FromArgb(232, 238, 245),
            AutoEllipsis = true
        };
        Controls.Add(cap);
        Controls.Add(val);
    }

    private static Button MakeButton(string text, Action onClick, bool primary = false)
    {
        var b = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            Margin = new Padding(3, 0, 3, 0),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            BackColor = primary ? Color.FromArgb(59, 130, 246) : Color.FromArgb(30, 41, 59),
            ForeColor = Color.White
        };
        b.FlatAppearance.BorderColor = primary ? Color.FromArgb(59, 130, 246) : Color.FromArgb(51, 65, 85);
        b.FlatAppearance.BorderSize = 1;
        b.Click += (_, _) => { try { onClick(); } catch { } };
        return b;
    }
}
