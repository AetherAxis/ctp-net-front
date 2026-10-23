using System.Collections.Concurrent;

using Ctp.Net;
using Ctp.Net.Front;
using Aether.Exchange;
using Aether.Exchange.Rpc;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// Session 契约测试夹具（2026-10-23 自 monorepo Acme.Ctp.Simulator.Tests/SimHarness 迁入 Front 仓）：
/// 确定性时钟 + 纯内存存储 + maker 双边挂单 + <b>带 <see cref="RemoteCtpSession"/></b> 的进程内回环。
/// <para>
/// Exchange 仓的同名夹具测的是裸 RPC 线（SimRpcClient）；本仓这份钉的是 ICtpSession 接缝——
/// 适配器（RemoteCtpSession）是 Front 的客户端半边，契约测试随之归 Front。
/// 工厂方法返回 <see cref="CtpOrderRequest"/>（协议侧）——与 Exchange 侧域类型工厂分工对应。
/// </para>
/// </summary>
internal static class SessionHarness
{
    /// <summary>2026-09-23 是周三，10:00 落在日盘 [09:00,10:15) 内 ⇒ <c>IsTradingTime</c> 为 true。</summary>
    public static readonly DateTime TradingNow = new(2026, 9, 23, 10, 0, 0);

    public const string Instrument = "ag2610";
    public const string Account = "tester";
    public const string Broker = "9999";

    /// <summary>ag2610 的交易所（品种种子表里 ag 属上期所）——撤单定位键的回显项。</summary>
    public const string Exchange = "SHFE";

    /// <summary>maker 双边挂单价（买 <see cref="Bid"/> / 卖 <see cref="Ask"/>）——市价单按对手侧成交的确定性来源。</summary>
    public const double Bid = 6999;
    public const double Ask = 7001;
    public const double Last = 7000;

    /// <summary>ag2610 的合约元数据（<c>InstrumentCatalog</c> 种子）：乘数 15 / 保证金率 12% / 变动价位 1。</summary>
    public const double VolumeMultiple = 15;
    public const double MarginRate = 0.12;

    public const double InitialBalance = 1_000_000;

    /// <summary>测试登录密码（密码必填——2026-09-28 起 <c>Login</c> 无密码一律拒 90017）。</summary>
    public const string TestPassword = "test-pw";

    /// <summary>maker 账户（「庄家」的替身：双边挂限价单供给流动性——价格发现归簿下流动性唯一来源是挂单）。</summary>
    public const string MakerAccount = "maker";

    /// <summary>maker 双边挂单的每侧手数（30×2 手 × 单手保证金 ≈ 75.6 万 < 初始 100 万——双边同时冻结须留足余量）。</summary>
    public const int MakerVolume = 30;

    /// <summary>默认参数：不落盘 + ag2610 上架 + 显式沙盒水龙头 1e6（平台默认已改 0——比特币模型）。</summary>
    public static SimOptions Options() => new() { DatabasePath = null, ListedInstruments = new[] { Instrument }, InitialBalance = InitialBalance };

    public static async Task<SimTradingCore> NewCoreAsync(
        SimOptions? options = null,
        bool seedMaker = true,
        DateTime? now = null)
    {
        var core = new SimTradingCore(options ?? Options(), new SimClock(() => now ?? TradingNow));
        await core.InitializeAsync();
        if (seedMaker) SeedMaker(core);
        return core;
    }

    /// <summary>maker 账户双边挂单（买 <see cref="Bid"/> × volumePerSide / 卖 <see cref="Ask"/> × volumePerSide）。</summary>
    public static void SeedMaker(SimTradingCore core, int volumePerSide = MakerVolume)
    {
        core.InsertOrder(MakerAccount, DomainLimit(Aether.Exchange.Direction.Buy, volumePerSide, Bid));
        core.InsertOrder(MakerAccount, DomainLimit(Aether.Exchange.Direction.Sell, volumePerSide, Ask));
    }

    public static ExchangeLoginResult Login(SimTradingCore core) => core.Login(Broker, Account, TestPassword);

    // ── 协议侧工厂方法（ICtpSession 接缝用——对应 Exchange 侧域类型工厂）──

    public static CtpOrderRequest Limit(
        CtpDirection direction,
        int volume,
        double price,
        CtpOffsetFlag offset = CtpOffsetFlag.Open,
        CtpTimeCondition timeCondition = CtpTimeCondition.GFD,
        string instrument = Instrument)
        => new()
        {
            BrokerId = Broker,
            InvestorId = Account,
            InstrumentId = instrument,
            Direction = direction,
            OffsetFlag = offset,
            PriceType = CtpOrderPriceType.Limit,
            LimitPrice = price,
            Volume = volume,
            TimeCondition = timeCondition,
        };

    public static CtpOrderRequest Market(
        CtpDirection direction,
        int volume,
        CtpOffsetFlag offset = CtpOffsetFlag.Open,
        CtpTimeCondition timeCondition = CtpTimeCondition.GFD,
        string instrument = Instrument)
        => new()
        {
            BrokerId = Broker,
            InvestorId = Account,
            InstrumentId = instrument,
            Direction = direction,
            OffsetFlag = offset,
            PriceType = CtpOrderPriceType.Market,
            LimitPrice = 0,
            Volume = volume,
            TimeCondition = timeCondition,
        };

    public static CtpOrderRef RefOf(CtpOrder order)
        => new(Broker, Account, order.FrontId, order.SessionId, order.OrderRef, order.ExchangeId);

    // ── 域类型工厂（Exchange 核心直调路径——测试进程内经 core 下单驱动对手方时用）──

    public static Aether.Exchange.OrderRequest DomainLimit(Aether.Exchange.Direction direction, int volume, double price,
        Aether.Exchange.OffsetFlag offset = Aether.Exchange.OffsetFlag.Open,
        string instrument = Instrument)
        => new()
        {
            BrokerId = Broker,
            InvestorId = Account,
            InstrumentId = instrument,
            Direction = direction,
            OffsetFlag = offset,
            PriceType = Aether.Exchange.OrderPriceType.Limit,
            LimitPrice = price,
            Volume = volume,
            TimeCondition = Aether.Exchange.TimeCondition.GFD,
        };

    public static Aether.Exchange.OrderRequest DomainMarket(Aether.Exchange.Direction direction, int volume,
        Aether.Exchange.OffsetFlag offset = Aether.Exchange.OffsetFlag.Open,
        string instrument = Instrument)
        => new()
        {
            BrokerId = Broker,
            InvestorId = Account,
            InstrumentId = instrument,
            Direction = direction,
            OffsetFlag = offset,
            PriceType = Aether.Exchange.OrderPriceType.Market,
            LimitPrice = 0,
            Volume = volume,
            TimeCondition = Aether.Exchange.TimeCondition.GFD,
        };
}

/// <summary>线程安全的回报收集器——断言时必须先取快照。</summary>
internal sealed class Collector<T>
{
    private readonly object _gate = new();
    private readonly List<T> _items = [];

    public void Add(T item)
    {
        lock (_gate) _items.Add(item);
    }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    public IReadOnlyList<T> Snapshot
    {
        get { lock (_gate) return _items.ToList(); }
    }
}

internal static class Wait
{
    public static async Task<bool> UntilAsync(Func<bool> predicate, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate()) return true;
            await Task.Delay(10);
        }
        return predicate();
    }

    public static async Task<int> QuiesceAsync(Func<int> count, int quietMs = 250)
    {
        await Task.Delay(quietMs);
        return count();
    }
}

/// <summary>
/// 回环仿真（Session 契约版）：测试进程内起「host 侧」listener + <see cref="RemoteCtpSession"/>——
/// 跨线驱动 ICtpSession 接缝，无需外部进程。Dispose = 关会话 + 停 listener + core.Dispose。
/// </summary>
internal sealed class LoopbackSim : IDisposable
{
    public SimTradingCore Core { get; }
    public SimRpcServer Server { get; }
    public RemoteCtpSession Session { get; }

    private LoopbackSim(SimTradingCore core, SimRpcServer server, RemoteCtpSession session)
    {
        Core = core;
        Server = server;
        Session = session;
    }

    public static LoopbackSim Start(Func<SimOptions, SimOptions>? configure = null, bool seedMaker = true)
    {
        var options = configure is null ? SessionHarness.Options() : configure(SessionHarness.Options());
        var core = new SimTradingCore(options, new SimClock(() => SessionHarness.TradingNow));
        core.InitializeAsync().GetAwaiter().GetResult();   // xunit 无同步上下文 ⇒ sync-over-async 安全
        if (seedMaker) SessionHarness.SeedMaker(core);
        var server = new SimRpcServer(core, "127.0.0.1", 0);
        server.Start();
        var session = new RemoteCtpSession("127.0.0.1", server.LocalEndpoint!.Port);
        return new LoopbackSim(core, server, session);
    }

    public void Dispose()
    {
        try { Session.Dispose(); } catch { }
        try { Server.Dispose(); } catch { }
        try { Core.Dispose(); } catch { }
    }
}
