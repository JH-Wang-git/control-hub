using System.Diagnostics;
using System.Text;

namespace MediaProbe;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new ProbeForm());
    }
}

sealed class ProbeForm : Form
{
    const int WM_APPCOMMAND = 0x0319;
    const int WM_KEYDOWN = 0x0100;
    const int WM_SYSKEYDOWN = 0x0104;

    readonly string _log = Path.Combine(AppContext.BaseDirectory, "media-probe.log");
    readonly System.Windows.Forms.Timer _focus = new() { Interval = 300 };

    public ProbeForm()
    {
        Text = "MediaProbe";
        Width = 520;
        Height = 300;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        BackColor = Color.FromArgb(20, 24, 32);
        ForeColor = Color.Gainsboro;
        var lbl = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Consolas", 11f),
            Text = "等待媒体键…\n(log: media-probe.log)"
        };
        Controls.Add(lbl);
        File.WriteAllText(_log, $"start {DateTime.Now:HH:mm:ss}\r\n", Encoding.UTF8);

        _focus.Tick += (_, _) =>
        {
            if (GetForegroundWindow() != Handle)
            {
                NativeMethods.keybd_event(0x12, 0, 0, UIntPtr.Zero);          // ALT down: 解锁 SetForegroundWindow
                NativeMethods.SetForegroundWindow(Handle);
                NativeMethods.keybd_event(0x12, 0, 2, UIntPtr.Zero);          // ALT up
            }
        };
        _focus.Start();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    protected override void WndProc(ref Message m)
    {
        if (m.Msg is WM_APPCOMMAND or WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            var appcmd = (int)((long)m.LParam >> 16) & 0x0FFF;
            var vk = (int)m.WParam;
            var name = m.Msg switch
            {
                WM_APPCOMMAND => $"APPCOMMAND {appcmd} ({AppCommandName(appcmd)})",
                _ => $"VK 0x{vk:X2} {KeyName(vk)}"
            };
            File.AppendAllText(_log, $"{DateTime.Now:HH:mm:ss.fff}  {name}\r\n", Encoding.UTF8);
            if (Controls.Count > 0 && Controls[0] is Label l)
                l.Text = name + "\n" + DateTime.Now.ToString("HH:mm:ss");
        }
        base.WndProc(ref m);
    }

    static string AppCommandName(int c) => c switch
    {
        13 => "MEDIA_PLAY_PAUSE",
        14 => "MEDIA_PLAY_PAUSE",
        46 => "MEDIA_PLAY",
        47 => "MEDIA_PAUSE",
        48 => "MEDIA_RECORD",
        49 => "MEDIA_FAST_FORWARD",
        50 => "MEDIA_REWIND",
        51 => "MEDIA_NEXTTRACK",
        52 => "MEDIA_PREVIOUSTRACK",
        53 => "MEDIA_STOP",
        34 => "VOLUME_MUTE",
        35 => "VOLUME_DOWN",
        36 => "VOLUME_UP",
        11 => "BROWSER_BACK",
        _ => "?"
    };

    static string KeyName(int vk) => vk switch
    {
        0xB3 => "VK_MEDIA_PLAY_PAUSE",
        0xB0 => "VK_MEDIA_NEXT_TRACK",
        0xB1 => "VK_MEDIA_PREV_TRACK",
        0xB2 => "VK_MEDIA_STOP",
        0xAD => "VK_VOLUME_MUTE",
        0xAE => "VK_VOLUME_DOWN",
        0xAF => "VK_VOLUME_UP",
        0x20 => "VK_SPACE",
        _ => "?"
    };
}

static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
}
