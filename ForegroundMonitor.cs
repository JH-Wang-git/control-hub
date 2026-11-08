using System.Diagnostics;
using System.Text;

namespace ControlHub;

/// <summary>周期性读取当前前台窗口属于哪个进程。</summary>
public sealed class ForegroundMonitor : BackgroundService
{
    private readonly ILogger<ForegroundMonitor> _log;
    private readonly int _intervalMs;

    public string ProcessName { get; private set; } = "";
    public string WindowTitle { get; private set; } = "";

    /// <summary>(进程名, 窗口标题)，仅在前台进程真正变化时触发。</summary>
    public event Action<string, string>? Changed;

    public ForegroundMonitor(ILogger<ForegroundMonitor> log, IConfiguration cfg)
    {
        _log = log;
        _intervalMs = cfg.GetValue("watchIntervalMs", 400);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Sample(); }
            catch (Exception ex) { _log.LogDebug(ex, "foreground sample failed"); }
            try { await Task.Delay(_intervalMs, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Sample()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;

        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return;

        string name;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName.ToLowerInvariant();
        }
        catch { return; }

        var sb = new StringBuilder(512);
        Native.GetWindowTextW(hwnd, sb, sb.Capacity);
        var title = sb.ToString();

        // 自己不算
        if (name is "controlhub" or "dotnet") return;

        if (name == ProcessName) return;

        ProcessName = name;
        WindowTitle = title;
        _log.LogInformation("foreground → {Proc} ({Title})", name, title);
        Changed?.Invoke(name, title);
    }
}
