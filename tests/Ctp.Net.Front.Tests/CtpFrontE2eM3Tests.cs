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

        // ② 订阅 m2701 + 订阅应答（SDK 即裁判：订阅应答帧被官方 SDK 接受并触发 4 个
        // OnRspSubMarketData 回调——含 m2701 的成功确认）
        var subAcks = new ConcurrentBag<(string Inst, int Err, string Msg, bool Last)>();
        md.SubMarketDataResult += (_, e) => subAcks.Add((e.InstrumentId, e.ErrorId, e.ErrorMsg, e.IsLast));
        await md.SubscribeMarketDataAsync(new[] { Instrument });
        var deadline = Environment.TickCount64 + 15000;
        while (Environment.TickCount64 < deadline && subAcks.Count == 0)
            await Task.Delay(50);
        Assert.Contains(subAcks, a => a.Inst == Instrument && a.Err == 0);

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
    }
}
