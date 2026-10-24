using Ctp.Net.Core.Trader;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// 查报单（<c>ReqQryOrder</c>）非空应答 E2E：前置把账户账本里的委托逐行编码成真前置同款 tag 记录帧
/// （⑯ 模板，2026-10-24 逆向完成），标准客户端经官方 SDK 解出与下单时一致的字段。
/// <para>
/// 旧行为：查报单一律回空表 ⇒ 客户端看到「没有可撤活单」（对账/追单逻辑走错分支）。本测试钉住
/// 「有委托就有行」。已知边界：<c>VolumeTraded</c> 沿用模板值 0（部分成交的成交数靠 OnRtnTrade 推送收敛）。
/// </para>
/// </summary>
public sealed class CtpFrontE2eOrderQueryTests : IDisposable
{
    private const string User = "oqr1us";    // 6 字符（等宽纪律：响应帧用户槽 6B）
    private const string Other = "ax001";    // 第二个上市合约

    private readonly FrontLoopback _sim = FrontLoopback.Start();

    public void Dispose() => _sim.Dispose();

    private async Task<CtpTrader> LoginAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ctp-front-e2e", "qryorder", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var trader = new CtpTrader(dir);
        await trader.LoginAsync(new CtpConnectRequest
        {
            FrontAddress = _sim.FrontAddress,
            BrokerId = FrontLoopback.Broker,
            UserId = User,
            Password = "oqr1-pass",
            AppId = "simnow_client_test",
            AuthCode = "0000000000000000",
        });
        return trader;
    }

    private Task<OrderInsertResult> RestAsync(CtpTrader trader, string instrumentId, double price, int volume) =>
        trader.OrderInsertAsync(new OrderRequest
        {
            InvestorId = User, BrokerId = FrontLoopback.Broker, InstrumentId = instrumentId,
            Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
            PriceType = OrderPriceType.Limit, LimitPrice = price, Volume = volume, ExchangeId = "DCE",
        });

    [Fact]
    public async Task 查报单_一张挂单_回一行且字段与下单一致()
    {
        _sim.SeedMaker();                                     // m2701：买 20@2999 / 卖 20@3001
        using var trader = await LoginAsync();
        var rest = await RestAsync(trader, FrontLoopback.Instrument, 2900, 3);   // <maker Bid ⇒ 挂住

        var rows = await trader.QueryOrderAsync(FrontLoopback.Broker, User);

        var row = Assert.Single(rows);
        Assert.Equal(rest.OrderRef, row.OrderRef);
        Assert.Equal(FrontLoopback.Instrument, row.InstrumentId);
        Assert.Equal("DCE", row.ExchangeId);
        Assert.Equal(Direction.Buy, row.Direction);
        Assert.Equal(OffsetFlag.Open, row.OffsetFlag);
        Assert.Equal(OrderPriceType.Limit, row.PriceType);
        Assert.Equal(2900, row.LimitPrice);
        Assert.Equal(3, row.VolumeTotalOriginal);
        Assert.Equal('3', (char)row.OrderStatusRaw);
        Assert.Matches(@"^\d{2}:\d{2}:\d{2}$", row.InsertTime);
    }

    [Fact]
    public async Task 查报单_两个合约各一张_两行全部到达()
    {
        _sim.SeedMaker();
        _sim.Core.InsertOrder("mm3", new Aether.Exchange.OrderRequest
        {
            BrokerId = FrontLoopback.Broker, InvestorId = "mm3", InstrumentId = Other,
            Direction = Aether.Exchange.Direction.Buy, OffsetFlag = Aether.Exchange.OffsetFlag.Open,
            PriceType = Aether.Exchange.OrderPriceType.Limit, LimitPrice = 2998, Volume = 20,
        });
        _sim.Core.InsertOrder("mm3", new Aether.Exchange.OrderRequest
        {
            BrokerId = FrontLoopback.Broker, InvestorId = "mm3", InstrumentId = Other,
            Direction = Aether.Exchange.Direction.Sell, OffsetFlag = Aether.Exchange.OffsetFlag.Open,
            PriceType = Aether.Exchange.OrderPriceType.Limit, LimitPrice = 3000, Volume = 20,
        });

        using var trader = await LoginAsync();
        await RestAsync(trader, FrontLoopback.Instrument, 2900, 1);
        await RestAsync(trader, Other, 2990, 2);

        var rows = await trader.QueryOrderAsync(FrontLoopback.Broker, User);

        Assert.Equal(new[] { Other, FrontLoopback.Instrument },
            rows.Select(r => r.InstrumentId).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.All(rows, r => Assert.Equal('3', (char)r.OrderStatusRaw));
    }
}
