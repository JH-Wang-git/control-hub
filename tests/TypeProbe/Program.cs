// 打字/快捷键验证靶子：一个自己抢前台的小窗口 + 一个文本框。
// 把文本框内容每 400ms 写到 probe.txt，方便外部程序核对 SendInput 是否真的落字。
using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    private static readonly string OutPath = Path.Combine(AppContext.BaseDirectory, "probe.txt");

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var box = new TextBox
        {
            Multiline = true,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 14f),
            ScrollBars = ScrollBars.Vertical,
            Name = "probeBox"
        };

        var form = new Form
        {
            Text = "TypeProbe 打字验证窗口",
            Width = 620,
            Height = 320,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(40, 40),
            TopMost = false
        };
        form.Controls.Add(box);

        // 每 200ms 抢一次前台（先轻敲 ALT，绕开 Windows 的前台锁定规则）
        var grab = new System.Windows.Forms.Timer { Interval = 200 };
        grab.Tick += (_, _) =>
        {
            if (Form.ActiveForm == form) return;
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            SetForegroundWindow(form.Handle);
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
        };

        var dump = new System.Windows.Forms.Timer { Interval = 400 };
        dump.Tick += (_, _) =>
        {
            try
            {
                File.WriteAllText(OutPath, box.Text ?? "", new UTF8Encoding(false));
            }
            catch { /* 忽略写入竞争 */ }
        };

        form.Shown += (_, _) =>
        {
            box.Focus();
            File.WriteAllText(OutPath, "", new UTF8Encoding(false));
            grab.Start();
            dump.Start();
        };

        Application.Run(form);
    }
}
