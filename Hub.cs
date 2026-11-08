using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ControlHub;

/// <summary>一个手机端连接。</summary>
public sealed class ClientConn
{
    public required WebSocket Socket { get; init; }
    public SemaphoreSlim SendLock { get; } = new(1, 1);

    /// <summary>这个手机是否跟随前台程序自动切页。</summary>
    public bool Auto { get; set; } = true;

    /// <summary>这个手机当前显示的页面。</summary>
    public string Page { get; set; } = "";

    public string Remote { get; init; } = "";
}

public sealed class Hub
{
    private readonly ConcurrentDictionary<Guid, ClientConn> _clients = new();
    private readonly ILogger<Hub> _log;

    public Hub(ILogger<Hub> log) => _log = log;

    public int Count => _clients.Count;

    public Guid Add(ClientConn c)
    {
        var id = Guid.NewGuid();
        _clients[id] = c;
        _log.LogInformation("client + {Remote} (total {N})", c.Remote, _clients.Count);
        return id;
    }

    public void Remove(Guid id)
    {
        if (_clients.TryRemove(id, out _)) _log.LogInformation("client - (total {N})", _clients.Count);
    }

    public IEnumerable<ClientConn> All => _clients.Values;

    public async Task SendAsync(ClientConn c, object payload)
    {
        var json = JsonSerializer.Serialize(payload, ConfigStore.JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        await c.SendLock.WaitAsync();
        try
        {
            if (c.Socket.State == WebSocketState.Open)
                await c.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "send failed");
        }
        finally { c.SendLock.Release(); }
    }

    public async Task BroadcastAsync(object payload)
    {
        foreach (var c in _clients.Values)
            await SendAsync(c, payload);
    }
}
