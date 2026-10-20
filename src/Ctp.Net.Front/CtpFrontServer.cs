using System.Net;
using System.Net.Sockets;
using Aether.Exchange.Rpc;

namespace Ctp.Net.Front;

/// <summary>
/// CTP 前置服务端：在指定端口监听 CTP 线协议连接，每个连接一个 <see cref="CtpFrontSession"/> 状态机 +
/// 一条到交易所的 RPC 连接（<b>进程分离 2026-10-05，真实世界对齐</b>：前置 = 券商侧接入层，
/// 经 <see cref="SimRpcClient"/> 连交易所——登录绑定账户、推送按连接路由；每客户端会话一条交易所连接）。
/// <para>
/// accept 循环只负责配对「客户端 TCP × 新建 SimRpcClient」——连接/登录由会话按状态机自行驱动。
/// </para>
/// </summary>
public sealed class CtpFrontServer : IDisposable
{
    private readonly string _exchangeHost;
    private readonly int _exchangePort;
    private readonly string _host;
    private readonly int _port;
    private readonly Action<string> _log;
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private readonly CancellationTokenSource _cts = new();

    public CtpFrontServer(string exchangeHost, int exchangePort, string host, int port, Action<string>? log = null)
    {
        _exchangeHost = exchangeHost;
        _exchangePort = exchangePort;
        _host = host;
        _port = port;
        _log = log ?? (msg => Console.WriteLine($"[CTP-Front] {msg}"));
    }

    public IPEndPoint? LocalEndpoint => _listener?.LocalEndpoint as IPEndPoint;

    public void Start()
    {
        var addr = IPAddress.TryParse(_host, out var ip) ? ip : IPAddress.Loopback;
        _listener = new TcpListener(addr, _port);
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _log($"CTP 前置已启动: {_host}:{_port}");
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; } catch { }
        }
        _log("CTP 前置已停止");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch { break; }

            // 每客户端会话一条交易所 RPC 连接（未连接——会话在登录/MD 登录时自行连接）
            var session = new CtpFrontSession(client, new SimRpcClient(_exchangeHost, _exchangePort), _log);
            _ = Task.Run(() => session.RunAsync(ct), ct);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
    }
}
