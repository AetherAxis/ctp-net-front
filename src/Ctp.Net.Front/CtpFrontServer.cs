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

    /// <summary>
    /// 「状态不确定」动作账本（2026-10-29）——<b>进程级单例</b>：一条 CTP 会话死掉时它记录的动作要能
    /// 交给下一条同账户会话去对账，所以不能挂在会话上（会话对象随断连一起没了）。
    /// </summary>
    private readonly FrontPendingLedger _pending;

    /// <summary>同账户活动会话登记（同账户重复登录踢旧会话——见 <see cref="FrontSessionRegistry"/>）。</summary>
    private readonly FrontSessionRegistry _sessions = new();

    public CtpFrontServer(string exchangeHost, int exchangePort, string host, int port, Action<string>? log = null)
    {
        _exchangeHost = exchangeHost;
        _exchangePort = exchangePort;
        _host = host;
        _port = port;
        _log = log ?? (msg => Console.WriteLine($"[CTP-Front] {msg}"));
        _pending = new FrontPendingLedger(log: _log);   // 账本自己的告警（满/过期丢弃）走同一条日志
    }

    public IPEndPoint? LocalEndpoint => _listener?.LocalEndpoint as IPEndPoint;

    /// <summary>测试/诊断钩子：进程级「状态不确定」动作账本（见 <see cref="FrontPendingLedger"/>）。</summary>
    internal FrontPendingLedger PendingLedger => _pending;

    /// <summary>测试/诊断钩子：同账户活动会话登记（见 <see cref="FrontSessionRegistry"/>）。</summary>
    internal FrontSessionRegistry SessionRegistry => _sessions;

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
            var session = new CtpFrontSession(client, new SimRpcClient(_exchangeHost, _exchangePort), _log, _pending, _sessions);
            _ = Task.Run(() => session.RunAsync(ct), ct);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
    }
}
