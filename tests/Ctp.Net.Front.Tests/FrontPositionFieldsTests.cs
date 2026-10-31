using Ctp.Net;
using Ctp.Net.Core.Trader;
using SimTradingCore = Aether.Exchange.SimTradingCore;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// 持仓记录帧的字段保真（2026-10-29）。
/// <para>
/// 背景：⑱ 模板大部分字段是 2026-10-03 抓包常量，前置只重写总仓槽。现场反馈「经前置看到的持仓是错的」，
/// 但一直没有可复现判据。这里用官方 SDK 回读**真实成交出来的持仓**，把「客户端到底看到什么」钉死。
/// </para>
/// <para>
/// 探针结论（同日，逐字节 patch + 每次重登，阳性对照齐全）：payload@54 = 总仓、payload@150 = 今仓，
/// 两者都是 1 字节槽（0/5/100/255 逐值验证）。此前「E7 槽 = 总仓」的结论只被 1 手样本验证过——
/// 与捕获常量同值，属假阳性；5 手样本才把它暴露出来。今仓槽此前**完全没被写过**，客户端恒读 1。
/// </para>
/// </summary>
public sealed class FrontPositionFieldsTests : IDisposable
{
    private readonly FrontLoopback _sim = FrontLoopback.Start();
    private const string User = "posfld";

    public void Dispose() => _sim.Dispose();

    private static string NewFlowDir([System.Runtime.CompilerServices.CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), "ctp-front-e2e", test, Guid.NewGuid().ToString("N"));

    private async Task<Position> QueryAsync(string user)
    {
        var dir = NewFlowDir();
        Directory.CreateDirectory(dir);
        using var trader = new CtpTrader(dir);
        await trader.LoginAsync(new CtpConnectRequest
        {
            FrontAddress = _sim.FrontAddress,
            BrokerId = FrontLoopback.Broker,
            UserId = user,
            Password = "posfld-pass",
            AppId = "simnow_client_test",
            AuthCode = "0000000000000000",
        });
        var rows = await trader.QueryInvestorPositionAsync(FrontLoopback.Broker, user);
        return rows.Single(p => p.InstrumentId == FrontLoopback.Instrument);
    }

    [Fact]
    public async Task 今仓五手_客户端读到的总仓与今仓都与交易所一致()
    {
        _sim.SeedMaker();      // m2701 双边 20 手（买 2999 / 卖 3001）
        _sim.Deposit(User);
        CoreBuy(User, 5, 3001);      // 吃卖一 5 手 ⇒ 多 5（今 5 / 昨 0）

        var venue = _sim.Core.QueryPositions(SimTradingCore.AccountIdOf(User))
            .Single(p => p.InstrumentId == FrontLoopback.Instrument);
        Assert.Equal(5, venue.TotalPosition);   // 前提自检：交易所侧确实是 5

        var front = await QueryAsync(User);
        Assert.Equal(venue.TotalPosition, front.TotalPosition);
        Assert.Equal(venue.TodayPosition, front.TodayPosition);
    }

    [Fact]
    public async Task 超过255手的总仓按真实值上报_今仓受协议上限封在255()
    {
        // 2026-10-30 定案（探针见 VolumeSlotEncodingTests 的类注释）：
        //   总仓槽可加宽 —— E7+1B ⇒ 0..255，E6+2B 大端 ⇒ 256..65535；
        //   今仓槽不可加宽 —— 16 个 tag × 5 种宽度全部解析失步，协议上限 255。
        // 旧实现两个槽都截到 255，理由是「加宽会失步」：对今仓成立，对总仓不成立。
        // 生产做市商真实总仓 +487 / -457，风控读的是总仓，所以这一半必须修（open-issues #6）。
        // 300 手保证金约 72 万，沙盒默认 100 万不够 ⇒ 直接补一笔（Deposit 对已入金账户是空操作）
        _sim.Core.CreditQuoteDeposit("mm", (ulong)(5_000_000 * SimTradingCore.QuoteAtomsPerToken));
        _sim.SeedMaker(300);
        _sim.Deposit(User);
        CoreBuy(User, 300, 3001);         // 吃满卖一 300 手 ⇒ 多 300（单字节槽放不下）

        var venue = _sim.Core.QueryPositions(SimTradingCore.AccountIdOf(User))
            .Single(p => p.InstrumentId == FrontLoopback.Instrument);
        Assert.Equal(300, venue.TotalPosition);

        var front = await QueryAsync(User);
        Assert.Equal(300, front.TotalPosition);        // 真实值，不再截断
        Assert.Equal(255, front.TodayPosition);        // 今仓槽协议上限——封顶但仍可读，不是丢行
    }

    [Fact]
    public async Task 持仓224手_值字节撞标记区也照样读回()
    {
        // 224 = 0xE0，正是 payload 的转义前缀。裸写 `E7 E0` 会让 SDK 丢掉整条记录（探针实测），
        // 必须写成 `E7 E0 E0`。旧实现既不转义也不截断到安全区 ⇒ 实测持仓落在 224..239 / 254 时
        // 客户端查到的是「空表」而不是「持仓 224」，比截断更难排查（open-issues #6 同源）。
        _sim.Core.CreditQuoteDeposit("mm", (ulong)(5_000_000 * SimTradingCore.QuoteAtomsPerToken));
        _sim.SeedMaker(224);
        _sim.Deposit(User);
        CoreBuy(User, 224, 3001);

        var venue = _sim.Core.QueryPositions(SimTradingCore.AccountIdOf(User))
            .Single(p => p.InstrumentId == FrontLoopback.Instrument);
        Assert.Equal(224, venue.TotalPosition);

        var front = await QueryAsync(User);
        Assert.Equal(224, front.TotalPosition);
        Assert.Equal(224, front.TodayPosition);
    }

    private void CoreBuy(string user, int volume, double price) =>
        _sim.Core.InsertOrder(user, new Aether.Exchange.OrderRequest
        {
            BrokerId = FrontLoopback.Broker,
            InvestorId = user,
            InstrumentId = FrontLoopback.Instrument,
            Direction = Aether.Exchange.Direction.Buy,
            OffsetFlag = Aether.Exchange.OffsetFlag.Open,
            PriceType = Aether.Exchange.OrderPriceType.Limit,
            LimitPrice = price,
            Volume = volume,
            TimeCondition = Aether.Exchange.TimeCondition.GFD,
        });
}
