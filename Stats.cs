using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace ControlHub;

/// <summary>一帧系统状态，用于手机端“状态页”。</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed record StatsSnapshot
{
    public string Time { get; init; } = "";
    public double Cpu { get; init; }                 // 0..100
    public double MemUsedMb { get; init; }
    public double MemTotalMb { get; init; }
    public double MemPct { get; init; }
    public double? Gpu { get; init; }                // 0..100
    public double? GpuTemp { get; init; }
    public double? CpuTemp { get; init; }
    public double? GpuMemUsedMb { get; init; }
    public double? GpuMemTotalMb { get; init; }
    public double? Fps { get; init; }
    public double? Hz { get; init; }
    public string App { get; init; } = "";
    public string Title { get; init; } = "";
}

/// <summary>每秒采一次 CPU / 内存 / GPU / 帧率，供状态页轮询。</summary>
public sealed class StatsCollector : BackgroundService
{
    private readonly ForegroundMonitor _monitor;
    private readonly ILogger<StatsCollector> _log;

    private Computer? _lhm;
    private IHardware? _gpuHw;
    private IHardware? _cpuHw;
    private bool _lhmBroken;

    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _haveCpuBase;

    public StatsSnapshot Current { get; private set; } = new() { Time = DateTime.Now.ToString("HH:mm:ss") };

    public StatsCollector(ForegroundMonitor monitor, ILogger<StatsCollector> log)
    {
        _monitor = monitor;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        InitSensors();
        while (!ct.IsCancellationRequested)
        {
            try { Current = Sample(); }
            catch (Exception ex) { _log.LogWarning(ex, "stats sample failed"); }

            try { await Task.Delay(1000, ct); } catch { break; }
        }
    }

    // ---------------- 传感器（LibreHardwareMonitor） ----------------

    private void InitSensors()
    {
        try
        {
            bool admin = IsAdmin();
            _lhm = new Computer
            {
                IsCpuEnabled = admin,      // CPU 温度要内核驱动，必须管理员
                IsGpuEnabled = true,
                IsMemoryEnabled = false,
                IsMotherboardEnabled = false,
                IsStorageEnabled = false,
                IsNetworkEnabled = false
            };
            _lhm.Open();

            var found = new List<string>();
            foreach (var hw in _lhm.Hardware)
            {
                found.Add(hw.HardwareType + ":" + hw.Name);
                if (hw.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel)
                    _gpuHw = hw;
                else if (hw.HardwareType == HardwareType.Cpu)
                    _cpuHw = hw;
            }

            _log.LogInformation("sensors: admin={Admin} count={N} [{List}] gpu={Gpu} cpu={Cpu}",
                admin, found.Count, string.Join(", ", found),
                _gpuHw?.Name ?? "none", _cpuHw?.Name ?? "none");
        }
        catch (Exception ex)
        {
            _lhmBroken = true;
            _log.LogWarning(ex, "sensors unavailable, 状态页只显示 CPU/内存");
        }
    }

    private static bool IsAdmin()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // ---------------- 采样 ----------------

    private StatsSnapshot Sample()
    {
        var mem = Memory();
        double cpu = CpuPercent();

        double? gpu = null, gpuTemp = null, cpuTemp = null, gpuUsed = null, gpuTotal = null;

        if (!_lhmBroken && _lhm is not null)
        {
            try
            {
                if (_gpuHw is not null)
                {
                    _gpuHw.Update();
                    foreach (var s in _gpuHw.Sensors)
                    {
                        if (s.Value is not float v) continue;
                        switch (s.SensorType)
                        {
                            case SensorType.Load when s.Name == "GPU Core": gpu = v; break;
                            case SensorType.Temperature when s.Name == "GPU Core": gpuTemp = v; break;
                            case SensorType.SmallData when s.Name == "GPU Memory Used": gpuUsed = v; break;
                            case SensorType.SmallData when s.Name == "GPU Memory Total": gpuTotal = v; break;
                        }
                    }
                }

                if (_cpuHw is not null)
                {
                    _cpuHw.Update();
                    float? pkg = null, max = null;
                    foreach (var s in _cpuHw.Sensors)
                    {
                        if (s.Value is not float v) continue;
                        if (s.SensorType != SensorType.Temperature) continue;
                        if (s.Name == "CPU Package") pkg = v;
                        else if (s.Name == "Core Max") max = v;
                    }
                    cpuTemp = pkg ?? max;
                }
            }
            catch { /* 单次失败不影响其它指标 */ }
        }

        var (fps, hz) = DwmFrameRate();

        return new StatsSnapshot
        {
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Cpu = Math.Round(cpu, 1),
            MemUsedMb = Math.Round(mem.used, 1),
            MemTotalMb = Math.Round(mem.total, 1),
            MemPct = mem.total > 0 ? Math.Round(mem.used / mem.total * 100, 1) : 0,
            Gpu = gpu is null ? null : Math.Round(gpu.Value, 1),
            GpuTemp = gpuTemp is null ? null : Math.Round(gpuTemp.Value, 1),
            CpuTemp = cpuTemp is null ? null : Math.Round(cpuTemp.Value, 1),
            GpuMemUsedMb = gpuUsed is null ? null : Math.Round(gpuUsed.Value, 0),
            GpuMemTotalMb = gpuTotal is null ? null : Math.Round(gpuTotal.Value, 0),
            Fps = fps,
            Hz = hz,
            App = _monitor.ProcessName,
            Title = _monitor.WindowTitle
        };
    }

    private double CpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;

        ulong i = FileTimeToU64(idle), k = FileTimeToU64(kernel), u = FileTimeToU64(user);
        if (!_haveCpuBase)
        {
            _prevIdle = i; _prevKernel = k; _prevUser = u;
            _haveCpuBase = true;
            return 0;
        }

        ulong di = i - _prevIdle, dk = k - _prevKernel, du = u - _prevUser;
        _prevIdle = i; _prevKernel = k; _prevUser = u;

        // kernel 时间包含 idle，所以总时间 = kernel + user
        ulong total = dk + du;
        if (total == 0) return 0;
        return Math.Clamp((total - di) / (double)total * 100.0, 0, 100);
    }

    private static (double used, double total) Memory()
    {
        var st = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref st)) return (0, 0);
        return ((st.ullTotalPhys - st.ullAvailPhys) / 1048576.0, st.ullTotalPhys / 1048576.0);
    }

    // ---------------- 帧率（DWM 合成） ----------------

    private ulong _prevFrames;
    private ulong _prevQpc;
    private double? _lastFps;

    private (double? fps, double? hz) DwmFrameRate()
    {
        try
        {
            var info = new DWM_TIMING_INFO { cbSize = (uint)Marshal.SizeOf<DWM_TIMING_INFO>() };
            int hr = DwmGetCompositionTimingInfo(IntPtr.Zero, ref info);
            if (hr != 0) return (_lastFps, RefreshHz());

            double hz = info.rateCompose.uiDenominator != 0
                ? (double)info.rateCompose.uiNumerator / info.rateCompose.uiDenominator
                : (info.rateRefresh.uiDenominator != 0
                    ? (double)info.rateRefresh.uiNumerator / info.rateRefresh.uiDenominator
                    : 0);

            double? fps = _lastFps;
            if (_prevQpc != 0 && info.qpcVBlank > _prevQpc)
            {
                ulong dq = info.qpcVBlank - _prevQpc;
                ulong df = info.cFramesDisplayed - _prevFrames;
                double sec = dq / (double)PerformanceFrequency;
                if (sec > 0.05) fps = _lastFps = Math.Round(df / sec, 1);
            }

            _prevQpc = info.qpcVBlank;
            _prevFrames = info.cFramesDisplayed;

            return (fps, hz > 0 ? Math.Round(hz, 1) : null);
        }
        catch { return (_lastFps, null); }
    }

    private static double? _hzCache;

    /// <summary>主显示器刷新率（DWM 合成信息拿不到时的兜底）。</summary>
    private static double? RefreshHz()
    {
        if (_hzCache is not null) return _hzCache;
        try
        {
            var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettingsW(null, -1, ref dm) && dm.dmDisplayFrequency > 1)
                _hzCache = dm.dmDisplayFrequency;
        }
        catch { }
        return _hzCache;
    }

    private static readonly long PerformanceFrequency = QueryPerformanceFrequencyValue();    private static long QueryPerformanceFrequencyValue()
    {
        QueryPerformanceFrequency(out long f);
        return f > 0 ? f : 10_000_000;
    }

    // ---------------- P/Invoke ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

    private static ulong FileTimeToU64(FILETIME t) => ((ulong)t.dwHighDateTime << 32) | t.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UNSIGNED_RATIO { public uint uiNumerator; public uint uiDenominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_TIMING_INFO
    {
        public uint cbSize;
        public UNSIGNED_RATIO rateRefresh;
        public ulong qpcRefreshPeriod;
        public UNSIGNED_RATIO rateCompose;
        public ulong qpcVBlank;
        public ulong cRefresh;
        public uint cDXRefresh;
        public ulong qpcCompose;
        public ulong cFrame;
        public uint cDXPresent;
        public ulong cRefreshFrame;
        public ulong cFrameSubmitted;
        public uint cDXPresentSubmitted;
        public ulong cFrameConfirmed;
        public uint cDXPresentConfirmed;
        public ulong cRefreshConfirmed;
        public uint cDXRefreshConfirmed;
        public ulong cFramesLate;
        public uint cFramesOutstanding;
        public ulong cFrameDisplayed;
        public ulong qpcFrameDisplayed;
        public ulong cRefreshFrameDisplayed;
        public ulong cFrameComplete;
        public ulong qpcFrameComplete;
        public ulong cFramePending;
        public ulong qpcFramePending;
        public ulong cFramesDisplayed;
        public ulong cFramesComplete;
        public ulong cFramesPending;
        public ulong cFramesAvailable;
        public ulong cFramesDropped;
        public ulong cFramesMissed;
        public ulong cRefreshNextDisplayed;
        public ulong cRefreshNextPresented;
        public ulong cRefreshesDisplayed;
        public ulong cRefreshesPresented;
        public ulong cRefreshStarted;
        public ulong cPixelsReceived;
        public ulong cPixelsDrawn;
        public ulong cBuffersEmpty;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public short dmOrientation;
        public short dmPaperSize;
        public short dmPaperLength;
        public short dmPaperWidth;
        public short dmScale;
        public short dmCopies;
        public short dmDefaultSource;
        public short dmPrintQuality;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll")]
    private static extern bool QueryPerformanceFrequency(out long freq);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DWM_TIMING_INFO info);
}
