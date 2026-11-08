using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ControlHub;

/// <summary>Windows 原生输入注入与程序启动。</summary>
public static class Native
{
    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL, wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    public static extern int GetKeyboardLayout(uint threadId);

    // ---- 剪贴板 ----
    public const uint CF_UNICODETEXT = 13;
    public const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalFree(IntPtr hMem);

    public static int InputSize => Marshal.SizeOf<INPUT>();
}

/// <summary>把配置里的 ActionDef 真正执行出来。</summary>
public sealed class ActionRunner
{
    private readonly ILogger<ActionRunner> _log;

    public ActionRunner(ILogger<ActionRunner> log) => _log = log;

    public async Task<string> RunAsync(ActionDef a, CancellationToken ct = default)
    {
        if (a.DelayMs > 0) await Task.Delay(a.DelayMs, ct);

        return a.Type?.ToLowerInvariant() switch
        {
            "app" => Launch(a.Path, a.Args),
            "open" => Open(a.Path),
            "text" => await TypeTextAsync(a.Text ?? "", a.Method ?? "type", ct),
            "hotkey" => Hotkey(a.Keys ?? new List<string>()),
            "sequence" => await RunSequenceAsync(a, ct),
            "none" => "ok (no action)",
            _ => throw new InvalidOperationException($"unknown action type '{a.Type}'")
        };
    }

    private async Task<string> RunSequenceAsync(ActionDef a, CancellationToken ct)
    {
        var results = new List<string>();
        foreach (var step in a.Steps ?? new List<ActionDef>())
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await RunAsync(step, ct));
            // 序列里每一步之间留一点时间，让目标窗口反应
            await Task.Delay(120, ct);
        }
        return "sequence ok: " + string.Join(" | ", results);
    }

    // ---------------- 启动 / 打开 ----------------

    private string Launch(string? path, string? args)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("action.path 为空");
        var p = Environment.ExpandEnvironmentVariables(path);
        var psi = new ProcessStartInfo
        {
            FileName = p,
            Arguments = args ?? "",
            UseShellExecute = true,
            WorkingDirectory = Directory.Exists(p) ? p : Path.GetDirectoryName(p) ?? ""
        };
        _log.LogInformation("launch {Path} {Args}", p, args);
        using var proc = Process.Start(psi);
        return $"launched {Path.GetFileName(p)}";
    }

    private string Open(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("action.path 为空");
        var p = Environment.ExpandEnvironmentVariables(path);
        if (!File.Exists(p) && !Directory.Exists(p))
            throw new FileNotFoundException($"路径不存在: {p}");
        Process.Start(new ProcessStartInfo { FileName = p, UseShellExecute = true });
        _log.LogInformation("open {Path}", p);
        return $"opened {Path.GetFileName(p)}";
    }

    // ---------------- 键盘 ----------------

    private static readonly Dictionary<string, ushort> VkMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B,
        ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09, ["esc"] = 0x1B, ["escape"] = 0x1B,
        ["space"] = 0x20, ["backspace"] = 0x08, ["bs"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E,
        ["insert"] = 0x2D, ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["capslock"] = 0x14, ["printscreen"] = 0x2C, ["pause"] = 0x13,
        // 媒体键（系统音量 / 播放控制），E0 扩展键
        ["volumemute"] = 0xAD, ["mute"] = 0xAD,
        ["volumedown"] = 0xAE, ["voldown"] = 0xAE,
        ["volumeup"] = 0xAF, ["volup"] = 0xAF,
        ["medianexttrack"] = 0xB0, ["nexttrack"] = 0xB0,
        ["mediaprevioustrack"] = 0xB1, ["prevtrack"] = 0xB1,
        ["mediastop"] = 0xB2, ["mediaplaypause"] = 0xB3, ["playpause"] = 0xB3,
        [";"] = 0xBA, ["="] = 0xBB, [","] = 0xBC, ["-"] = 0xBD, ["."] = 0xBE, ["/"] = 0xBF,
        ["`"] = 0xC0, ["["] = 0xDB, ["\\"] = 0xDC, ["]"] = 0xDD, ["'"] = 0xDE
    };

    private static ushort Vk(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("按键为空");
        var t = token.Trim();
        if (t.Length == 1)
        {
            char c = char.ToUpperInvariant(t[0]);
            if (c >= 'A' && c <= 'Z') return (ushort)c;          // 0x41..0x5A
            if (c >= '0' && c <= '9') return (ushort)c;          // 0x30..0x39
        }
        if (t.Length >= 2 && (t[0] == 'F' || t[0] == 'f') && int.TryParse(t[1..], out int f) && f >= 1 && f <= 24)
            return (ushort)(0x70 + f - 1);

        if (VkMap.TryGetValue(t, out ushort vk)) return vk;
        throw new ArgumentException($"不认识的按键: {token}");
    }

    private string Hotkey(List<string> keysIn)
    {
        if (keysIn.Count == 0) throw new ArgumentException("hotkey.keys 为空");

        // 支持 "ctrl+shift+s" 这种写成单个字符串的形式
        var tokens = new List<string>();
        foreach (var k in keysIn)
        {
            if (k.Contains('+') && k.Length > 1) tokens.AddRange(k.Split('+', StringSplitOptions.RemoveEmptyEntries));
            else tokens.Add(k);
        }

        var vks = tokens.Select(Vk).ToList();
        var down = new List<Native.INPUT>();
        var up = new List<Native.INPUT>();

        foreach (var vk in vks)
        {
            if (!down.Any(i => i.U.ki.wVk == vk))
                down.Add(KeyInput(vk, false));
        }
        for (int i = vks.Count - 1; i >= 0; i--)
        {
            if (!up.Any(x => x.U.ki.wVk == vks[i]))
                up.Add(KeyInput(vks[i], true));
        }

        var all = down.Concat(up).ToArray();
        uint sent = Native.SendInput((uint)all.Length, all, Native.InputSize);
        if (sent != all.Length)
            return $"WARN 只发送了 {sent}/{all.Length}，可能是目标窗口权限更高（管理员窗口），本程序需同样以管理员运行";

        _log.LogInformation("hotkey {Keys}", string.Join("+", tokens));
        return $"hotkey {string.Join("+", tokens)}";
    }

    private static Native.INPUT KeyInput(ushort vk, bool keyUp)
    {
        bool extended = vk is 0x2E or 0x26 or 0x28 or 0x25 or 0x27 or 0x24 or 0x23 or 0x21 or 0x22 or 0x2D or 0x5B
            or 0xAD or 0xAE or 0xAF or 0xB0 or 0xB1 or 0xB2 or 0xB3;
        var flags = (keyUp ? Native.KEYEVENTF_KEYUP : 0u) | (extended ? Native.KEYEVENTF_EXTENDEDKEY : 0u);
        return new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            U = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = flags } }
        };
    }

    /// <summary>逐字输入 UTF-16 文本，中文、emoji 都能进得去。</summary>
    private async Task<string> TypeTextAsync(string text, string method, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text)) return "ok (empty text)";

        if (string.Equals(method, "paste", StringComparison.OrdinalIgnoreCase))
            return await PasteAsync(text, ct);

        const int chunk = 64;
        for (int off = 0; off < text.Length; off += chunk)
        {
            ct.ThrowIfCancellationRequested();
            var slice = text.Substring(off, Math.Min(chunk, text.Length - off));
            var inputs = new List<Native.INPUT>(slice.Length * 2);
            foreach (char ch in slice)
            {
                inputs.Add(UnicodeInput(ch, false));
                inputs.Add(UnicodeInput(ch, true));
            }
            var arr = inputs.ToArray();
            uint sent = Native.SendInput((uint)arr.Length, arr, Native.InputSize);
            if (sent != arr.Length)
                return $"WARN 只发送了 {sent}/{arr.Length}，目标窗口可能权限更高";
            await Task.Delay(15, ct);
        }
        return $"typed {text.Length} chars";
    }

    private static Native.INPUT UnicodeInput(char ch, bool keyUp) => new()
    {
        type = Native.INPUT_KEYBOARD,
        U = new Native.InputUnion
        {
            ki = new Native.KEYBDINPUT
            {
                wVk = 0,
                wScan = ch,
                dwFlags = Native.KEYEVENTF_UNICODE | (keyUp ? Native.KEYEVENTF_KEYUP : 0u)
            }
        }
    };

    /// <summary>写剪贴板再按 Ctrl+V —— 某些输入框不认 Unicode 注入时更可靠。</summary>
    private async Task<string> PasteAsync(string text, CancellationToken ct)
    {
        SetClipboardText(text);
        await Task.Delay(60, ct);
        var vks = new[] { (ushort)0x11, (ushort)0x56 }; // ctrl + v
        var down = vks.Select(v => KeyInput(v, false)).ToList();
        var up = vks.Reverse().Select(v => KeyInput(v, true)).ToList();
        var all = down.Concat(up).ToArray();
        Native.SendInput((uint)all.Length, all, Native.InputSize);
        return $"pasted {text.Length} chars";
    }

    /// <summary>写剪贴板（失败会重试，因为别的程序可能占着）。</summary>
    public static void SetClipboardText(string text)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (Native.OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    Native.EmptyClipboard();
                    var bytes = Encoding.Unicode.GetBytes(text + "\0");
                    var hMem = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)bytes.Length);
                    if (hMem == IntPtr.Zero) throw new OutOfMemoryException("GlobalAlloc 失败");
                    var ptr = Native.GlobalLock(hMem);
                    if (ptr == IntPtr.Zero) throw new OutOfMemoryException("GlobalLock 失败");
                    try { Marshal.Copy(bytes, 0, ptr, bytes.Length); }
                    finally { Native.GlobalUnlock(hMem); }
                    if (Native.SetClipboardData(Native.CF_UNICODETEXT, hMem) == IntPtr.Zero)
                    {
                        Native.GlobalFree(hMem);
                        throw new InvalidOperationException("SetClipboardData 失败");
                    }
                    return;
                }
                finally { Native.CloseClipboard(); }
            }
            Thread.Sleep(50);
        }
        throw new InvalidOperationException("剪贴板被其他程序占用，无法写入");
    }
}
