using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using ControlHub;
using Microsoft.Extensions.FileProviders;

// 只允许一个实例：再点一次快捷方式只弹个提示，而不是端口冲突后静默退出
using var singleInstance = new Mutex(true, @"Local\ControlHub.SingleInstance", out var firstInstance);
if (!firstInstance)
{
    MessageBox.Show("ControlHub 已经在运行了（看右下角托盘图标）。", "ControlHub",
        MessageBoxButtons.OK, MessageBoxIcon.Information);
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
    o.ColorBehavior = Microsoft.Extensions.Logging.Console.LoggerColorBehavior.Disabled;
});
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

var contentRoot = builder.Environment.ContentRootPath;

// 配置与上传的图片单独放在程序目录之外的 config 文件夹：
//   D:\DSH\Software\control-hub\config\config.json
//   D:\DSH\Software\control-hub\config\uploads\
// 这样重新编译 / 重新发布（只覆盖 dist）不会碰掉用户自己的配置。
static string ResolveDataDir(string contentRoot)
{
    var env = Environment.GetEnvironmentVariable("CONTROLHUB_HOME");
    if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env.Trim());

    var self = Path.Combine(contentRoot, "config");
    if (Directory.Exists(self)) return self;

    var parent = Directory.GetParent(contentRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    if (parent is not null)
    {
        var sibling = Path.Combine(parent.FullName, "config");
        if (Directory.Exists(sibling)) return sibling;
    }
    return self;
}

var dataDir = ResolveDataDir(contentRoot);
Directory.CreateDirectory(dataDir);
var configPath = Path.Combine(dataDir, "config.json");

var store = new ConfigStore(configPath);
store.Load();

var port = store.Current.Port > 0 ? store.Current.Port : 8080;
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddSingleton(store);
builder.Services.AddSingleton<ActionRunner>();
builder.Services.AddSingleton<Hub>();
builder.Services.AddSingleton<ForegroundMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ForegroundMonitor>());
builder.Services.AddSingleton<StatsCollector>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StatsCollector>());

// 上传的图片也放在 config\uploads，用 /uploads 映射出去
var uploadDir = Path.Combine(dataDir, "uploads");
Directory.CreateDirectory(uploadDir);

var app = builder.Build();

var hub = app.Services.GetRequiredService<Hub>();
var runner = app.Services.GetRequiredService<ActionRunner>();
var monitor = app.Services.GetRequiredService<ForegroundMonitor>();
var stats = app.Services.GetRequiredService<StatsCollector>();
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ControlHub");

// ---------------- 令牌校验 ----------------
// 令牌留空 = 关闭验证（用户可在管理页自己改或清空）
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "/";
    var needAuth = path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
                   || path.Equals("/ws", StringComparison.OrdinalIgnoreCase);
    var expected = store.Current.Token;
    if (needAuth && !string.IsNullOrEmpty(expected))
    {
        var token = ctx.Request.Query["t"].ToString();
        if (string.IsNullOrEmpty(token)) token = ctx.Request.Headers["X-Token"].ToString();
        if (!string.Equals(token, expected, StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "bad token" });
            return;
        }
    }
    await next();
});

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

// 手机端页面与脚本每次都要重新取：否则手机上会一直跑旧缓存，改了逻辑看不到效果
app.Use(async (ctx, next) =>
{
    var p = ctx.Request.Path.Value ?? "";
    if (p is "/" or "/index.html" or "/admin.html" or "/app.js" or "/style.css" or "/manifest.json")
    {
        ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        ctx.Response.Headers["Pragma"] = "no-cache";
        ctx.Response.Headers["Expires"] = "0";
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

// 用户上传的图片（背景、图标）在 config\uploads 里，挂在 /uploads 下
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(uploadDir),
    RequestPath = "/uploads"
});

// ---------------- REST ----------------

app.MapGet("/api/state", (HttpContext ctx) =>
{
    var cfg = store.Current;
    var page = ResolvePageForApp(cfg, monitor.ProcessName) ?? cfg.DefaultPage;
    return Results.Json(new
    {
        token = cfg.Token,
        port = cfg.Port,
        defaultPage = cfg.DefaultPage,
        pages = cfg.Pages,
        mappings = cfg.Mappings,
        activeApp = monitor.ProcessName,
        windowTitle = monitor.WindowTitle,
        currentPage = page,
        clients = hub.Count,
        machine = Environment.MachineName
    }, ConfigStore.JsonOpts);
});

app.MapGet("/api/config", () => Results.Json(store.Current, ConfigStore.JsonOpts));

app.MapPut("/api/config", async (HttpContext ctx) =>
{
    AppConfig? cfg;
    try { cfg = await ctx.Request.ReadFromJsonAsync<AppConfig>(ConfigStore.JsonOpts); }
    catch (Exception ex) { return Results.BadRequest(new { error = "JSON 解析失败: " + ex.Message }); }
    if (cfg is null) return Results.BadRequest(new { error = "空配置" });

    if (cfg.Pages.Count == 0) return Results.BadRequest(new { error = "至少要有一个页面" });
    if (cfg.Pages.Select(p => p.Id).Distinct().Count() != cfg.Pages.Count)
        return Results.BadRequest(new { error = "页面 id 有重复" });

    store.Save(cfg);
    await hub.BroadcastAsync(new
    {
        type = "config",
        pages = cfg.Pages,
        mappings = cfg.Mappings,
        defaultPage = cfg.DefaultPage,
        background = cfg.Background,
        glass = cfg.Glass,
        glassAlpha = cfg.GlassAlpha,
        glassBlur = cfg.GlassBlur
    });
    log.LogInformation("config saved ({Pages} pages, {Maps} mappings, token={Token})",
        cfg.Pages.Count, cfg.Mappings.Count, string.IsNullOrEmpty(cfg.Token) ? "(关闭)" : cfg.Token);
    return Results.Json(new { ok = true, configPath, token = cfg.Token }, ConfigStore.JsonOpts);
});

// 管理端“试一下这个动作”
app.MapPost("/api/action", async (HttpContext ctx) =>
{
    ActionDef? action;
    try { action = await ctx.Request.ReadFromJsonAsync<ActionDef>(ConfigStore.JsonOpts); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
    if (action is null) return Results.BadRequest(new { error = "空动作" });
    try
    {
        var result = await runner.RunAsync(action);
        return Results.Json(new { ok = true, result }, ConfigStore.JsonOpts);
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message }, ConfigStore.JsonOpts);
    }
});

app.MapGet("/api/info", () => Results.Json(new
{
    port,
    token = store.Current.Token,
    urls = LocalUrls(port, store.Current.Token),
    configPath,
    version = "1.9"
}, ConfigStore.JsonOpts));

// 状态页数据
app.MapGet("/api/stats", () => Results.Json(stats.Current, ConfigStore.JsonOpts));

// 图片上传（图标 / 背景）：multipart/form-data，字段名 file
app.MapPost("/api/upload", async (HttpContext ctx) =>
{
    if (!ctx.Request.HasFormContentType)
        return Results.BadRequest(new { error = "需要 multipart/form-data" });

    var form = await ctx.Request.ReadFormAsync();
    var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "没有收到文件" });

    const long maxBytes = 12L * 1024 * 1024;
    if (file.Length > maxBytes)
        return Results.BadRequest(new { error = "图片太大（上限 12 MB）" });

    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    var allowed = new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".ico" };
    if (!allowed.Contains(ext))
        return Results.BadRequest(new { error = $"不支持的格式 {ext}，用 png / jpg / webp / gif" });

    var name = DateTime.Now.ToString("yyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + ext;
    var full = Path.Combine(uploadDir, name);
    await using (var fs = File.Create(full))
        await file.CopyToAsync(fs);

    log.LogInformation("upload {Name} ({Kb} KB)", name, file.Length / 1024);
    return Results.Json(new { ok = true, url = "/uploads/" + name, size = file.Length }, ConfigStore.JsonOpts);
});

// ---------------- WebSocket ----------------

app.Map("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("websocket only");
        return;
    }

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    var conn = new ClientConn
    {
        Socket = socket,
        Remote = ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
        Auto = store.Current.AutoSwitchDefault
    };
    // 新连接默认落在「排在第一位的页面」（没有页面时才退回 DefaultPage）
    conn.Page = store.Current.Pages.Count > 0
        ? store.Current.Pages[0].Id
        : store.Current.DefaultPage;
    var id = hub.Add(conn);

    try
    {
        await hub.SendAsync(conn, new
        {
            type = "hello",
            pages = store.Current.Pages,
            mappings = store.Current.Mappings,
            defaultPage = store.Current.DefaultPage,
            background = store.Current.Background,
            glass = store.Current.Glass,
            glassAlpha = store.Current.GlassAlpha,
            glassBlur = store.Current.GlassBlur,
            currentPage = conn.Page,
            activeApp = monitor.ProcessName,
            windowTitle = monitor.WindowTitle,
            auto = conn.Auto,
            machine = Environment.MachineName,
            serverTime = DateTime.Now.ToString("HH:mm:ss")
        });

        await ReceiveLoop(conn, id, hub, runner, store, monitor, log, ctx.RequestAborted);
    }
    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
    {
        // 手机锁屏/切网常见，正常
    }
    finally
    {
        hub.Remove(id);
        if (socket.State == WebSocketState.Open)
        {
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
            catch { /* ignore */ }
        }
    }
});

// ---------------- 前台程序变化 → 自动切页 ----------------

monitor.Changed += (procName, title) =>
{
    _ = Task.Run(async () =>
    {
        await hub.BroadcastAsync(new { type = "app", app = procName, title });
        foreach (var c in hub.All)
        {
            if (!c.Auto) continue;
            var page = ResolvePageForApp(store.Current, procName);
            if (page is null || page == c.Page) continue;
            c.Page = page;
            await hub.SendAsync(c, new { type = "page", pageId = page, why = "auto", app = procName });
        }
    });
};

// ---------------- 启动 ----------------

await app.StartAsync();

// 每秒把状态数据推给正在看“状态页”的手机
_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(1000);
        try
        {
            if (hub.Count == 0) continue;

            var snapshot = stats.Current;
            foreach (var c in hub.All)
                await hub.SendAsync(c, new { type = "stats", stats = snapshot });
        }
        catch { /* 下一轮再来 */ }
    }
});

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine();
Console.WriteLine("  ┌──────────────────────────────────────────────┐");
Console.WriteLine("  │            ControlHub  局域网控制台           │");
Console.WriteLine("  └──────────────────────────────────────────────┘");
Console.ResetColor();
Console.WriteLine($"  配置文件 : {configPath}");
Console.WriteLine($"  令牌     : {store.Current.Token}");
Console.WriteLine();
foreach (var u in LocalUrls(port, store.Current.Token))
    Console.WriteLine($"  手机访问 : {u}");
Console.WriteLine();
Console.WriteLine("  电脑端管理页面：在上面的地址后面把 / 换成 /admin.html");
Console.WriteLine("  按 Ctrl+C 退出（也可以右键托盘图标退出）");
Console.WriteLine();

var urls = LocalUrls(port, store.Current.Token ?? "");
var appUrl = urls.Count > 0 ? urls[0] : $"http://127.0.0.1:{port}/";
var cut = appUrl.IndexOf("/?t=", StringComparison.Ordinal);
var baseUrl = cut > 0 ? appUrl[..cut] : appUrl.TrimEnd('/');
var adminUrl = $"{baseUrl}/admin.html?t={store.Current.Token}";
var tokenText = store.Current.Token ?? "";

try
{
    File.WriteAllText(Path.Combine(dataDir, "startup.log"),
        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 启动\r\n配置文件: {configPath}\r\n令牌: {tokenText}\r\n手机: {appUrl}\r\n设置: {adminUrl}\r\n");
}
catch { /* 日志写不了就算了 */ }

// 托盘图标跑在单独的 STA 线程上（WinForms 要求），双击显示窗口、右键有菜单
var trayThread = new Thread(() =>
{
    Application.SetHighDpiMode(HighDpiMode.SystemAware);
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    Application.Run(new TrayApp(appUrl, adminUrl, tokenText, configPath, () =>
    {
        try { app.StopAsync().Wait(3000); } catch { /* ignore */ }
        Environment.Exit(0);
    }, args.Contains("--show")));
})
{ IsBackground = true, Name = "controlhub-tray" };
trayThread.SetApartmentState(ApartmentState.STA);
trayThread.Start();

await app.WaitForShutdownAsync();

// ---------------- 局部函数 ----------------

static string? ResolvePageForApp(AppConfig cfg, string procName)
{
    if (string.IsNullOrEmpty(procName)) return null;
    foreach (var m in cfg.Mappings)
    {
        var p = m.Process.Trim();
        if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) p = p[..^4];
        if (string.Equals(p, procName, StringComparison.OrdinalIgnoreCase)) return m.PageId;
    }
    return null;
}

static List<string> LocalUrls(int port, string token)
{
    var list = new List<string>();
    try
    {
        foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
        {
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
            if (IPAddress.IsLoopback(ip)) continue;
            list.Add($"http://{ip}:{port}/?t={token}");
        }
    }
    catch { /* ignore */ }
    list.Add($"http://127.0.0.1:{port}/?t={token}");
    return list;
}

static async Task ReceiveLoop(
    ClientConn conn, Guid id, Hub hub, ActionRunner runner,
    ConfigStore store, ForegroundMonitor monitor, ILogger log, CancellationToken ct)
{
    var buffer = new byte[16 * 1024];
    var sb = new StringBuilder();

    while (conn.Socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
    {
        WebSocketReceiveResult res;
        try { res = await conn.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct); }
        catch { break; }

        if (res.MessageType == WebSocketMessageType.Close) break;

        sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
        if (!res.EndOfMessage) continue;

        var text = sb.ToString();
        sb.Clear();

        try { await HandleMessage(text, conn, hub, runner, store, monitor, log); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "ws message failed");
            await hub.SendAsync(conn, new { type = "toast", level = "err", text = ex.Message });
        }
    }
}

static async Task HandleMessage(
    string text, ClientConn conn, Hub hub, ActionRunner runner,
    ConfigStore store, ForegroundMonitor monitor, ILogger log)
{
    using var doc = JsonDocument.Parse(text);
    var root = doc.RootElement;
    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

    switch (type)
    {
        case "ping":
            await hub.SendAsync(conn, new { type = "pong" });
            break;

        case "goto":
        {
            var pageId = root.GetProperty("pageId").GetString() ?? "";
            if (store.Current.Pages.All(p => p.Id != pageId)) break;
            conn.Page = pageId;
            await hub.SendAsync(conn, new { type = "page", pageId, why = "manual" });
            break;
        }

        case "auto":
        {
            conn.Auto = root.TryGetProperty("enabled", out var e) && e.GetBoolean();
            if (conn.Auto)
            {
                var page = ResolvePageForApp(store.Current, monitor.ProcessName);
                if (page is not null && page != conn.Page)
                {
                    conn.Page = page;
                    await hub.SendAsync(conn, new { type = "page", pageId = page, why = "auto" });
                }
            }
            await hub.SendAsync(conn, new { type = "autoState", auto = conn.Auto });
            break;
        }

        case "press":
        {
            var pageId = root.GetProperty("pageId").GetString() ?? conn.Page;
            var buttonId = root.GetProperty("buttonId").GetString() ?? "";
            var page = store.Current.Pages.FirstOrDefault(p => p.Id == pageId);
            var btn = page?.Buttons.FirstOrDefault(b => b.Id == buttonId);
            if (btn is null)
            {
                await hub.SendAsync(conn, new { type = "toast", level = "err", text = $"按钮 {buttonId} 不存在" });
                break;
            }

            conn.Page = pageId;
            await hub.SendAsync(conn, new { type = "flash", buttonId });

            var action = btn.Action;
            // prompt：手机端弹窗输入的文字随消息带上来，在这里替换成 text 动作
            if (string.Equals(action.Type, "prompt", StringComparison.OrdinalIgnoreCase))
            {
                var typed = root.TryGetProperty("text", out var tx) ? tx.GetString() : "";
                action = new ActionDef
                {
                    Type = "text",
                    Text = typed ?? "",
                    Method = string.IsNullOrEmpty(action.Method) ? "type" : action.Method,
                    DelayMs = action.DelayMs
                };
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await runner.RunAsync(action);
                    var shown = string.IsNullOrWhiteSpace(btn.Label) ? "按钮" : btn.Label;
                    log.LogInformation("press {Page}/{Button} → {Result}", pageId, btn.Label, result);
                    await hub.SendAsync(conn, new { type = "toast", level = "ok",
                        text = $"{shown}：{result}", buttonId });
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "action failed for {Button}", btn.Label);
                    await hub.SendAsync(conn, new { type = "toast", level = "err",
                        text = $"{btn.Label} 失败：{ex.Message}", buttonId });
                }
            });
            break;
        }

        default:
            break;
    }
}
