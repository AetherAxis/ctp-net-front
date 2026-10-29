using Ctp.Net.Core.Trader;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// 查成交（<c>ReqQryTrade</c>）**非空**应答 E2E：前置把账户成交流水逐笔编码成真前置同款 tag 记录帧
/// （⑲ 284B 模板，2026-10-24 用真前置实成交样本逆向完成），标准客户端经官方 SDK 解出与成交回报一致的字段。
/// <para>
/// 模板来源：`000000000002` 那张 m2701 单在真前置吃到 1@3279（TradeID 2344）后抓的查成交应答。
/// 空表路径仍走 13B tag 空表（真前置同形），见 <see cref="CtpFrontE2eTradeQueryTests"/>。
/// </para>
/// </summary>
public sealed class CtpFrontE2eTradeRecordTests : IDisposable
{
    private const string User = "trd1us";   // 6 字符（等宽纪律）

    private readonly FrontLoopback _sim = FrontLoopback.Start();

    public void Dispose() => _sim.Dispose();

    private async Task<CtpTrader> LoginAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ctp-front-e2e", "qrytrade", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var trader = new CtpTrader(dir);
        _sim.Deposit(User);   // 开户余额恒 0 ⇒ 参与者先显式入金（链上充值替身）
        await trader.LoginAsync(new CtpConnectRequest
        {
            FrontAddress = _sim.FrontAddress,
            BrokerId = FrontLoopback.Broker,
            UserId = User,
            Password = "trd1-pass",
            AppId = "simnow_client_test",
            AuthCode = "0000000000000000",
        });
        return trader;
    }

    [Fact]
    public async Task 查成交_一笔成交_回一行且字段与成交回报一致()
    {
        _sim.SeedMaker();                                    // m2701：买 20@2999 / 卖 20@3001
        using var trader = await LoginAsync();

        var trades = new List<Trade>();
        trader.TradeReceived += (_, t) => trades.Add(t);

        var fill = await trader.OrderInsertAsync(new OrderRequest
        {
            InvestorId = User, BrokerId = FrontLoopback.Broker, InstrumentId = FrontLoopback.Instrument,
            Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
            PriceType = OrderPriceType.Limit, LimitPrice = 3100, Volume = 2, ExchangeId = "DCE",
        });
        Assert.True(await UntilAsync(() => trades.Any(t => t.OrderRef == fill.OrderRef)),
            "成交回报未到达——查成交无从谈起");

        var rows = await trader.QueryTradeAsync(FrontLoopback.Broker, User);

        var row = Assert.Single(rows);
        Assert.Equal(fill.OrderRef, row.OrderRef);
        Assert.Equal(FrontLoopback.Instrument, row.InstrumentId);
        Assert.Equal("DCE", row.ExchangeId);
        Assert.Equal(Direction.Buy, row.Direction);
        Assert.Equal(OffsetFlag.Open, row.OffsetFlag);
        Assert.Equal(3001, row.Price);                       // 吃 maker 卖一价
        Assert.Equal(2, row.Volume);
        Assert.Matches(@"^\d{2}:\d{2}:\d{2}$", row.TradeTime);
        Assert.Equal(FrontLoopback.TradingNow.ToString("yyyyMMdd"), row.TradeDate);
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
}
