using System.Net;
using System.Net.Sockets;
using Aether.Exchange.Rpc;

namespace Ctp.Net.Front;

/// <summary>
/// CTP 行情腿前置服务端（M3）：独立端口监听（host 默认 17802，env <c>AETHER_CTP_MD_FRONT_PORT</c>），
/// 每连接一个 <see cref="CtpFrontMdSession"/> + 一条到交易所的 RPC 连接（进程分离 2026-10-05——
/// tick 经 <see cref="SimRpcClient.TickPushed"/> 按订阅集路由；快照/时钟经 `quotes` op）。
/// 行情随柜台：tick 来自同一撮合核心（交易所进程）。
/// </summary>
public sealed class CtpFrontMdServer : IDisposable
{
    private readonly string _exchangeHost;
    private readonly int _exchangePort;
    private readonly string _host;
    private readonly int _port;
    private readonly Action<string> _log;
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private readonly CancellationTokenSource _cts = new();

    public CtpFrontMdServer(string exchangeHost, int exchangePort, string host, int port, Action<string>? log = null)
    {
        _exchangeHost = exchangeHost;
        _exchangePort = exchangePort;
        _host = host;
        _port = port;
        _log = log ?? (msg => Console.WriteLine($"[CTP-Front-MD] {msg}"));
    }

    public IPEndPoint? LocalEndpoint => _listener?.LocalEndpoint as IPEndPoint;

    public void Start()
    {
        var addr = IPAddress.TryParse(_host, out var ip) ? ip : IPAddress.Loopback;
        _listener = new TcpListener(addr, _port);
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _log($"CTP 行情腿前置已启动: {_host}:{_port}");
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; } catch { }
        }
        _log("CTP 行情腿前置已停止");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch { break; }
            var session = new CtpFrontMdSession(client, () => new SimRpcClient(_exchangeHost, _exchangePort), _log);
            _ = Task.Run(() => session.RunAsync(ct), ct);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
    }
}
