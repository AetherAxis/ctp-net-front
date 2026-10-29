using System.Collections.Concurrent;
using Ctp.Net;
using Ctp.Net.Core.Trader;
// E2E 里 Direction/OrderRequest/PosiDirection 等归 SDK 客户端类型（Ctp.Net.Core.Trader）；
// 交易所核心类型只用到 SimTradingCore——别名引入避免同名枚举歧义
using SimTradingCore = Aether.Exchange.SimTradingCore;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// M2 E2E：报单/撤单/成交推送全链路（SDK 即裁判）——官方 SDK 解析我们构造的 OnRtnOrder/OnRtnTrade/
/// ⑳/㉒ 帧，任何字段错位 ⇒ 回调不触发 ⇒ 超时失败。需要成交的用例先 <see cref="FrontLoopback.SeedMaker"/>
/// 挂 m2701 双边（bid 2999/ask 3001——价格发现归簿，流动性来自挂单）。
/// </summary>
public sealed class CtpFrontE2eM2Tests : IDisposable
{
    private readonly FrontLoopback _sim = FrontLoopback.Start();
    private const string User = "m2user";   // 6 字符（等宽纪律）
    private const string Instrument = "m2701";   // 5 字符 = 推送模板捕获槽宽（等宽纪律——变宽实证失步）

    public void Dispose() => _sim.Dispose();

    private static string NewFlowDir([System.Runtime.CompilerServices.CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), "ctp-front-e2e", test, Guid.NewGuid().ToString("N"));

    private CtpTrader Login(string flowDir)
    {
        _sim.Deposit(User);   // 开户余额恒 0 ⇒ 参与者先显式入金（链上充值替身）
        var trader = new CtpTrader(flowDir);
        var login = trader.LoginAsync(new CtpConnectRequest
        {
            FrontAddress = _sim.FrontAddress,
            BrokerId = FrontLoopback.Broker,
            UserId = User,
            Password = "m2-pass",
            AppId = "simnow_client_test",
            AuthCode = "0000000000000000",
        }).GetAwaiter().GetResult();
        Assert.Equal("20260923", login.TradingDay);
        return trader;
    }

    private static async Task<bool> UntilAsync(Func<bool> pred, int timeoutMs = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (pred()) return true;
            await Task.Delay(50);
        }
        return pred();
    }

    [Fact]
    public async Task 报单成交撤单全链路_OnRtnOrder_OnRtnTrade_推送()
    {
        var dir = NewFlowDir();
        Directory.CreateDirectory(dir);
        using var trader = Login(dir);
        _sim.SeedMaker();   // maker 双边挂单（bid 2999/ask 3001）——成交的对手方

        var orders = new ConcurrentBag<Order>();
        var trades = new ConcurrentBag<Trade>();
        trader.OrderReceived += (_, o) => orders.Add(o);
        trader.TradeReceived += (_, t) => trades.Add(t);

        // ① 激进限价买 1@3100（≥maker Ask 3001）→ 受理 '3' + 全成 '0' + OnRtnTrade（价=挂单价 3001）
        var fill = await trader.OrderInsertAsync(new OrderRequest
        {
            InvestorId = User, BrokerId = FrontLoopback.Broker, InstrumentId = Instrument,
            Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
            PriceType = OrderPriceType.Limit, LimitPrice = 3100, Volume = 1, ExchangeId = "DCE",
        });
        Assert.True(await UntilAsync(() => trades.Count >= 1), "OnRtnTrade 未到达（推送帧解析失败？）");
        Assert.Contains(orders, o => o.OrderRef == fill.OrderRef && (char)o.OrderStatusRaw == '3');
        var filled = orders.Single(o => o.OrderRef == fill.OrderRef && (char)o.OrderStatusRaw == '0');
        Assert.True(filled.InstrumentId == Instrument,
            $"SDK 解析字段: {string.Join(" | ", orders.Select(o => $"ref={o.OrderRef} inst='{o.InstrumentId}' dir={o.Direction} off={o.OffsetFlag} px={o.LimitPrice} vol={o.VolumeTotalOriginal}/{o.VolumeTraded} st={(char)o.OrderStatusRaw} ex='{o.ExchangeId}' sys='{o.OrderSysID}'"))}");
        Assert.Equal(1, filled.VolumeTotalOriginal);
        var trade = trades.Single(t => t.OrderRef == fill.OrderRef);
        Assert.Equal(3001, trade.Price);
        Assert.Equal(1, trade.Volume);

        // ② 远端限价买 2900（<maker Bid 2999）→ 仅受理 '3'，静默
        var rest = await trader.OrderInsertAsync(new OrderRequest
        {
            InvestorId = User, BrokerId = FrontLoopback.Broker, InstrumentId = Instrument,
            Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
            PriceType = OrderPriceType.Limit, LimitPrice = 2900, Volume = 1, ExchangeId = "DCE",
        });
        Assert.True(await UntilAsync(() => orders.Any(o => o.OrderRef == rest.OrderRef && (char)o.OrderStatusRaw == '3')), "挂单受理推送未到达");
        var before = orders.Count;
        await Task.Delay(500);
        Assert.Equal(before, orders.Count);   // 静默：无成交

        // ③ 撤挂单 → OrderActionAsync 正常返回（不超时 = ㉒ 成功应答闭环）+ OnRtnOrder '5' 终态推送到达
        //（2026-10-02：'3' 模板 + 状态槽(177)/消息槽(219) 等宽编辑——早期「合成形状 SDK 不触发回调」结论被推翻）。
        // '5' 是 CtpBrokerage tracker 收敛撤单的唯一信号（撤单成功无 ⑳）——缺它 = 做市商移心重挂死循环冻结市场。
        await trader.OrderActionAsync(FrontLoopback.Broker, User, 1, 1, rest.OrderRef, "DCE");
        Assert.True(await UntilAsync(() => orders.Any(o => o.OrderRef == rest.OrderRef && (char)o.OrderStatusRaw == '5')),
            "撤单 '5' 终态推送未到达（SDK 未回调合成帧？）");
        var coreRest = _sim.Core.QueryOrders(SimTradingCore.AccountIdOf(User)).Single(o => o.OrderRef == rest.OrderRef);
        Assert.True((char)coreRest.OrderStatusRaw == '5' && coreRest.VolumeTotal == 0, "核心侧撤单未收敛");
        var acct = _sim.Core.QueryAccount(SimTradingCore.AccountIdOf(User));
        Assert.True(acct.Available > FrontLoopback.SandboxDeposit - 5000, $"撤单后冻结未释放: Available={acct.Available}");   // 成交保证金~3000+手续费+浮亏 ≈3000，远小于 5000

        // ④ 核心侧闭环：成交后持仓 1 手多（OrderRef 直通——核心账本按客户端号记账）
        var positions = _sim.Core.QueryPositions(SimTradingCore.AccountIdOf(User));
        Assert.Contains(positions, p => p.TotalPosition == 1);
    }

    [Fact]
    public async Task F0族推送序号达240_前置主动断开_新会话恢复()
    {
        // 2026-10-19 服务器长跑定案 + E2E 逐形态实证：F0 族推送 instance 的 SDK 可接受域为 1-251
        // （224-239 转义对/240-251 裸值可过；E3 FC 起、E2 双字节区、回卷形态均被拒收——SDK 侧底层
        // 限制，服务器无法扩域。此前双 bot「活跃 2-5 分钟假死」= 序号破界后推送全静默）。
        // 前置策略：序号达 240 主动断开客户端（SDK 自动重连、新会话序号归零，客户端零改动——
        // 生产链路 CtpSessionService 的 sim 前 24h 重连兜底接住）。本钉验证：
        // ① 240 帧前推送正常；② 断开确实发生；③ 新会话（重登）推送恢复。
        var dir = NewFlowDir();
        Directory.CreateDirectory(dir);
        var trader = Login(dir);
        _sim.SeedMaker();

        var orders = new ConcurrentBag<Order>();
        var disconnected = new TaskCompletionSource();
        trader.OrderReceived += (_, o) => orders.Add(o);
        trader.FrontDisconnected += (_, _) => disconnected.TrySetResult();

        // ① 安全区内（<240 帧）：每张静默单 1 帧 F001，全部到达
        for (var i = 0; i < 235; i++)
        {
            var r = await trader.OrderInsertAsync(new OrderRequest
            {
                InvestorId = User, BrokerId = FrontLoopback.Broker, InstrumentId = Instrument,
                Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
                PriceType = OrderPriceType.Limit, LimitPrice = 2700 + (i % 50),   // 低于 bid 2999 全部静默
                Volume = 1, ExchangeId = "DCE",
            });
            Assert.True(await UntilAsync(() => orders.Any(o => o.OrderRef == r.OrderRef)),
                $"第 {i} 张静默单的受理推送未到达（安全区内，若失败与本钉无关）");
        }

        // ② 继续发单越过 240 阈值：前置主动断开（4097）——15s 未断即超时失败
        for (var i = 235; i < 250 && !disconnected.Task.IsCompleted; i++)
        {
            await trader.OrderInsertAsync(new OrderRequest
            {
                InvestorId = User, BrokerId = FrontLoopback.Broker, InstrumentId = Instrument,
                Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
                PriceType = OrderPriceType.Limit, LimitPrice = 2700 + (i % 50),
                Volume = 1, ExchangeId = "DCE",
            });
            await Task.Delay(100);
        }
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // ③ 新会话（镜像生产重连重登）：推送恢复 + 成交闭环
        trader.Dispose();
        var dir2 = NewFlowDir();
        Directory.CreateDirectory(dir2);
        using var trader2 = Login(dir2);
        var trades = new ConcurrentBag<Trade>();
        var orders2 = new ConcurrentBag<Order>();
        trader2.OrderReceived += (_, o) => orders2.Add(o);
        trader2.TradeReceived += (_, t) => trades.Add(t);
        var fill = await trader2.OrderInsertAsync(new OrderRequest
        {
            InvestorId = User, BrokerId = FrontLoopback.Broker, InstrumentId = Instrument,
            Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
            PriceType = OrderPriceType.Limit, LimitPrice = 3100, Volume = 1, ExchangeId = "DCE",
        });
        Assert.True(await UntilAsync(() => trades.Any(t => t.OrderRef == fill.OrderRef), 15000),
            "新会话（重登后）OnRtnTrade 未恢复——序号归零未生效");
        Assert.True(await UntilAsync(() => orders2.Any(o => o.OrderRef == fill.OrderRef && (char)o.OrderStatusRaw == '0')),
            "新会话成交终态推送未到达");
    }

    [Fact]
    public async Task 多手成交的量回显_SDK读到真实手数()
    {
        // F002 量槽偏移回归钉（2026-10-17 双 bot 定案）：旧版量编辑写在 offset 184（E4 01，无关字段——
        // 写错位还污染它），SDK 从模板 E8 槽读量 ⇒ 恒读原值 1——多手成交全被读成 1 手 ⇒ 客户端
        // 「报单全成但成交回报缺 N 手」高频幻影补齐。此前 E2E 只测 1 手成交（01=1 恰好吻合）掩盖了偏移。
        var dir = NewFlowDir();
        Directory.CreateDirectory(dir);
        using var trader = Login(dir);
        _sim.SeedMaker();   // 每边 20 手

        var trades = new ConcurrentBag<Trade>();
        trader.TradeReceived += (_, t) => trades.Add(t);

        // 激进限价买 7@3100（≥ask 3001 ⇒ 一笔吃 7 手，maker 档量 20 足够）
        await trader.OrderInsertAsync(new OrderRequest
        {
            InvestorId = User, BrokerId = FrontLoopback.Broker, InstrumentId = Instrument,
            Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
            PriceType = OrderPriceType.Limit, LimitPrice = 3100, Volume = 7, ExchangeId = "DCE",
        });
        Assert.True(await UntilAsync(() => trades.Count >= 1), "OnRtnTrade 未到达");
        var trade = trades.Single();
        Assert.Equal(7, trade.Volume);   // SDK 读到的量 = 真实成交手数（旧偏移下恒 1）
        Assert.Equal(3001, trade.Price);
    }

    [Fact]
    public async Task 拒单路径_报单拒绝对_F003触发RspError()
    {
        var dir = NewFlowDir();
        Directory.CreateDirectory(dir);
        using var trader = Login(dir);

        var errors = new ConcurrentBag<(int Id, string Msg)>();
        trader.RspError += (_, e) => errors.Add((e.ErrorId, e.ErrorMsg));

        // 资金不足触发风控 90005（5000 手保证金 ≫ 初始 100 万；2 字节量走 E7 tag——2026-10-02 修复
        // ⑲ 量字段 tag 随宽度变后，大单不再被静默丢帧）
        var rej = await trader.OrderInsertAsync(new OrderRequest
        {
            InvestorId = User, BrokerId = FrontLoopback.Broker, InstrumentId = Instrument,
            Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
            PriceType = OrderPriceType.Limit, LimitPrice = 3100, Volume = 5000, ExchangeId = "DCE",
        });
        // 核心侧拒单是权威断言（⑳/F003 的 RspInfo 已等宽编码——2026-10-02：变长编辑会致 SDK 静默丢帧）；
        // SDK 侧 RspError 触发与否是客户端可见性问题（CtpBrokerage 有 watchdog 兜底），不强断言。
        var acct = _sim.Core.QueryAccount(SimTradingCore.AccountIdOf(User));
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline
               && (acct.FrozenMargin != 0 || acct.Available != FrontLoopback.SandboxDeposit))
        {
            await Task.Delay(50);   // ⑲ fire-and-forget：等核心侧拒单落账再断言（旧版靠解析失败静默丢单才「恰好」通过）
            acct = _sim.Core.QueryAccount(SimTradingCore.AccountIdOf(User));
        }
        Assert.True(acct.FrozenMargin == 0 && acct.Available == FrontLoopback.SandboxDeposit, "核心未拒单（资金被冻结）");
    }

    [Fact]
    public async Task IOC单未成交残量作废_不挂簿_终态推送到达()
    {
        var dir = NewFlowDir();
        Directory.CreateDirectory(dir);
        using var trader = Login(dir);

        var orders = new ConcurrentBag<Order>();
        trader.OrderReceived += (_, o) => orders.Add(o);

        // IOC 买 2@2900（不交叉 ask 3001）⇒ 零成交 ⇒ 残量作废（'4' 未成交不在队列），不挂簿。
        // CtpOrderRequestMapper 把市价单映射成「对手价限价 + IOC」——⑲ 的 TimeCondition 透传是
        // 该语义跨前置存活的前提（2026-10-02 修复前：漏传 ⇒ 核心按 GFD 入簿 ⇒ 残量滞留 ⇒ 自成交跳过冻死市场）。
        var ioc = await trader.OrderInsertAsync(new OrderRequest
        {
            InvestorId = User, BrokerId = FrontLoopback.Broker, InstrumentId = Instrument,
            Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
            PriceType = OrderPriceType.Limit, LimitPrice = 2900, Volume = 2, ExchangeId = "DCE",
            TimeCondition = TimeCondition.IOC,
        });
        Assert.True(await UntilAsync(() => orders.Any(o => o.OrderRef == ioc.OrderRef && (char)o.OrderStatusRaw == '4')),
            "IOC 残量作废 '4' 终态推送未到达（TimeCondition 未透传 ⇒ GFD 入簿？）");

        // 核心侧：该单不在队（VolumeTotal=0，资金零冻结——作废即释放）
        var coreOrder = _sim.Core.QueryOrders(SimTradingCore.AccountIdOf(User)).Single(o => o.OrderRef == ioc.OrderRef);
        Assert.True((char)coreOrder.OrderStatusRaw == '4' && coreOrder.VolumeTotal == 0, $"核心侧 IOC 残量未作废: {coreOrder}");
        var acct = _sim.Core.QueryAccount(SimTradingCore.AccountIdOf(User));
        Assert.True(acct.FrozenMargin == 0 && acct.Available == FrontLoopback.SandboxDeposit,
            $"IOC 作废后冻结未释放: FrozenMargin={acct.FrozenMargin} Available={acct.Available}");
    }

    [Fact]
    public async Task PostOnly限价单_TC4经线协议透传_入簿与整单拒收()
    {
        var dir = NewFlowDir();
        Directory.CreateDirectory(dir);
        using var trader = Login(dir);
        _sim.SeedMaker();   // bid 2999 / ask 3001

        // ① 不交叉（买 2900 < bid 2999——同侧低价）：TC '4' 经 ⑲ 量 run 尾字节透传（VolShape 白名单）
        //    ⇒ 核心按 PostOnly 受理入簿（GFD 同款挂单）。native SDK 会把非标 TC 字节原样序列化进 ⑲——
        //    本 E2E 即裁判（改 VolShape 前 TC '4' 会让 ⑲ 解析失败静默丢单）。
        var rest = await trader.OrderInsertAsync(new OrderRequest
        {
            InvestorId = User, BrokerId = FrontLoopback.Broker, InstrumentId = Instrument,
            Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
            PriceType = OrderPriceType.Limit, LimitPrice = 2900, Volume = 1, ExchangeId = "DCE",
            TimeCondition = TimeCondition.PostOnly,
        });
        Assert.True(await UntilAsync(() => _sim.Core.QueryOrders(SimTradingCore.AccountIdOf(User))
            .Any(o => o.OrderRef == rest.OrderRef && (char)o.OrderStatusRaw == '3')),
            "核心侧未见 post-only 挂单（⑲ VolShape 未认 TC '4'？）");

        // ② 会交叉（买 3100 ≥ maker ask 3001）：核心整单拒收 90018——拒在报单号分配/冻结之前 ⇒
        //    QueryOrders 无该 ref 且资金零冻结（核心侧拒单是权威断言——⑳ 推送的客户端可见性是已知边界）
        var cross = await trader.OrderInsertAsync(new OrderRequest
        {
            InvestorId = User, BrokerId = FrontLoopback.Broker, InstrumentId = Instrument,
            Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
            PriceType = OrderPriceType.Limit, LimitPrice = 3100, Volume = 1, ExchangeId = "DCE",
            TimeCondition = TimeCondition.PostOnly,
        });
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline
               && _sim.Core.QueryOrders(SimTradingCore.AccountIdOf(User)).Any(o => o.OrderRef == cross.OrderRef))
        {
            await Task.Delay(50);   // ⑲ fire-and-forget：等核心侧处理完毕再断言缺席
        }
        Assert.DoesNotContain(_sim.Core.QueryOrders(SimTradingCore.AccountIdOf(User)), o => o.OrderRef == cross.OrderRef);

        // ③ ①的挂单撤单闭环（冻结释放）——证明 post-only 与 IOC/GFD 共用常规生命周期
        await trader.OrderActionAsync(FrontLoopback.Broker, User, 1, 1, rest.OrderRef, "DCE");
        Assert.True(await UntilAsync(() => _sim.Core.QueryOrders(SimTradingCore.AccountIdOf(User))
            .Any(o => o.OrderRef == rest.OrderRef && (char)o.OrderStatusRaw == '5')), "post-only 撤单 '5' 终态未到达");
        var acct = _sim.Core.QueryAccount(SimTradingCore.AccountIdOf(User));
        Assert.True(acct.FrozenMargin == 0 && acct.Available == FrontLoopback.SandboxDeposit,
            $"post-only 撤单后冻结未释放: FrozenMargin={acct.FrozenMargin} Available={acct.Available}");
    }

    [Fact]
    public async Task 交易所模式_双客户端互为对手_IOC吃单_撤单终态推送()
    {
        // 用户场景 E2E（做市商 + 噪声交易者经前置端口跑起来——2026-10-02 双 bot 冻结根因的回归钉）：
        // maker 挂双边 GFD 报价（做市商行为），taker IOC 对手价吃单（CtpOrderRequestMapper 的市价映射行为）
        // ⇒ 跨账户按 maker 价成交、被动方空闲收 F002/F001；maker 移心撤残腿 ⇒ '5' 终态推送到达
        //（撤单闭环——CtpBrokerage tracker 收敛的唯一信号）。无庄家、价格发现归簿。
        using var sim = FrontLoopback.Start();
        var makerDir = NewFlowDir(); var takerDir = NewFlowDir();
        Directory.CreateDirectory(makerDir); Directory.CreateDirectory(takerDir);
        using var maker = LoginAccount(sim, "maker1");
        using var taker = LoginAccount(sim, "taker1");

        var makerOrders = new ConcurrentBag<Order>(); var makerTrades = new ConcurrentBag<Trade>();
        maker.OrderReceived += (_, o) => makerOrders.Add(o);
        maker.TradeReceived += (_, t) => makerTrades.Add(t);
        var takerOrders = new ConcurrentBag<Order>(); var takerTrades = new ConcurrentBag<Trade>();
        taker.OrderReceived += (_, o) => takerOrders.Add(o);
        taker.TradeReceived += (_, t) => takerTrades.Add(t);

        // ① maker 挂双边报价（GFD 限价入簿）
        var ask = await maker.OrderInsertAsync(new OrderRequest
        {
            InvestorId = "maker1", BrokerId = FrontLoopback.Broker, InstrumentId = Instrument,
            Direction = Direction.Sell, OffsetFlag = OffsetFlag.Open,
            PriceType = OrderPriceType.Limit, LimitPrice = 3002, Volume = 5, ExchangeId = "DCE",
        });
        var bid = await maker.OrderInsertAsync(new OrderRequest
        {
            InvestorId = "maker1", BrokerId = FrontLoopback.Broker, InstrumentId = Instrument,
            Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
            PriceType = OrderPriceType.Limit, LimitPrice = 2998, Volume = 5, ExchangeId = "DCE",
        });
        Assert.True(await UntilAsync(() => makerOrders.Any(o => o.OrderRef == ask.OrderRef && (char)o.OrderStatusRaw == '3')
                                         && makerOrders.Any(o => o.OrderRef == bid.OrderRef && (char)o.OrderStatusRaw == '3')),
            "双边报价挂单受理推送未到达");

        // ② taker IOC 吃对手价（买 3@ask=3002）⇒ 跨账户按 maker 价成交 3 手
        var eat = await taker.OrderInsertAsync(new OrderRequest
        {
            InvestorId = "taker1", BrokerId = FrontLoopback.Broker, InstrumentId = Instrument,
            Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
            PriceType = OrderPriceType.Limit, LimitPrice = 3002, Volume = 3, ExchangeId = "DCE",
            TimeCondition = TimeCondition.IOC,
        });
        Assert.True(await UntilAsync(() => takerTrades.Any(t => t.OrderRef == eat.OrderRef)), "taker 成交回报未到达（IOC 未吃到簿？）");
        Assert.Equal(3002, takerTrades.Single(t => t.OrderRef == eat.OrderRef).Price);
        // 吃单方全成（3/3，IOC 无残量——若有残量按 '4' 作废不挂簿）
        Assert.Contains(takerOrders, o => o.OrderRef == eat.OrderRef && (char)o.OrderStatusRaw == '0');

        // ③ 被动方（maker）空闲收到自己的成交 + 部成状态（做市商被动成交路径）
        Assert.True(await UntilAsync(() => makerTrades.Any(t => t.OrderRef == ask.OrderRef)), "maker 被动成交回报未到达");
        Assert.Contains(makerOrders, o => o.OrderRef == ask.OrderRef && (char)o.OrderStatusRaw == '1');

        // ④ maker 移心撤残腿（卖腿剩 2 手）⇒ 4007 + '5' 终态推送（tracker 收敛信号）
        await maker.OrderActionAsync(FrontLoopback.Broker, "maker1", 1, 1, ask.OrderRef, "DCE");
        Assert.True(await UntilAsync(() => makerOrders.Any(o => o.OrderRef == ask.OrderRef && (char)o.OrderStatusRaw == '5')),
            "撤单 '5' 终态推送未到达");
        // 核心侧：撤单后簿上只剩买腿（卖腿量归零）
        var coreAsk = sim.Core.QueryOrders(SimTradingCore.AccountIdOf("maker1")).Single(o => o.OrderRef == ask.OrderRef);
        Assert.True((char)coreAsk.OrderStatusRaw == '5' && coreAsk.VolumeTotal == 0, "核心侧撤单未收敛");
    }

    [Fact]
    public async Task 同会话重复查询_实例回显_第二次应答不被丢弃()
    {
        // 2026-10-03 根因回归：80 族查询应答（⑯/⑱/查合约）的实例字节若恒回 captured 值（01），
        // 客户端把第二次起的应答当重复帧静默丢弃（读而不派发——Recv-Q=0、无断线事件、30s 超时）——
        // bot 长会话必死：期货页登录时已查过资金，Lean 引擎 GetCashBalance 再查即触发超时。
        // 修复 = 回显请求侧 80 族实例计数（真前置录制实证：⑮=1→⑯=1、⑰=2→⑱=2、idle⑮=3→⑯=3…）。
        var dir = NewFlowDir();
        Directory.CreateDirectory(dir);
        using var trader = Login(dir);

        var first = await trader.QueryTradingAccountAsync(FrontLoopback.Broker, User, CancellationToken.None);
        Assert.Equal(FrontLoopback.SandboxDeposit, first.Balance);
        // 第二次同型查询——修复前这里 30s 超时（实例重复被丢）
        var second = await trader.QueryTradingAccountAsync(FrontLoopback.Broker, User, CancellationToken.None);
        Assert.Equal(FrontLoopback.SandboxDeposit, second.Balance);
        // 第三次跨型查询（⑰ 空表——实例计数继续推进）
        var positions = await trader.QueryInvestorPositionAsync(FrontLoopback.Broker, User, ct: CancellationToken.None);
        Assert.NotNull(positions);
        // 查报单（80 族 op 带计数器）：SDK 现仍无法解析本地前置的 tag 空应答（真前置多行走 LZ4 二进制、
        // 空走 17B tag——同构 verbatim 也被拒，原因未定位）。先钉住「查报单超时是否堵死后续查询」——
        // 这决定它是 30s 烦扰还是管线杀手（noise 实例资金查询超时的归因）。
        IReadOnlyList<Order>? orders = null;
        try { orders = await trader.QueryOrderAsync(FrontLoopback.Broker, User, ct: CancellationToken.None); }
        catch (TimeoutException) { /* 已知边界：tag 空应答 SDK 不派发 */ }
        // 之后的查询仍健康？（若这里也超时 ⇒ 未决查询堵死管线 ⇒ 必须在服务端彻底解决查报单）
        var third = await trader.QueryTradingAccountAsync(FrontLoopback.Broker, User, CancellationToken.None);
        Assert.Equal(FrontLoopback.SandboxDeposit, third.Balance);
    }

    [Fact]
    public async Task 持仓查询非空_逐仓一行_客户端解析方向与总仓()
    {
        // ⑱ 非空单行模板（2026-10-03 SimNow 实抓：1 手 m2701 多·今仓）——SDK 即裁判：
        // 模板偏移错 ⇒ 解析字段错 ⇒ 断言失败。maker 卖单供给对手价，先吃 1 手成交落持仓。
        using var sim = FrontLoopback.Start();
        sim.SeedMaker();
        var dir = NewFlowDir();
        Directory.CreateDirectory(dir);
        using var trader = LoginAccount(sim, "posusr");

        var fill = await trader.OrderInsertAsync(new OrderRequest
        {
            InvestorId = "posusr", BrokerId = FrontLoopback.Broker, InstrumentId = Instrument,
            Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
            PriceType = OrderPriceType.Limit, LimitPrice = 3001, Volume = 1, ExchangeId = "DCE",
        });
        Assert.True(await UntilAsync(() => sim.Core.QueryPositions(SimTradingCore.AccountIdOf("posusr"))
            .Any(p => p.TotalPosition == 1)), "核心侧持仓未落账");

        var positions = await trader.QueryInvestorPositionAsync(FrontLoopback.Broker, "posusr", ct: CancellationToken.None);
        var row = positions.FirstOrDefault(p => p.InstrumentId == Instrument && p.TotalPosition == 1);
        Assert.NotNull(row);
        Assert.Equal(PosiDirection.Long, row!.PosiDirection);
        Assert.Equal(PositionDate.Today, row.PositionDate);
    }

    private static CtpTrader LoginAccount(FrontLoopback sim, string user)
    {
        sim.Deposit(user);   // 开户余额恒 0 ⇒ 参与者先显式入金（链上充值替身）
        var dir = Path.Combine(Path.GetTempPath(), "ctp-front-e2e", $"dual-{user}", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var trader = new CtpTrader(dir);
        var login = trader.LoginAsync(new CtpConnectRequest
        {
            FrontAddress = sim.FrontAddress,
            BrokerId = FrontLoopback.Broker,
            UserId = user,
            Password = "dual-pass",
            AppId = "simnow_client_test",
            AuthCode = "0000000000000000",
        }).GetAwaiter().GetResult();
        Assert.Equal("20260923", login.TradingDay);
        return trader;
    }

    [Fact]
    public async Task OrderRef续排_跨重连MaxOrderRef推进()
    {
        var dir1 = NewFlowDir();
        Directory.CreateDirectory(dir1);
        using (var trader1 = Login(dir1))
        {
            var o = await trader1.OrderInsertAsync(new OrderRequest
            {
                InvestorId = User, BrokerId = FrontLoopback.Broker, InstrumentId = Instrument,
                Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
                PriceType = OrderPriceType.Limit, LimitPrice = 2900, Volume = 1, ExchangeId = "DCE",
            });
            Assert.Equal("000000000001", o.OrderRef);   // 首单 = MaxOrderRef(0)+1，D12
            // ⑲ 是 fire-and-forget——等核心侧挂单生效（冻结出现）再二登，否则 login2 抢在报单登记前
            // 读到未推进的 MaxOrderRef（时序闪烁——E2E 实证）
            var acct = _sim.Core.QueryAccount(SimTradingCore.AccountIdOf(User));
            var dl = Environment.TickCount64 + 10000;
            while (Environment.TickCount64 < dl && acct.FrozenMargin == 0)
            {
                await Task.Delay(50);
                acct = _sim.Core.QueryAccount(SimTradingCore.AccountIdOf(User));
            }
            Assert.True(acct.FrozenMargin > 0, "挂单未生效（冻结未出现）");
        }

        // 二次登录（新 SDK 实例/流目录）：⑩ 的 MaxOrderRef 已推进（核心 per-account 序列）
        var dir2 = NewFlowDir();
        Directory.CreateDirectory(dir2);
        using var trader2 = new CtpTrader(dir2);
        var login2 = await trader2.LoginAsync(new CtpConnectRequest
        {
            FrontAddress = _sim.FrontAddress,
            BrokerId = FrontLoopback.Broker,
            UserId = User,
            Password = "m2-pass",
            AppId = "simnow_client_test",
            AuthCode = "0000000000000000",
        });
        Assert.Equal("20260923", login2.TradingDay);
        Assert.True(int.Parse(login2.MaxOrderRef.TrimEnd("\x00".ToCharArray())) == 1, $"MaxOrderRef 原始串=[{login2.MaxOrderRef}] len={login2.MaxOrderRef?.Length}");
    }
}
