using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlHub;

/// <summary>config.json 的读写与默认值。</summary>
public sealed class ConfigStore
{
    private readonly object _gate = new();
    private readonly string _path;

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AppConfig Current { get; private set; } = new();
    public string Path_ => _path;

    public ConfigStore(string path) => _path = path;

    public void Load()
    {
        lock (_gate)
        {
            bool exists = File.Exists(_path);
            if (exists)
            {
                try
                {
                    var json = File.ReadAllText(_path);
                    Current = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? Defaults();
                }
                catch (Exception ex)
                {
                    var bad = _path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    File.Copy(_path, bad, overwrite: true);
                    Console.WriteLine($"[config] 解析失败，已备份到 {bad}: {ex.Message}");
                    Current = Defaults();
                }
            }
            else
            {
                Current = Defaults();
            }

            // 只有首次生成配置时才自动发令牌；已有的空令牌表示用户主动关闭了验证
            if (!exists && string.IsNullOrWhiteSpace(Current.Token))
                Current.Token = NewToken();

            MigrateGlass(Current);
            SaveLocked();
        }
    }

    /// <summary>
    /// 老版本只有一个「毛玻璃透明度」字段：把它同时换算成颜色透明度和模糊程度，
    /// 换算完清掉旧字段，避免以后两套参数并存。
    /// </summary>
    private static void MigrateGlass(AppConfig cfg)
    {
        if (cfg.GlassOpacity is int legacy)
        {
            cfg.GlassAlpha = Clamp100(legacy);
            cfg.GlassBlur = Clamp100(legacy);
            cfg.GlassOpacity = null;
        }
        cfg.GlassAlpha = Clamp100(cfg.GlassAlpha);
        cfg.GlassBlur = Clamp100(cfg.GlassBlur);

        foreach (var p in cfg.Pages)
        {
            if (p.GlassOpacity is int pg)
            {
                p.GlassAlpha = Clamp100(pg);
                p.GlassBlur = Clamp100(pg);
                p.GlassOpacity = null;
            }
            if (p.GlassAlpha is int a) p.GlassAlpha = Clamp100(a);
            if (p.GlassBlur is int b) p.GlassBlur = Clamp100(b);
        }
    }

    public static int Clamp100(int v) => Math.Clamp(v, 0, 100);

    public void Save(AppConfig cfg)
    {
        lock (_gate)
        {
            // 令牌允许留空（关闭验证）；null 表示“没传”，沿用旧值
            cfg.Token ??= Current.Token;
            cfg.Token = cfg.Token.Trim();
            Current = cfg;
            SaveLocked();
        }
    }

    private void SaveLocked()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOpts));
        File.Move(tmp, _path, overwrite: true);
    }

    private static string NewToken()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        var rnd = Random.Shared;
        return new string(Enumerable.Range(0, 6).Select(_ => alphabet[rnd.Next(alphabet.Length)]).ToArray());
    }

    /// <summary>首次运行生成的示例配置：能立刻上手，也是配置格式的活文档。</summary>
    public static AppConfig Defaults()
    {
        string U(string p) => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + p;

        return new AppConfig
        {
            Port = 8080,
            DefaultPage = "default",
            AutoSwitchDefault = true,
            Pages = new List<PageDef>
            {
                new()
                {
                    Id = "status", Name = "状态", Kind = "status", Rows = 8, Cols = 4,
                    Widgets = new List<WidgetDef>
                    {
                        new() { Id = "w1", Kind = "clock",   Row = 0, Col = 0, RowSpan = 2, ColSpan = 2 },
                        new() { Id = "w2", Kind = "date",    Row = 0, Col = 2, RowSpan = 2, ColSpan = 2 },
                        new() { Id = "w3", Kind = "cpu",     Row = 2, Col = 0, RowSpan = 2, ColSpan = 2 },
                        new() { Id = "w4", Kind = "mem",     Row = 2, Col = 2, RowSpan = 2, ColSpan = 2 },
                        new() { Id = "w5", Kind = "gpu",     Row = 4, Col = 0, RowSpan = 2, ColSpan = 2 },
                        new() { Id = "w6", Kind = "gputemp", Row = 4, Col = 2, RowSpan = 2, ColSpan = 2 },
                        new() { Id = "w7", Kind = "cputemp", Row = 6, Col = 0, RowSpan = 1, ColSpan = 2 },
                        new() { Id = "w8", Kind = "fps",     Row = 6, Col = 2, RowSpan = 1, ColSpan = 2 },
                        new() { Id = "w9", Kind = "app",     Row = 7, Col = 0, RowSpan = 1, ColSpan = 4 },
                    }
                },
                new()
                {
                    Id = "default", Name = "通用", Rows = 3, Cols = 3,
                    Buttons = new List<ButtonDef>
                    {
                        new() { Id = "b1", Row = 0, Col = 0, Label = "记事本", Icon = "📝", Color = "#2b6cb0",
                                Action = new ActionDef { Type = "app", Path = "notepad.exe" } },
                        new() { Id = "b2", Row = 0, Col = 1, Label = "资源管理器", Icon = "📁", Color = "#2f855a",
                                Action = new ActionDef { Type = "open", Path = U("") } },
                        new() { Id = "b3", Row = 0, Col = 2, Label = "浏览器", Icon = "🌐", Color = "#6b46c1",
                                Action = new ActionDef { Type = "app", Path = "https://www.bing.com" } },
                        new() { Id = "b4", Row = 1, Col = 0, Label = "保存", Icon = "💾",
                                Action = new ActionDef { Type = "hotkey", Keys = new() { "ctrl", "s" } } },
                        new() { Id = "b5", Row = 1, Col = 1, Label = "复制", Icon = "⧉",
                                Action = new ActionDef { Type = "hotkey", Keys = new() { "ctrl", "c" } } },
                        new() { Id = "b6", Row = 1, Col = 2, Label = "粘贴", Icon = "📋",
                                Action = new ActionDef { Type = "hotkey", Keys = new() { "ctrl", "v" } } },
                        new() { Id = "b7", Row = 2, Col = 0, Label = "撤销", Icon = "↶",
                                Action = new ActionDef { Type = "hotkey", Keys = new() { "ctrl", "z" } } },
                        new() { Id = "b8", Row = 2, Col = 1, Label = "全选", Icon = "▣",
                                Action = new ActionDef { Type = "hotkey", Keys = new() { "ctrl", "a" } } },
                        new() { Id = "b9", Row = 2, Col = 2, Label = "回车", Icon = "⏎",
                                Action = new ActionDef { Type = "hotkey", Keys = new() { "enter" } } }
                    }
                },
                new()
                {
                    Id = "code", Name = "写代码", Rows = 3, Cols = 3,
                    Buttons = new List<ButtonDef>
                    {
                        new() { Id = "c1", Row = 0, Col = 0, Label = "保存", Icon = "💾", Color = "#2b6cb0",
                                Action = new ActionDef { Type = "hotkey", Keys = new() { "ctrl", "s" } } },
                        new() { Id = "c2", Row = 0, Col = 1, Label = "终端", Icon = ">_", Color = "#2d3748",
                                Action = new ActionDef { Type = "hotkey", Keys = new() { "ctrl", "`" } } },
                        new() { Id = "c3", Row = 0, Col = 2, Label = "运行", Icon = "▶", Color = "#2f855a",
                                Action = new ActionDef { Type = "hotkey", Keys = new() { "F5" } } },
                        new() { Id = "c4", Row = 1, Col = 0, Label = "注释", Icon = "//",
                                Action = new ActionDef { Type = "hotkey", Keys = new() { "ctrl", "/" } } },
                        new() { Id = "c5", Row = 1, Col = 1, Label = "查找", Icon = "🔍",
                                Action = new ActionDef { Type = "hotkey", Keys = new() { "ctrl", "f" } } },
                        new() { Id = "c6", Row = 1, Col = 2, Label = "全局查找", Icon = "🔎",
                                Action = new ActionDef { Type = "hotkey", Keys = new() { "ctrl", "shift", "f" } } },
                        new() { Id = "c7", Row = 2, Col = 0, Label = "格式化", Icon = "≡",
                                Action = new ActionDef { Type = "hotkey", Keys = new() { "shift", "alt", "f" } } },
                        new() { Id = "c8", Row = 2, Col = 1, Label = "命令面板", Icon = "⌘",
                                Action = new ActionDef { Type = "hotkey", Keys = new() { "ctrl", "shift", "p" } } },
                        new() { Id = "c9", Row = 2, Col = 2, Label = "跑测试", Icon = "🧪",
                                Action = new ActionDef { Type = "sequence", Steps = new()
                                {
                                    new ActionDef { Type = "hotkey", Keys = new() { "ctrl", "`" } },
                                    new ActionDef { Type = "text", Text = "npm test", DelayMs = 300 },
                                    new ActionDef { Type = "hotkey", Keys = new() { "enter" } }
                                } } }
                    }
                },
                new()
                {
                    Id = "media", Name = "影音", Rows = 3, Cols = 3,
                    Buttons = new List<ButtonDef>
                    {
                        new() { Id = "m1", Row = 0, Col = 0, Label = "播放/暂停", Icon = "⏯", Sub = "媒体键",
                                Action = new ActionDef { Type = "hotkey", Keys = new() { "mediaplaypause" } } },
                        new() { Id = "m7", Row = 0, Col = 1, Label = "空格", Icon = "␣", Sub = "给只认空格的播放器",
                                Action = new ActionDef { Type = "hotkey", Keys = new() { "space" } } },
                        new() { Id = "m8", Row = 0, Col = 2, Label = "停止", Icon = "⏹",
                                Action = new ActionDef { Type = "hotkey", Keys = new() { "mediastop" } } },
                        new() { Id = "m2", Row = 1, Col = 0, Label = "音量+", Icon = "🔊",
                                Action = new ActionDef { Type = "hotkey", Keys = new() { "volumeup" } } },
                        new() { Id = "m3", Row = 1, Col = 1, Label = "音量-", Icon = "🔉",
                                Action = new ActionDef { Type = "hotkey", Keys = new() { "volumedown" } } },
                        new() { Id = "m4", Row = 1, Col = 2, Label = "静音", Icon = "🔇",
                                Action = new ActionDef { Type = "hotkey", Keys = new() { "volumemute" } } },
                        new() { Id = "m5", Row = 2, Col = 0, Label = "上一首", Icon = "⏮",
                                Action = new ActionDef { Type = "hotkey", Keys = new() { "mediaprevioustrack" } } },
                        new() { Id = "m6", Row = 2, Col = 1, Label = "下一首", Icon = "⏭",
                                Action = new ActionDef { Type = "hotkey", Keys = new() { "medianexttrack" } } }
                    }
                }
            },
            Mappings = new List<AppMap>
            {
                new() { Process = "code", PageId = "code" },
                new() { Process = "devenv", PageId = "code" },
                new() { Process = "notepad", PageId = "default" },
                new() { Process = "potplayermini64", PageId = "media" },
                new() { Process = "vlc", PageId = "media" },
                new() { Process = "wmplayer", PageId = "media" }
            }
        };
    }
}
