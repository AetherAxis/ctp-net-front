using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Ctp.Net;
using Aether.Exchange;
using Aether.Exchange.Rpc;

namespace Ctp.Net.Front;

/// <summary>
/// 本地仿真柜台的**远程**会话适配器（JSON-RPC 参考客户端）：把独立 host 进程（Exchange.Host，经
/// <see cref="SimRpcClient"/> 连 127.0.0.1）接到 <see cref="ICtpSession"/> 接缝上。
/// <para>
/// 结构：SimRpcClient（收发 Exchange Domain 类型） + <see cref="CtpDomainMapper"/>（Domain ↔ Ctp*）
/// + 泵 Channel + 后台回调线程、tick 本地缓存、状态去重、未登录同步抛。
/// </para>
/// <para>
/// <b>行情随柜台</b>：sim 登录的行情**恒由 host RPC 推送**，本类**无本地 MD 腿**。
/// <c>CtpMarketDataConnect</c> 参数被本实现忽略（真实后端仍需要同一签名）。
/// </para>
/// <para>
/// <b>契约差异</b>：服务端风控拒单从「Task 返回前同步抛」变为「await 时抛」；
/// 「未登录」守卫保留**客户端本地同步抛**；host 未启动 ⇒ <c>CtpBusinessException(90012)</c>；
/// host 死亡 ⇒ <see cref="ConnectionStateChanged"/> 发 Disconnected、后续操作快速失败 90013。
/// 无自动重连——重新登录即重连。
/// </para>
/// </summary>
public sealed class RemoteCtpSession : ICtpSession, IDisposable
{
    private const int NotLoggedIn = 90001;
    private const int CallbackQueueCapacity = 4096;

    private readonly SimRpcClient _client;
    private readonly object _lock = new();
    private readonly Channel<Callback> _pump = Channel.CreateBounded<Callback>(
        new BoundedChannelOptions(CallbackQueueCapacity)
        {
            SingleReader = true, SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    private CtpLoginResult? _loginResult;
    private string? _brokerId;
    private string? _userId;
    private CtpConnectionStatus _status = CtpConnectionStatus.Disconnected;
    private bool _disposed;

    private readonly List<string> _cachedSymbols = new();
    private readonly ConcurrentDictionary<string, CtpTick> _latestTicks = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct Callback(
        CtpOrder? Order = null,
        CtpTrade? Trade = null,
        CtpTick? Tick = null,
        CtpConnectionStatus? Status = null);

    public RemoteCtpSession(string host, int port)
    {
        _client = new SimRpcClient(host, port);
        _client.OrderPushed += o => Enqueue(new Callback(Order: CtpDomainMapper.ToCtpOrder(o)));
        _client.TradePushed += t => Enqueue(new Callback(Trade: CtpDomainMapper.ToCtpTrade(t)));
        _client.TickPushed += k => Enqueue(new Callback(Tick: CtpDomainMapper.ToCtpTick(k)));
        _client.ConnectionLost += OnConnectionLost;
        _ = Task.Run(PumpAsync);
    }

    // —— ICtpSession：状态 ——

    public bool IsConnected { get { lock (_lock) return _loginResult is not null; } }
    public bool IsMdSubscribed => _client.IsAlive;
    public CtpLoginResult? LoginResult { get { lock (_lock) return _loginResult; } }
    public string? BrokerId { get { lock (_lock) return _brokerId; } }
    public string? UserId { get { lock (_lock) return _userId; } }

    public event EventHandler<CtpOrder>? OrderReceived;
    public event EventHandler<CtpTrade>? TradeReceived;
    public event EventHandler<CtpTick>? TickReceived;
    public event EventHandler<CtpConnectionStatus>? ConnectionStateChanged;

    // —— ICtpSession：登录 / 登出 ——

    public async Task<CtpLoginResult> LoginAsync(CtpLoginRequest request, CancellationToken ct = default)
    {
        EnsureNotDisposed();
        await _client.ConnectAsync(ct).ConfigureAwait(false);

        ExchangeLoginResult result;
        try
        {
            result = await _client.LoginAsync(
                new LoginArg(request.BrokerId, request.UserId, request.Password), ct).ConfigureAwait(false);
        }
        catch (ExchangeBusinessException ex)
        {
            throw new CtpBusinessException(ex.ErrorId, ex.Message);
        }

        var ctpResult = CtpDomainMapper.ToCtpLoginResult(result);
        lock (_lock)
        {
            _loginResult = ctpResult;
            _brokerId = request.BrokerId;
            _userId = request.UserId;
        }
        SetStatus(CtpConnectionStatus.Connected);
        return ctpResult;
    }

    public void Logout()
    {
        UnsubscribeMarketData();
        lock (_lock) { _loginResult = null; _brokerId = null; _userId = null; }
        _client.Logout();
        SetStatus(CtpConnectionStatus.Disconnected);
    }

    // —— ICtpSession：查询 ——

    public Task<CtpAccount> QueryAccountAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_loginResult is null)
                return Task.FromResult(new CtpAccount { AccountId = AccountKey(_userId) });
        }
        return MapAsync(_client.QueryAccountAsync(ct), CtpDomainMapper.ToCtpAccount);
    }

    public Task<IReadOnlyList<CtpPosition>> QueryPositionAsync(string? instrumentId = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_loginResult is null)
                return Task.FromResult<IReadOnlyList<CtpPosition>>(Array.Empty<CtpPosition>());
        }
        return MapAsync(_client.QueryPositionsAsync(instrumentId, ct), list => (IReadOnlyList<CtpPosition>)list.Select(CtpDomainMapper.ToCtpPosition).ToList());
    }

    public Task<IReadOnlyList<CtpOrder>> QueryOrderAsync(string? instrumentId = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_loginResult is null)
                return Task.FromResult<IReadOnlyList<CtpOrder>>(Array.Empty<CtpOrder>());
        }
        return MapAsync(_client.QueryOrdersAsync(instrumentId, ct), list => (IReadOnlyList<CtpOrder>)list.Select(CtpDomainMapper.ToCtpOrder).ToList());
    }

    public async Task<IReadOnlyList<CtpInstrument>> QueryInstrumentsAsync(CancellationToken ct = default)
    {
        EnsureNotDisposed();
        await _client.ConnectAsync(ct).ConfigureAwait(false);
        var domain = await _client.QueryInstrumentsAsync(ct).ConfigureAwait(false);
        return domain.Select(CtpDomainMapper.ToCtpInstrument).ToList();
    }

    private static string AccountKey(string? userId)
        => string.IsNullOrWhiteSpace(userId) ? "sim" : userId;

    // —— ICtpSession：下单 / 撤单 ——

    public async Task<CtpOrderInsertResult> OrderInsertAsync(CtpOrderRequest request, CancellationToken ct = default)
    {
        EnsureLoggedIn();
        var domainReq = CtpDomainMapper.ToDomainOrderRequest(request);
        try
        {
            var domain = await _client.InsertOrderAsync(domainReq, ct).ConfigureAwait(false);
            return new CtpOrderInsertResult(domain.ClientOrderRef, domain.RequestId);
        }
        catch (ExchangeBusinessException ex)
        {
            throw new CtpBusinessException(ex.ErrorId, ex.Message);
        }
    }

    public async Task OrderActionAsync(CtpOrderRef orderRef, CancellationToken ct = default)
    {
        EnsureLoggedIn();
        var cancelKey = CtpDomainMapper.ToDomainCancelKey(orderRef);
        try
        {
            await _client.CancelOrderAsync(cancelKey, ct).ConfigureAwait(false);
        }
        catch (ExchangeBusinessException ex)
        {
            throw new CtpBusinessException(ex.ErrorId, ex.Message);
        }
    }

    // —— ICtpSession：行情 ——

    public async Task SubscribeMarketDataAsync(CtpMarketDataConnect connect, IReadOnlyList<string> symbols, CancellationToken ct = default)
    {
        EnsureNotDisposed();
        await _client.ConnectAsync(ct).ConfigureAwait(false);
        lock (_lock) _cachedSymbols.AddRange(symbols.Where(s => !_cachedSymbols.Contains(s)));
        await _client.SubscribeAsync(symbols, ct).ConfigureAwait(false);
    }

    public void UnsubscribeMarketData()
    {
        lock (_lock) _cachedSymbols.Clear();
        if (_client.IsAlive) _client.Unsubscribe();
    }

    public CtpTick? GetLatestTick(string instrumentId)
        => _latestTicks.TryGetValue(instrumentId, out var t) ? t : null;

    public IReadOnlyDictionary<string, CtpTick> GetAllLatestTicks()
        => new Dictionary<string, CtpTick>(_latestTicks);

    // —— 内部 ——

    private static async Task<T2> MapAsync<T1, T2>(Task<T1> task, Func<T1, T2> mapper)
    {
        try { return mapper(await task.ConfigureAwait(false)); }
        catch (ExchangeBusinessException ex) { throw new CtpBusinessException(ex.ErrorId, ex.Message); }
    }

    private void Enqueue(Callback cb) => _pump.Writer.TryWrite(cb);

    private async Task PumpAsync()
    {
        try
        {
            while (await _pump.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_pump.Reader.TryRead(out var cb))
                {
                    try
                    {
                        if (cb.Order is { } o) OrderReceived?.Invoke(this, o);
                        if (cb.Trade is { } t) TradeReceived?.Invoke(this, t);
                        if (cb.Tick is { } k)
                        {
                            _latestTicks[k.InstrumentId] = k;
                            TickReceived?.Invoke(this, k);
                        }
                        if (cb.Status is { } s) ConnectionStateChanged?.Invoke(this, s);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine($"[CTP-SIM] 回调订阅方异常（已忽略）：{ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[CTP-SIM] 回调泵退出：{ex.Message}");
        }
    }

    private void OnConnectionLost()
    {
        lock (_lock) { _loginResult = null; _brokerId = null; _userId = null; }
        SetStatus(CtpConnectionStatus.Disconnected);
    }

    private void SetStatus(CtpConnectionStatus status)
    {
        lock (_lock) { if (_status == status) return; _status = status; }
        Enqueue(new Callback(Status: status));
    }

    private void EnsureLoggedIn()
    {
        lock (_lock)
        {
            if (_loginResult is null)
                throw new CtpBusinessException(NotLoggedIn, "未登录，无法交易。");
        }
    }

    private void EnsureNotDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RemoteCtpSession));
    }

    public void Dispose()
    {
        lock (_lock) { if (_disposed) return; _disposed = true; }
        _pump.Writer.TryComplete();
        try { UnsubscribeMarketData(); } catch { }
        try { _client.Dispose(); } catch { }
    }
}
