namespace ControlHub;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>整个应用的全部可配置状态，序列化为 config.json。</summary>
public sealed class AppConfig
{
    /// <summary>手机端访问令牌，防止同网段其他人乱按。留空 = 关闭验证（同网段谁都能连）。</summary>
    public string Token { get; set; } = "";

    /// <summary>全局背景图地址（/uploads/xxx.jpg）。每页可单独覆盖。</summary>
    public string? Background { get; set; }

    /// <summary>按钮毛玻璃效果（配合背景图用）。每页可单独覆盖。</summary>
    public bool Glass { get; set; }

    /// <summary>毛玻璃的颜色透明度，0–100：0 = 按钮颜色实心，100 = 颜色全透明（只剩模糊）。默认 60。</summary>
    public int GlassAlpha { get; set; } = 60;

    /// <summary>毛玻璃的模糊程度，0–100：0 = 完全不模糊（背景图原样透出），100 = 最糊。默认 50。</summary>
    public int GlassBlur { get; set; } = 50;

    /// <summary>旧版只有一个「透明度」字段，读旧配置时用它换算成玻璃的两个参数；写盘时不再输出。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? GlassOpacity { get; set; }

    public int Port { get; set; } = 8080;

    /// <summary>前台程序不在映射表里时显示的页面。</summary>
    public string DefaultPage { get; set; } = "default";

    /// <summary>自动切页总开关的默认值。</summary>
    public bool AutoSwitchDefault { get; set; } = true;

    public List<PageDef> Pages { get; set; } = new();

    /// <summary>前台进程名 → 页面 id 的映射。</summary>
    public List<AppMap> Mappings { get; set; } = new();
}

public sealed class PageDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>grid（按钮网格，默认）| status（状态页：时间、前台程序、帧率、CPU/GPU/内存）</summary>
    public string Kind { get; set; } = "grid";

    /// <summary>这一页自己的背景图地址；留空则用全局背景。</summary>
    public string? Background { get; set; }

    /// <summary>这一页的毛玻璃开关；null = 跟随全局设置。</summary>
    public bool? Glass { get; set; }

    /// <summary>这一页的颜色透明度 0–100；null = 跟随全局设置。</summary>
    public int? GlassAlpha { get; set; }

    /// <summary>这一页的模糊程度 0–100；null = 跟随全局设置。</summary>
    public int? GlassBlur { get; set; }

    /// <summary>旧版字段，仅用于读旧配置做换算，不再写出。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? GlassOpacity { get; set; }

    public int Rows { get; set; } = 3;
    public int Cols { get; set; } = 3;

    /// <summary>按钮/模块之间的行间距，像素。</summary>
    public double RowGap { get; set; } = 9;

    /// <summary>按钮/模块之间的列间距，像素。</summary>
    public double ColGap { get; set; } = 9;

    /// <summary>深色 / 彩色主题，可选。</summary>
    public string? Accent { get; set; }

    public List<ButtonDef> Buttons { get; set; } = new();

    /// <summary>状态页（Kind = "status"）的显示模块，按底盘网格摆放。</summary>
    public List<WidgetDef> Widgets { get; set; } = new();
}

/// <summary>状态页上的一个显示模块，占底盘网格的一块矩形区域。</summary>
public sealed class WidgetDef
{
    public string Id { get; set; } = "";

    /// <summary>clock | date | app | cpu | mem | gpu | cputemp | gputemp | fps | hz | label</summary>
    public string Kind { get; set; } = "label";

    public int Row { get; set; }
    public int Col { get; set; }
    public int RowSpan { get; set; } = 1;
    public int ColSpan { get; set; } = 1;

    /// <summary>标题，留空时用该类型的默认标题。</summary>
    public string? Title { get; set; }

    /// <summary>Kind = "label" 时显示的静态文字。</summary>
    public string? Text { get; set; }

    /// <summary>模块主色（十六进制），留空用默认。</summary>
    public string? Color { get; set; }
}

public sealed class ButtonDef
{
    public string Id { get; set; } = "";
    public int Row { get; set; }
    public int Col { get; set; }
    public string Label { get; set; } = "";
    public string? Icon { get; set; }

    /// <summary>自定义图标图片地址（/uploads/xxx.png）；设置后优先于 Icon 文字。</summary>
    public string? IconImage { get; set; }

    public string? Color { get; set; }
    public string? Sub { get; set; }

    /// <summary>跨列数（默认 1）。</summary>
    public int ColSpan { get; set; } = 1;

    /// <summary>跨行数（默认 1）。</summary>
    public int RowSpan { get; set; } = 1;

    public ActionDef Action { get; set; } = new();
}

/// <summary>一个按钮按下后要做的事。</summary>
public sealed class ActionDef
{
    /// <summary>none | app | open | text | hotkey | prompt | sequence</summary>
    public string Type { get; set; } = "none";

    /// <summary>app/open: 程序、文件或文件夹路径（可用 %USERPROFILE% 等环境变量）。</summary>
    public string? Path { get; set; }

    /// <summary>app: 启动参数。</summary>
    public string? Args { get; set; }

    /// <summary>text: 要输入的文本（支持中文）。</summary>
    public string? Text { get; set; }

    /// <summary>hotkey: 组合键，例如 ["ctrl","shift","s"]，也接受 "ctrl+shift+s" 字符串。</summary>
    [JsonConverter(typeof(KeyListConverter))]
    public List<string>? Keys { get; set; }

    /// <summary>text: type（逐字输入）或 paste（写剪贴板后 Ctrl+V，兼容性更好）。</summary>
    public string? Method { get; set; }

    /// <summary>sequence: 按顺序执行的子动作。</summary>
    public List<ActionDef>? Steps { get; set; }

    /// <summary>执行前的等待毫秒数，用于等窗口出现。</summary>
    public int DelayMs { get; set; }
}

public sealed class AppMap
{
    /// <summary>进程名，不含 .exe，不区分大小写；也接受 "chrome.exe" 写法。</summary>
    public string Process { get; set; } = "";

    public string PageId { get; set; } = "";
}

/// <summary>让 "keys" 既能写成 ["ctrl","a"]，也能写成 "ctrl+a"。</summary>
public sealed class KeyListConverter : JsonConverter<List<string>>
{
    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        if (reader.TokenType == JsonTokenType.String)
        {
            var raw = reader.GetString() ?? "";
            return raw.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("keys 需要是字符串或字符串数组");

        var list = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            if (reader.TokenType == JsonTokenType.String)
                list.Add(reader.GetString() ?? "");
        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var s in value) writer.WriteStringValue(s);
        writer.WriteEndArray();
    }
}
