using System.Collections.Concurrent;
using Ctp.Net.Core.MarketData;
using Ctp.Net.Core.Trader;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// M3 E2E：行情腿全链路（SDK 即裁判）——官方 CtpMarketData 客户端经真实 TCP 连行情腿前置，
/// 走 握手→挑战→MD 登录→订阅→tick 推送。任何帧字段错位 ⇒ 回调不触发 ⇒ 超时失败。
/// </summary>
public sealed class CtpFrontE2eM3Tests : IDisposable
{
    private readonly FrontLoopback _sim = FrontLoopback.Start();
    private const string User = "m3user";
    private const string Instrument = "m2701";   // 5 字符 = tick 模板捕获槽宽（等宽纪律）

    public void Dispose() => _sim.Dispose();

    [Fact]
    public async Task 行情腿_Md登录_订阅_Tick推送全链路()
    {
        // maker 双边挂单（价格发现归簿——tick 事件驱动：订阅快照 + 成交推送，无注价入口）
        _sim.SeedMaker();

        var flowDir = Path.Combine(Path.GetTempPath(), "ctp-front-e2e", "md", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(flowDir);
        using var md = new CtpMarketData(flowDir);

        var ticks = new ConcurrentBag<TickData>();
        md.TickReceived += (_, t) => ticks.Add(t);

        // ① MD 登录（凭据不校验——行情订阅无账户语义）
        await md.LoginAsync(_sim.MdAddress, FrontLoopback.Broker, User, "m3-pass");
        // LoginAsync 无异常返回 = 登录应答帧被 SDK 接受（MdLoginRsp 模板通过裁判）

        // ② 订阅 m2701 + 订阅应答（SDK 即裁判：订阅应答帧被官方 SDK 接受并逐合约触发
        // OnRspSubMarketData——**只有**本帧请求的那一个合约，末条 isLast=true）
        var subAcks = new ConcurrentBag<(string Inst, int Err, string Msg, bool Last)>();
        md.SubMarketDataResult += (_, e) => subAcks.Add((e.InstrumentId, e.ErrorId, e.ErrorMsg, e.IsLast));
        await md.SubscribeMarketDataAsync(new[] { Instrument });
        var deadline = Environment.TickCount64 + 15000;
        while (Environment.TickCount64 < deadline && !subAcks.Any(a => a.Last))
            await Task.Delay(50);
        Assert.Equal(new[] { Instrument }, subAcks.Select(a => a.Inst).Distinct().ToArray());
        Assert.Contains(subAcks, a => a.Inst == Instrument && a.Err == 0 && a.Last);
        Assert.InRange(subAcks.Count, 1, 8);    // 无重投（无限重投时是几十万条）

        // ③ 成交驱动一帧 tick（E4F103 流式 op——SDK 即裁判：值/合约/时间逐字段断言）：
        // taker 市价买 1 吃 maker 卖侧 ⇒ last=3001，簿顶 bid=2999 / ask=3001（卖侧剩 19）
        _sim.Core.InsertOrder("m3taker", new Aether.Exchange.OrderRequest
        {
            BrokerId = FrontLoopback.Broker,
            InvestorId = "m3taker",
            InstrumentId = Instrument,
            Direction = Aether.Exchange.Direction.Buy,
            OffsetFlag = Aether.Exchange.OffsetFlag.Open,
            PriceType = Aether.Exchange.OrderPriceType.Market,
            LimitPrice = 0,
            Volume = 1,
        });

        // ④ 查合约（板子数据源——交易腿 op，前置 tag 应答，SDK 解析返回上市表）
        var traderFlow = Path.Combine(Path.GetTempPath(), "ctp-front-e2e", "qry", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(traderFlow);
        using var trader = new CtpTrader(traderFlow);
        await trader.LoginAsync(new CtpConnectRequest
        {
            FrontAddress = _sim.FrontAddress,
            BrokerId = FrontLoopback.Broker,
            UserId = User,
            Password = "m3-pass",
            AppId = "simnow_client_test",
            AuthCode = "0000000000000000",
        });
        var instruments = await trader.QueryInstrumentsAsync();
        // ax001（2026-10-08 起默认上市合约——原 zx001）；wire ExchangeId 槽宽 3（等宽纪律）
        // ⇒ "AETHER" 截断为 "AET"（VolumeMultiple/PriceTick/PreSettlement 是模板捕获 verbatim，板子只读 id/交易所）
        Assert.Contains(instruments, i => i.InstrumentId == "ax001" && i.ExchangeId == "AET");
        // 多行钉子（2026-10-05 wrapper 静默期收尾修复）：双品种上市表**全部行**到达——tag 帧恒 isLast=true，
        // 多行收集靠 CtpTrader.OnQueryItem 静默期收尾（旧版首个 isLast 即完成、只有首行）。
        Assert.Contains(instruments, i => i.InstrumentId == "m2701" && i.ExchangeId == "DCE");
        Assert.Equal(2, instruments.Count);

        var tickDeadline = Environment.TickCount64 + 15000;
        while (Environment.TickCount64 < tickDeadline && !ticks.Any(t => Math.Abs(t.LastPrice - 3001) < 1e-9))
            await Task.Delay(50);
        Assert.Contains(ticks, t => Math.Abs(t.LastPrice - 3001) < 1e-9);
        var tick = ticks.First(t => Math.Abs(t.LastPrice - 3001) < 1e-9);
        Assert.Equal(Instrument, tick.InstrumentId);
        Assert.Equal(3001, tick.LastPrice);
        Assert.Equal(2999, tick.BidPrice);
        Assert.Equal(3001, tick.AskPrice);
        // 成交量真实化（2026-10-24）：模板量槽原为抓包常量 802254（客户端常年读到冻结量）；
        // 现在 ← 交易所 tick 的累计成交量——本场 1 手成交 ⇒ 1。
        Assert.Equal(1, tick.Volume);
        // 买卖一档量同理（模板常量 1105/808 即两槽抓包值，offset 86-87 / 92-93）。
        // 卖侧 20 手被吃 1 手 ⇒ 剩 19。
        Assert.Equal(19, tick.AskVolume);
        // 持仓量（offset 72，5B 槽；模板常量 2457399 与解码值逐字节吻合）——本场 1 手成交 ⇒ 1。
        Assert.Equal(1, tick.OpenInterest);
    }

    /// <summary>
    /// 订阅应答只回显本次请求的合约（2026-10-29 回归）。
    /// 旧版把 183B 抓包整段回放 ⇒ 无论订阅什么，客户端都收到 m2701/i2701/ta2701/IF2612 四条
    /// 「订阅成功」，而自己订阅的品种（如 ax001）一条应答都没有——自定义品种被灌了别人的订阅回执。
    /// 记录数不能裁剪（裁剪成 N 条会让 native 无限重投，见 BuildMdSubscribeRsp 注释），
    /// 所以这里断言的是「应答里的合约名**只有**本次请求的、且不重投」。
    /// </summary>
    [Fact]
    public async Task 订阅应答只回显请求的合约()
    {
        var flowDir = Path.Combine(Path.GetTempPath(), "ctp-front-e2e", "mdsub", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(flowDir);
        using var md = new CtpMarketData(flowDir);

        var acks = new ConcurrentBag<(string Inst, int Err, string Msg, bool Last)>();
        md.SubMarketDataResult += (_, e) => acks.Add((e.InstrumentId, e.ErrorId, e.ErrorMsg, e.IsLast));

        await md.LoginAsync(_sim.MdAddress, FrontLoopback.Broker, User, "m3-pass");
        await md.SubscribeMarketDataAsync(new[] { "m2701", "ax001" });

        var deadline = Environment.TickCount64 + 15000;
        while (Environment.TickCount64 < deadline && !acks.Any(a => a.Last))
            await Task.Delay(50);

        // 合约名只能来自本次请求（旧版这里是 m2701/i2701/ta2701/IF2612 混着来）
        Assert.Equal(new[] { "ax001", "m2701" },
            acks.Select(a => a.Inst).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.All(acks, a => Assert.Equal(0, a.Err));
        Assert.Equal(1, acks.Count(a => a.Last));                // 末条且仅末条 isLast=true
        // 记录数不再钉死 4：把 6 字符槽写成 5 字符 ID 后原生解析器只走出 3 条（实测），
        // 关键是**没有重投**——无限重投时这里会是几十万条。
        Assert.InRange(acks.Count, 1, 8);
    }

    /// <summary>
    /// 量槽含标记字节时仍要原值到客户端（2026-10-25 事故回归）。
    /// 成交量 224 = <c>00 00 E0</c>、卖一档量 226 = <c>00 E2</c>——旧版裸插 ⇒ 客户端按标记切字段
    /// 错位 ⇒ native 侧无限重投同一笔 tick。改成 E0 转义后，官方 SDK 必须解回原值（帧长 +2B，
    /// 由帧头声明，SDK 按长度读）。用 ax001（价位 99）而非 m2701：224 手保证金在 1M 余额内。
    /// </summary>
    [Fact]
    public async Task 量槽含标记字节时客户端读回原值()
    {
        const string inst = "ax001";

        // 单边挂 450 手卖；随后吃 224 手 ⇒ 成交量 224、卖一档剩 226，两个量槽同时命中标记字母表
        _sim.Core.InsertOrder("mm", new Aether.Exchange.OrderRequest
        {
            BrokerId = FrontLoopback.Broker,
            InvestorId = "mm",
            InstrumentId = inst,
            Direction = Aether.Exchange.Direction.Sell,
            OffsetFlag = Aether.Exchange.OffsetFlag.Open,
            PriceType = Aether.Exchange.OrderPriceType.Limit,
            LimitPrice = 99,
            Volume = 450,
            TimeCondition = Aether.Exchange.TimeCondition.GFD,
        });

        var flowDir = Path.Combine(Path.GetTempPath(), "ctp-front-e2e", "mdesc", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(flowDir);
        using var md = new CtpMarketData(flowDir);

        var ticks = new ConcurrentBag<TickData>();
        md.TickReceived += (_, t) => ticks.Add(t);

        await md.LoginAsync(_sim.MdAddress, FrontLoopback.Broker, User, "m3-pass");
        await md.SubscribeMarketDataAsync(new[] { inst });
        await Task.Delay(300);   // 等订阅快照落地（快照 tick 的量为 0）

        _sim.Core.InsertOrder("m3taker", new Aether.Exchange.OrderRequest
        {
            BrokerId = FrontLoopback.Broker,
            InvestorId = "m3taker",
            InstrumentId = inst,
            Direction = Aether.Exchange.Direction.Buy,
            OffsetFlag = Aether.Exchange.OffsetFlag.Open,
            PriceType = Aether.Exchange.OrderPriceType.Market,
            LimitPrice = 0,
            Volume = 224,
        });

        var deadline = Environment.TickCount64 + 15000;
        while (Environment.TickCount64 < deadline && !ticks.Any(t => Math.Abs(t.Volume - 224) < 1e-9))
            await Task.Delay(50);

        var tick = ticks.FirstOrDefault(t => Math.Abs(t.Volume - 224) < 1e-9);
        Assert.NotNull(tick);
        Assert.Equal(99, tick!.LastPrice);
        Assert.Equal(226, tick.AskVolume);   // 450 − 224
    }
}
