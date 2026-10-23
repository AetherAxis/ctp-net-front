using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ctp.Net;
using Ctp.Net.Front;
using Aether.Exchange;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// <see cref="RemoteCtpSession"/>（<see cref="ICtpSession"/> 接缝上的仿真适配器）
/// 的契约测试——经 <see cref="LoopbackSim"/>（测试进程内起 host 侧 listener + RPC）**跨线**驱动。
/// <para>（2026-10-23 自 monorepo Acme.Ctp.Simulator.Tests 迁入 Front 仓：适配器属 Front 的客户端半边，
/// Exchange 仓只留裸 RPC 线测试（SimRpcWireTests）。随迁 AccountAuthTests/ExchangeModeTests 中
/// 走 Session 的两条用例——见文件尾部「跨域收编」节。）</para>
/// </summary>
/// <remarks>
/// <para>
/// 上层（<c>CtpTradingService</c> / 6 个 CTP AI 工具 / 桌面「期货」页 / Lean 的 <c>CtpBrokerage</c>）
/// 对 <see cref="ICtpSession"/> 的信任建立在一组**不成文契约**上——它们没有任何一处被编译器检查，
/// 只能靠本文件钉死。最要紧的四条：
/// </para>
/// <list type="number">
/// <item><b>拒单走业务异常（ErrorId 原样）</b>：「未登录」守卫在客户端**本地同步抛**（方法体非 async，
/// Task 返回之前）；服务端风控拒单在 await 时抛——独立 host 形态下线上传输的既定松弛
/// （VM/AI 工具 await+catch、Lean GetResult 全兼容，ErrorId 精度不丢）。</item>
/// <item><b>下单是 fire-and-forget</b>：成功的下单只返回 <c>OrderRef</c>/<c>RequestId</c>，
/// 状态一律经 <c>OrderReceived</c> 异步到达，且**应答先于本单推送**（服务端 capture-during-dispatch
/// ——Lean 的 <c>CtpOrderTracker</c> 在应答后才注册 OrderRef，先到的成交回报会被静默丢弃）。</item>
/// <item><b>同一笔成交有两个回报</b>：先 <c>OrderReceived</c>（状态推进到部分/全部成交）再
/// <c>TradeReceived</c>——次序反了会让 UI 先画出成交再改状态，出现「成交了但委托还挂着」的闪现。
/// 跨线保序：核心按序抛 → 出站通道 FIFO → TCP FIFO → 客户端泵 FIFO。</item>
/// <item><b>查询无登录守卫</b>：未登录查询返回**空账本形态**而非抛异常
/// （远端未连接本会抛 90013——适配器在未登录时本地直答空壳）。</item>
/// </list>
/// <para>
/// 全部用「直构回环柜台 + <see cref="Wait.UntilAsync"/> 等回报」的方式测——
/// 不经过 DI（DI 分流由 <c>CtpInfrastructureExtensions</c> 的环境变量路径覆盖）。
/// </para>
/// </remarks>
public class SessionAdapterTests
{
    /// <summary>不落盘 + ag2610 上架的回环仿真（与 <see cref="SessionHarness.Options"/> 同口径；configure 塑造服务端 options）。</summary>
    /// <remarks><see cref="SimOptions"/> 是 <c>init</c>-only record ⇒ 覆写走 <c>with</c> 表达式；seedMaker 默认注入双边流动性（部分用例自管簿须关）。</remarks>
    private static LoopbackSim NewLoopback(Func<SimOptions, SimOptions>? configure = null, bool seedMaker = true)
        => LoopbackSim.Start(configure, seedMaker);

    /// <summary>
    /// 把「本地同步抛出」的调用收成 <see cref="Action"/>。
    /// </summary>
    /// <remarks>
    /// 直接写 <c>Assert.Throws&lt;T&gt;(() =&gt; session.OrderInsertAsync(...))</c> 会被编译期绑定到 xunit
    /// 已过时的 <c>Assert.Throws&lt;T&gt;(Func&lt;Task&gt;)</c> 重载（CS0619/xUnit2014）——而那条重载恰恰是
    /// **错的语义**（它 await 之后再断言，本文件要钉的就是「未登录异常在 await 之前抛出」）。块体 lambda 返回 void，
    /// 只匹配 <see cref="Action"/> 重载，同时 `_ =` 明确表达「不 await」。
    /// </remarks>
    private static Action FireAndForget(Func<Task> call) => () => { _ = call(); };

    private static CtpLoginRequest LoginRequest(string userId = SessionHarness.Account, string password = "任意") => new()
    {
        FrontAddress = "tcp://127.0.0.1:40001",
        MdFrontAddress = "tcp://127.0.0.1:40002",
        BrokerId = SessionHarness.Broker,
        UserId = userId,
        Password = password,
        AppId = "aether",
        AuthCode = "auth",
    };

    private static CtpOrderRequest Limit(CtpDirection direction, int volume, double price,
        CtpOffsetFlag offset = CtpOffsetFlag.Open)
        => SessionHarness.Limit(direction, volume, price, offset);

    private static CtpOrderRequest Market(CtpDirection direction, int volume,
        CtpOffsetFlag offset = CtpOffsetFlag.Open)
        => SessionHarness.Market(direction, volume, offset);

    // —— 登录 / 登出 ——

    [Fact]
    public async Task 登录返回可被int解析的单号上限与自增的前端会话号()
    {
        using var sim = NewLoopback();
        var session = sim.Session;
        Assert.False(session.IsConnected);
        Assert.Null(session.LoginResult);

        var result = await session.LoginAsync(LoginRequest());

        // 真柜台的 CtpTrader.LoginAsync 拿 MaxOrderRef 做 int.Parse —— 不可解析会直接抛在客户端里
        Assert.True(int.TryParse(result.MaxOrderRef, out var maxRef), $"MaxOrderRef 必须可解析为 int，实际「{result.MaxOrderRef}」");
        Assert.Equal(0, maxRef);                                  // 全新账户单号从 0 起，下单后自增到 1
        // 服务端核心不注入时钟（用的是真实墙钟），故只钉格式不钉值：yyyyMMdd / HH:mm:ss
        Assert.True(DateTime.TryParseExact(result.TradingDay, "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _), $"交易日字段格式不对：「{result.TradingDay}」");
        Assert.True(DateTime.TryParseExact(result.LoginTime, "HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _), $"登录时间字段格式不对：「{result.LoginTime}」");
        Assert.Equal(1, result.FrontId);
        Assert.True(result.SessionId >= 1, "会话号必须为正（真柜台登录成功即应非零）");

        Assert.True(session.IsConnected);
        Assert.Same(result, session.LoginResult);
        Assert.Equal(SessionHarness.Broker, session.BrokerId);
        Assert.Equal(SessionHarness.Account, session.UserId);
    }

    [Fact]
    public async Task 空账号也能登录并被归一为sim账户()
    {
        using var sim = NewLoopback();
        var session = sim.Session;

        await session.LoginAsync(LoginRequest(userId: ""));       // 仿真不校验凭据——目的是「连得上」

        Assert.True(session.IsConnected);
        Assert.Equal("sim", SimTradingCore.AccountIdOf(session.UserId)); // 服务端用同一归一规则算账户键
    }

    [Fact]
    public async Task 登录与登出各恰好外发一次连接状态且次序为连接后断开()
    {
        using var sim = NewLoopback();
        var session = sim.Session;
        var states = new Collector<CtpConnectionStatus>();
        session.ConnectionStateChanged += (_, s) => states.Add(s);

        await session.LoginAsync(LoginRequest());
        Assert.True(await Wait.UntilAsync(() => states.Count >= 1), "登录后必须外发连接状态");

        session.Logout();
        Assert.True(await Wait.UntilAsync(() => states.Count >= 2), "登出后必须外发断开状态");

        // 静默期确认没有第三次（SetStatus 对同值短路；漏了短路会重复外发）
        Assert.Equal(2, await Wait.QuiesceAsync(() => states.Count));
        Assert.Equal(new[] { CtpConnectionStatus.Connected, CtpConnectionStatus.Disconnected }, states.Snapshot.ToArray());

        Assert.False(session.IsConnected);
        Assert.Null(session.LoginResult);
        Assert.Null(session.BrokerId);
        Assert.Null(session.UserId);
    }

    [Fact]
    public async Task 登出保留账本持仓而订阅标志不依赖连接()
    {
        using var sim = NewLoopback();
        var session = sim.Session;
        await session.LoginAsync(LoginRequest());
        await session.SubscribeMarketDataAsync(MdConnect(), [SessionHarness.Instrument]);

        await session.OrderInsertAsync(Market(CtpDirection.Buy, 1));   // 吃 maker 挂单——立即全成交
        var opened = Assert.Single(await session.QueryPositionAsync());

        session.Logout();

        // 「已订阅」= RPC 连接存活（Logout 不断 TCP，行情恒由 host 推送——不依赖任何前置连接）
        // ⇒ 登出后仍为 true。被清掉的是服务端订阅清单；本地行情缓存保留（下个用例钉的正是这条语义）。
        Assert.True(session.IsMdSubscribed);
        Assert.False(session.IsConnected);

        // 账本跨登出**保留**（服务端核心的账户状态 GetOrCreate，登出只解绑连接不销账户，下次登录续用）。
        // 刻意不断言 Balance——手续费与浮动盈亏都会挪动它；「持仓仍在、占用仍在」才是恒真的不变量。
        await session.LoginAsync(LoginRequest());
        var account = await session.QueryAccountAsync();
        Assert.Equal(SessionHarness.Account, account.AccountId);
        Assert.True(account.CurrMargin > 0, "登出重登不得释放持仓保证金占用");

        var position = Assert.Single(await session.QueryPositionAsync());
        Assert.Equal(SessionHarness.Instrument, position.InstrumentId);
        Assert.Equal(opened.TotalPosition, position.TotalPosition);
    }

    // —— 未登录：查询给空账本，交易同步拒 ——

    [Fact]
    public async Task 未登录查询返回空账本形态而非抛异常()
    {
        using var sim = NewLoopback();
        var session = sim.Session;

        var account = await session.QueryAccountAsync();

        // 适配器本地直答：未登录不起 RPC（远端未连接会抛 90013），账户键归一 "sim"，返回**空壳**（不抛、不为 null）
        Assert.Equal("sim", account.AccountId);
        Assert.Equal(0, account.Balance, 2);
        Assert.Equal(0, account.Available, 2);
        Assert.Equal(0, account.CurrMargin, 2);

        Assert.Empty(await session.QueryPositionAsync());
        Assert.Empty(await session.QueryOrderAsync());
        Assert.Null(session.GetLatestTick(SessionHarness.Instrument));
        Assert.Empty(session.GetAllLatestTicks());
    }

    [Fact]
    public void 未登录下单同步抛业务异常()
    {
        using var sim = NewLoopback();
        var session = sim.Session;

        // 关键：未登录守卫在**客户端本地**、返回 Task 之前抛出——故 Assert.Throws 直接套在调用上，
        // 不是 Assert.ThrowsAsync（FireAndForget 的用途与理由见其注释）。不为一发 RPC 破坏同步契约。
        var ex = Assert.Throws<CtpBusinessException>(
            FireAndForget(() => session.OrderInsertAsync(Market(CtpDirection.Buy, 1))));
        Assert.Equal(SimErrorCodes.NotLoggedIn, ex.ErrorId);
        Assert.False(string.IsNullOrWhiteSpace(ex.ErrorMsg));
    }

    [Fact]
    public void 未登录撤单同步抛业务异常()
    {
        using var sim = NewLoopback();
        var session = sim.Session;

        var bogus = new CtpOrderRef(SessionHarness.Broker, SessionHarness.Account, 1, 1, "000000000001", SessionHarness.Exchange);
        var ex = Assert.Throws<CtpBusinessException>(FireAndForget(() => session.OrderActionAsync(bogus)));
        Assert.Equal(SimErrorCodes.NotLoggedIn, ex.ErrorId);
    }

    [Fact]
    public async Task 登录后风控拒单在await时抛业务异常且不留痕()
    {
        using var sim = NewLoopback();
        var session = sim.Session;
        await session.LoginAsync(LoginRequest());

        // 服务端拒单（手数非正）经 wire 回 err 帧、客户端重建 CtpBusinessException——ErrorId 原样。
        // 独立 host 形态的既定松弛：同步抛 → await 时抛（消费方全部兼容，见类注释契约①）。
        var ex = await Assert.ThrowsAsync<CtpBusinessException>(
            () => session.OrderInsertAsync(Market(CtpDirection.Buy, 0)));
        Assert.Equal(SimErrorCodes.InvalidVolume, ex.ErrorId);
        Assert.False(string.IsNullOrWhiteSpace(ex.ErrorMsg));

        // 拒单不消耗单号：下一笔合法单仍拿到 1
        var inserted = await session.OrderInsertAsync(Market(CtpDirection.Buy, 1));
        Assert.Equal("000000000001", inserted.OrderRef);
    }

    // —— 下单 → 回报（异步泵） ——

    [Fact]
    public async Task 市价单的回报序列为受理先于成交_成交先于终态且回报级联到全成交()
    {
        using var sim = NewLoopback();
        var session = sim.Session;
        await session.LoginAsync(LoginRequest());

        var orders = new Collector<CtpOrder>();
        var trades = new Collector<CtpTrade>();
        var interleaved = new Collector<string>();
        session.OrderReceived += (_, o) => { orders.Add(o); interleaved.Add($"order:{(char)o.OrderStatusRaw}"); };
        session.TradeReceived += (_, t) => { trades.Add(t); interleaved.Add($"trade:{t.Volume}"); };

        var inserted = await session.OrderInsertAsync(Market(CtpDirection.Buy, 1));

        Assert.True(await Wait.UntilAsync(() => trades.Count >= 1), "市价单必须成交");
        Assert.Equal(2, await Wait.QuiesceAsync(() => orders.Count));   // 恰好两次状态：已报入 + 全成交

        var statuses = orders.Snapshot.Select(o => (char)o.OrderStatusRaw).ToArray();
        Assert.Equal(new[] { '3', '0' }, statuses);                     // 已报入 → 全部成交
        // 受理 '3' 先于成交（OrderRef 配对）→ 成交先于终态 '0'（真 CTP 惯例——tracker 依赖 Trade 到齐再终态；
        // 旧序终态先抛 ⇒ 攻击方高频幻影补齐，2026-10-17 换序修复）
        Assert.Equal(new[] { "order:3", "trade:1", "order:0" }, interleaved.Snapshot.ToArray());

        // 成交前的报单回报已带正确的合约/方向/单号（UI 的委托列表靠它建行）
        var accepted = orders.Snapshot[0];
        Assert.Equal(SessionHarness.Instrument, accepted.InstrumentId);
        Assert.Equal(SessionHarness.Exchange, accepted.ExchangeId);
        Assert.Equal(inserted.OrderRef, accepted.OrderRef);
        Assert.Equal(CtpDirection.Buy, accepted.Direction);
        Assert.Equal(CtpOffsetFlag.Open, accepted.OffsetFlag);
        Assert.Equal(1, accepted.VolumeTotalOriginal);
        Assert.Equal(0, accepted.VolumeTraded);
        // FrontId/SessionId 是客户端撤单定位键的两段（CtpOrderRef），必须与登录回显同源
        Assert.Equal(session.LoginResult!.FrontId, accepted.FrontId);
        Assert.Equal(session.LoginResult.SessionId, accepted.SessionId);

        var filled = orders.Snapshot[^1];
        Assert.Equal(1, filled.VolumeTraded);
        Assert.Equal(0, filled.VolumeTotal);
        Assert.Equal(accepted.OrderSysId, filled.OrderSysId);            // 同一笔报单，系统号不变（跨线 ⇒ 值相等）
        Assert.False(string.IsNullOrEmpty(filled.OrderSysId), "系统号非空（跨线后不再能引用同一实例，须值断言）");

        var trade = trades.Snapshot[0];
        Assert.Equal(inserted.OrderRef, trade.OrderRef);
        Assert.Equal(accepted.OrderSysId, trade.OrderSysId);
        Assert.Equal(1, trade.Volume);
        Assert.Equal(CtpDirection.Buy, trade.Direction);
        Assert.Equal("7001", trade.Price.ToString("0.####"));           // 买进吃 maker 卖侧挂单 7001
    }

    [Fact]
    public async Task 部分成交时每笔成交后跟状态回报且终态最后()
    {
        // 部分成交由「对手方分批供给」驱动：GFD 限价买 3 在空簿静息入簿，
        // 对手方（lp 账户）每次插 1 张等价反向单 ⇒ 吃掉 1 手 ⇒ 逐笔推进到全成
        using var sim = NewLoopback(seedMaker: false);
        var session = sim.Session;
        await session.LoginAsync(LoginRequest());

        var orders = new Collector<CtpOrder>();
        var trades = new Collector<CtpTrade>();
        var interleaved = new Collector<string>();
        session.OrderReceived += (_, o) => { orders.Add(o); interleaved.Add("order"); };
        session.TradeReceived += (_, t) => { trades.Add(t); interleaved.Add("trade"); };

        await session.OrderInsertAsync(Limit(CtpDirection.Buy, 3, 7001));   // 空簿 ⇒ 静息入簿

        sim.Core.InsertOrder("lp", SessionHarness.DomainLimit(Aether.Exchange.Direction.Sell, 1, 7001));
        sim.Core.InsertOrder("lp", SessionHarness.DomainLimit(Aether.Exchange.Direction.Sell, 1, 7001));
        sim.Core.InsertOrder("lp", SessionHarness.DomainLimit(Aether.Exchange.Direction.Sell, 1, 7001));

        Assert.True(await Wait.UntilAsync(() => trades.Count >= 3), "3 手逐手成交");
        Assert.Equal(4, await Wait.QuiesceAsync(() => orders.Count));   // 已报入 + 3 次推进

        // 3 手 × (成交 + 状态) 交错的完整序列——受理先于每笔成交、每笔成交后跟状态推进、终态 '0' 收尾
        //（真 CTP 惯例序：成交先于终态——tracker 依赖 Trade 到齐再终态；旧序 2026-10-17 换序修复）
        Assert.Equal(new[] { "order", "trade", "order", "trade", "order", "trade", "order" }, interleaved.Snapshot.ToArray());

        var statuses = orders.Snapshot.Select(o => (char)o.OrderStatusRaw).ToArray();
        Assert.Equal(new[] { '3', '1', '1', '0' }, statuses);           // 已报入 → 部分成交 ×2 → 全成交
        Assert.Equal(new[] { 0, 1, 2, 3 }, orders.Snapshot.Select(o => o.VolumeTraded).ToArray());
        Assert.Equal(new[] { 3, 2, 1, 0 }, orders.Snapshot.Select(o => o.VolumeTotal).ToArray());
        Assert.Equal(new[] { "1", "2", "3" }, trades.Snapshot.Select(t => t.TradeId).ToArray());
    }

    [Fact]
    public async Task 不穿价的限价单只有一笔已报入回报()
    {
        using var sim = NewLoopback();
        var session = sim.Session;
        await session.LoginAsync(LoginRequest());

        var orders = new Collector<CtpOrder>();
        var trades = new Collector<CtpTrade>();
        session.OrderReceived += (_, o) => orders.Add(o);
        session.TradeReceived += (_, t) => trades.Add(t);

        // 买限价 6900 < maker 卖一 7001 ⇒ 挂得住（静息入簿，等对手方来吃）
        await session.OrderInsertAsync(Limit(CtpDirection.Buy, 1, 6900));

        Assert.True(await Wait.UntilAsync(() => orders.Count >= 1));
        Assert.Equal(1, await Wait.QuiesceAsync(() => orders.Count));   // 静默期后仍只有一笔——没有隐含成交
        Assert.Equal('3', (char)orders.Snapshot[0].OrderStatusRaw);
        Assert.Equal(1, orders.Snapshot[0].VolumeTotal);
        Assert.Empty(trades.Snapshot);
    }

    [Fact]
    public async Task 撤单推进到已撤并释放挂单回报()
    {
        using var sim = NewLoopback();
        var session = sim.Session;
        await session.LoginAsync(LoginRequest());

        var orders = new Collector<CtpOrder>();
        session.OrderReceived += (_, o) => orders.Add(o);

        var inserted = await session.OrderInsertAsync(Limit(CtpDirection.Buy, 2, 6900));
        Assert.True(await Wait.UntilAsync(() => orders.Count >= 1));

        var pending = orders.Snapshot[^1];
        Assert.Equal(2, pending.VolumeTotal);

        await session.OrderActionAsync(SessionHarness.RefOf(pending));

        Assert.True(await Wait.UntilAsync(() => orders.Snapshot.Any(o => (char)o.OrderStatusRaw == '5')), "撤单必须回报已撤状态");
        var canceled = orders.Snapshot.First(o => (char)o.OrderStatusRaw == '5');
        Assert.Equal(0, canceled.VolumeTotal);
        Assert.Equal(0, canceled.VolumeTraded);
        Assert.Equal(inserted.OrderRef, canceled.OrderRef);
    }

    [Fact]
    public async Task 查询回显仿真账本与撤单后的委托()
    {
        using var sim = NewLoopback();
        var session = sim.Session;
        await session.LoginAsync(LoginRequest());

        // 撮合与账本更新都在服务端处理 insertOrder 期间**同步**完成（应答帧在其后），
        // 故应答返回后的查询无需等待
        await session.OrderInsertAsync(Market(CtpDirection.Buy, 1));

        var account = await session.QueryAccountAsync();
        Assert.Equal(SessionHarness.Account, account.AccountId);
        Assert.Equal(12601.8, account.CurrMargin, 2);                   // maker 卖一 7001 × 乘数 15 × 保证金率 12%

        var positions = await session.QueryPositionAsync();
        var position = Assert.Single(positions);
        Assert.Equal(SessionHarness.Instrument, position.InstrumentId);
        Assert.Equal(1, position.TotalPosition);

        var orders = await session.QueryOrderAsync();
        var order = Assert.Single(orders);
        Assert.Equal(1, order.VolumeTraded);                             // 全部成交
        Assert.Equal(0, order.VolumeTotal);

        // instrumentId 过滤：不匹配的合约返回空（真柜台由前置过滤，仿真在核心内判）
        Assert.Empty(await session.QueryPositionAsync("cu2610"));
        Assert.Empty(await session.QueryOrderAsync("cu2610"));
    }

    // —— 行情 ——

    [Fact]
    public async Task 登录后即视为已订阅且不依赖前置连接()
    {
        using var sim = NewLoopback();
        var session = sim.Session;

        // 连接（Hello）未建立前 false——须先连接/登录才谈得上「已订阅」
        Assert.False(session.IsMdSubscribed);
        await session.LoginAsync(LoginRequest());

        // 行情随柜台：行情由 host RPC 提供（不走任何前置连接），连接存活即「已订阅」——不依赖订阅动作
        Assert.True(session.IsMdSubscribed);
    }

    [Fact]
    public async Task 订阅即激活行情并缓存tick()
    {
        using var sim = NewLoopback(o => o with
        {
            ListedInstruments = new[] { SessionHarness.Instrument, "rb2610" },   // 第二合约须上架才能激活
        });
        var session = sim.Session;
        await session.LoginAsync(LoginRequest());

        var ticks = new Collector<CtpTick>();
        session.TickReceived += (_, t) => ticks.Add(t);

        await session.SubscribeMarketDataAsync(MdConnect(), ["ag2610", "rb2610"]);

        // 订阅即激活 ⇒ 激活即推快照（maker 双边挂单在簿 ⇒ ag2610 盘口有价；rb2610 空簿 ⇒ 零值快照也推）
        Assert.True(await Wait.UntilAsync(() => session.GetLatestTick("ag2610") is not null), "订阅后必须收到该合约行情");
        Assert.True(await Wait.UntilAsync(() => session.GetLatestTick("rb2610") is not null), "同批订阅的第二个合约也要有行情");

        // 行情事件驱动（无定时器续流）——登出清服务端订阅路由且**不清**本地行情缓存
        // （正是这条语义——登出后 GetLatestTick 仍可读）。
        session.Logout();
        await Wait.QuiesceAsync(() => ticks.Count);

        var tick = session.GetLatestTick("ag2610")!;
        Assert.Equal("ag2610", tick.InstrumentId);
        Assert.Equal("SHFE", tick.ExchangeId);

        // 服务端核心不注入时钟（用的是真实墙钟），故只钉格式不钉值：yyyyMMdd
        Assert.True(DateTime.TryParseExact(tick.TradingDay, "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _), $"交易日字段格式不对：「{tick.TradingDay}」");

        Assert.Equal(6990, tick.PreSettlement, 6);                      // ag2610 昨结算
        Assert.True(tick.AskPrice > tick.BidPrice, $"盘口必须买低卖高：{tick.BidPrice}/{tick.AskPrice}");   // maker 6999/7001

        // 行情缓存与事件同步：GetLatestTick 拿到的就是最后一帧 TickReceived（订阅路由已停 ⇒ 此刻比较无竞态）
        var last = ticks.Snapshot.LastOrDefault(t => t.InstrumentId == "ag2610");
        Assert.NotNull(last);
        Assert.Equal(last!.LastPrice, tick.LastPrice, 6);

        Assert.Equal(2, session.GetAllLatestTicks().Count);
    }

    [Fact]
    public async Task 重复订阅不产生重复合约也不重置已有行情()
    {
        using var sim = NewLoopback(o => o with
        {
            ListedInstruments = new[] { SessionHarness.Instrument, "cu2610" },   // 增量订阅的第二合约须上架
        });
        var session = sim.Session;
        await session.LoginAsync(LoginRequest());
        await session.SubscribeMarketDataAsync(MdConnect(), ["ag2610"]);
        Assert.True(await Wait.UntilAsync(() => session.GetLatestTick("ag2610") is not null));

        // 一笔成交推动行情（last=7001、volume=1——会话统计是真实成交的累计，不归零）
        sim.Core.InsertOrder("taker", SessionHarness.DomainMarket(Aether.Exchange.Direction.Buy, 1));
        Assert.True(await Wait.UntilAsync(() => session.GetLatestTick("ag2610")!.Volume > 0
            && Math.Abs(session.GetLatestTick("ag2610")!.LastPrice - 7001) < 1e-9));
        var before = session.GetLatestTick("ag2610")!;

        await session.SubscribeMarketDataAsync(MdConnect(), ["ag2610", "cu2610"]);   // 增量订阅

        Assert.True(await Wait.UntilAsync(() => session.GetLatestTick("cu2610") is not null));
        Assert.Equal(2, session.GetAllLatestTicks().Count);

        // 已订阅过的合约**不被重置**：重复激活不重置会话统计（最新价/累计量延续，不归零回昨结算）。
        var after = session.GetLatestTick("ag2610")!;
        Assert.True(after.Volume >= before.Volume && Math.Abs(after.LastPrice - before.LastPrice) < 1e-9,
            "重复订阅不得重置已有行情（会话统计应延续而非归零）");
    }

    [Fact]
    public async Task 默认配置订阅后即收到tick推送()
    {
        // 行情随柜台的默认 pin：无任何开关/塑造，订阅 ⇒ host 恒推送（激活即推快照——maker 挂单在簿）
        using var sim = NewLoopback();
        var session = sim.Session;
        await session.LoginAsync(LoginRequest());

        var ticks = new Collector<CtpTick>();
        session.TickReceived += (_, t) => ticks.Add(t);

        await session.SubscribeMarketDataAsync(MdConnect(), [SessionHarness.Instrument]);

        Assert.True(await Wait.UntilAsync(() => session.GetLatestTick(SessionHarness.Instrument) is not null),
            "默认（无开关）订阅后必须收到行情（激活快照）");
        Assert.All(ticks.Snapshot, t => Assert.Equal(SessionHarness.Instrument, t.InstrumentId));
    }

    [Fact]
    public async Task 订阅非种子合约经品种派生激活行情()
    {
        // 期货页行情板订阅全部主力合约（~250 个），绝大多数不在种子 9 合约表内——
        // 上市表的自定义 id 须经下单同款解析链（品种派生/合成）补元数据，否则板面只有零星几格有价
        using var sim = NewLoopback(o => o with { ListedInstruments = new[] { "rb2605", "zz9999" } }, seedMaker: false);
        var session = sim.Session;
        await session.LoginAsync(LoginRequest());

        await session.SubscribeMarketDataAsync(MdConnect(), ["rb2605", "zz9999"]);   // 品种派生 + 未知品种合成

        Assert.True(await Wait.UntilAsync(() => session.GetLatestTick("rb2605") is not null),
            "同品种非种子合约（rb2605）必须经品种派生激活出行情");
        Assert.True(await Wait.UntilAsync(() => session.GetLatestTick("zz9999") is not null),
            "未知品种合约（zz9999）必须经合成兜底激活出行情（上市表解析链含合成）");
    }

    [Fact]
    public async Task QueryInstrumentsAsync返回host上市表()
    {
        // 默认上市表 = ax001（须显式把 ListedInstruments 归 null——SessionHarness 默认上架 ag2610 供交易用例）。
        // RPC JSON 路径透传完整 ExchangeId "AETHER"（CTP 帧模板路径的 3 字符截断是另一条腿的事——见 E2eM3）。
        using var sim = NewLoopback(o => o with { ListedInstruments = null }, seedMaker: false);
        var session = sim.Session;
        await session.LoginAsync(LoginRequest());

        var list = await session.QueryInstrumentsAsync();

        var sim001 = Assert.Single(list);   // 默认 = ax001（Aether 币价期货——2026-10-08 起默认品种；原 zx001）
        Assert.Equal("ax001", sim001.InstrumentId);
        Assert.Equal("AETHER", sim001.ExchangeId);
        Assert.Equal(1, sim001.VolumeMultiple);
        Assert.Equal(0.01, sim001.PriceTick);
        Assert.Equal(100, sim001.PreSettlement, 6);
    }

    [Fact]
    public async Task QueryInstrumentsAsync返回自定义上市表()
    {
        using var sim = NewLoopback(o => o with { ListedInstruments = new[] { "ag2610", "xx2601" } }, seedMaker: false);
        var session = sim.Session;
        await session.LoginAsync(LoginRequest());

        var list = await session.QueryInstrumentsAsync();

        Assert.Equal(2, list.Count);
        Assert.Contains(list, i => i.InstrumentId == "xx2601" && i.BasePrice == 100);   // 未知品种合成兜底
        Assert.Contains(list, i => i.InstrumentId == "ag2610" && i.ExchangeId == "SHFE");
    }

    [Fact]
    public async Task 双客户端不同账户互为对手撮合()
    {
        // 用户场景 E2E：两个客户端（不同账户）连同一 host——A 挂单、B 吃单，被动方空闲中收到自己的成交推送
        // （纯两客户端簿——alice 的挂单就是 bob 的对手价，无须 maker 种子）
        using var sim = NewLoopback(seedMaker: false);
        var alice = sim.Session;
        await alice.LoginAsync(LoginRequest(userId: "alice"));
        using var bob = new RemoteCtpSession("127.0.0.1", sim.Server.LocalEndpoint!.Port);
        await bob.LoginAsync(LoginRequest(userId: "bob"));

        var aliceOrders = new Collector<CtpOrder>();
        var aliceTrades = new Collector<CtpTrade>();
        alice.OrderReceived += (_, o) => aliceOrders.Add(o);
        alice.TradeReceived += (_, t) => aliceTrades.Add(t);

        // alice 挂限价买 7000（空簿 ⇒ 静息入簿）
        await alice.OrderInsertAsync(SessionHarness.Limit(CtpDirection.Buy, 1, 7000));
        var resting = Assert.Single(await alice.QueryOrderAsync());
        Assert.Equal((byte)'3', resting.OrderStatusRaw);

        // bob 市价卖 ⇒ 吃 alice 的挂单（maker 价 7000）
        await bob.OrderInsertAsync(SessionHarness.Market(CtpDirection.Sell, 1));

        // alice 空闲中收到被动成交推送（订单 + 成交，来自 bob 的 op 线程直写）
        Assert.True(await Wait.UntilAsync(() => aliceTrades.Count > 0), "被动方必须收到成交推送");
        Assert.Equal(7000, aliceTrades.Snapshot[0].Price, 6);
        Assert.Contains(aliceOrders.Snapshot, o => o.OrderStatusRaw == (byte)'0');

        // 双方三查镜像
        var alicePosition = Assert.Single(await alice.QueryPositionAsync());
        Assert.Equal(CtpPosiDirection.Long, alicePosition.PosiDirection);
        Assert.Equal(1, alicePosition.TotalPosition);
        var bobPosition = Assert.Single(await bob.QueryPositionAsync());
        Assert.Equal(CtpPosiDirection.Short, bobPosition.PosiDirection);
        Assert.Equal(1, bobPosition.TotalPosition);
        var bobOrder = Assert.Single(await bob.QueryOrderAsync());
        Assert.Equal((byte)'0', bobOrder.OrderStatusRaw);
        Assert.Equal(1, bobOrder.VolumeTraded);
    }

    // —— 释放 ——

    [Fact]
    public async Task 释放幂等且释放后不再外发回调()
    {
        using var sim = NewLoopback();
        var session = sim.Session;
        await session.LoginAsync(LoginRequest());

        var orders = new Collector<CtpOrder>();
        session.OrderReceived += (_, o) => orders.Add(o);
        await session.OrderInsertAsync(Market(CtpDirection.Buy, 1));
        Assert.True(await Wait.UntilAsync(() => orders.Count >= 2));

        var after = orders.Count;
        session.Dispose();
        session.Dispose();                                              // 幂等：第二次不得抛

        // 泵已停（完成写端）+ RPC 已断 ⇒ 后续推送不再外发
        Assert.Equal(after, await Wait.QuiesceAsync(() => orders.Count));
    }

    // —— 跨域收编（2026-10-23 自 Exchange 侧 AccountAuthTests/ExchangeModeTests 迁入的 Session 级用例）——

    [Fact]
    public async Task 凭据失败连接不绑定账户()
    {
        using var sim = NewLoopback(o => o with { PasswordIterations = 1000 });
        sim.Core.Login(SessionHarness.Broker, "dave", "pw");   // 服务端侧直接开户设密（余额 1e6）

        var ex = await Assert.ThrowsAsync<CtpBusinessException>(
            () => sim.Session.LoginAsync(LoginRequest(userId: "dave", password: "bad")));
        Assert.Equal(SimErrorCodes.BadCredentials, ex.ErrorId);

        // 连接未绑上 dave：查询走未登录本地空账（契约④），不是 dave 的 1e6
        var account = await sim.Session.QueryAccountAsync();
        Assert.Equal(0, account.Balance, 6);
    }

    [Fact]
    public async Task 订阅即收到盘口快照_wire级路由()
    {
        // 回归（2026-09-28 实测踩中）：交易所模式空闲市场的唯一一帧 = 订阅激活时的盘口快照。
        // 曾因「先激活后登记订阅」被路由吞掉 ⇒ 订阅者板恒空（挂单明明在簿里）。
        // 修法 = 先登记订阅再激活——快照落进本 op 的 capture-during-dispatch 缓冲，应答帧后送达。
        // 做市商以外部客户端身份接入——此处经 core 直下等价模拟其挂单。
        using var sim = NewLoopback(o => o with { PasswordIterations = 1000 }, seedMaker: false);
        // 参与者双边挂单（任意普通账户——直接经 core 下单，服务端视角与外部客户端经 wire 下单等价）
        sim.Core.InsertOrder("mm", new Aether.Exchange.OrderRequest
        {
            BrokerId = SessionHarness.Broker,
            InvestorId = "mm",
            InstrumentId = SessionHarness.Instrument,
            Direction = Aether.Exchange.Direction.Buy,
            OffsetFlag = Aether.Exchange.OffsetFlag.Open,
            PriceType = Aether.Exchange.OrderPriceType.Limit,
            LimitPrice = 6988,
            Volume = 5,
            TimeCondition = Aether.Exchange.TimeCondition.GFD,
        });
        sim.Core.InsertOrder("mm", new Aether.Exchange.OrderRequest
        {
            BrokerId = SessionHarness.Broker,
            InvestorId = "mm",
            InstrumentId = SessionHarness.Instrument,
            Direction = Aether.Exchange.Direction.Sell,
            OffsetFlag = Aether.Exchange.OffsetFlag.Open,
            PriceType = Aether.Exchange.OrderPriceType.Limit,
            LimitPrice = 6992,
            Volume = 5,
            TimeCondition = Aether.Exchange.TimeCondition.GFD,
        });

        var session = sim.Session;
        await session.LoginAsync(LoginRequest());

        var ticks = new Collector<CtpTick>();
        session.TickReceived += (_, t) => ticks.Add(t);

        await session.SubscribeMarketDataAsync(MdConnect(), [SessionHarness.Instrument]);

        // 订阅完成后必须立即见到做市双边盘口（无定时器续命、无后续市场事件——全靠激活快照）
        Assert.True(
            await Wait.UntilAsync(() => session.GetLatestTick(SessionHarness.Instrument) is { BidPrice: > 0, AskPrice: > 0 }),
            "订阅后必须立即收到盘口快照");
        var tick = session.GetLatestTick(SessionHarness.Instrument)!;
        Assert.Equal(6988, tick.BidPrice, 6);
        Assert.Equal(6992, tick.AskPrice, 6);
        Assert.Equal(5, tick.BidVolume);
        Assert.Equal(5, tick.AskVolume);
    }

    private static CtpMarketDataConnect MdConnect() => new()
    {
        MdFrontAddress = "tcp://127.0.0.1:40002",
        BrokerId = SessionHarness.Broker,
        UserId = SessionHarness.Account,
        Password = "任意",
    };
}
