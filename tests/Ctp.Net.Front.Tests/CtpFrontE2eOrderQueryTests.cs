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
        _sim.Deposit(User);   // 开户余额恒 0 ⇒ 参与者先显式入金（链上充值替身）
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
        _sim.SeedMaker();                                     // m2701：买 20@2999 / 卖 20@3001（内含 mm 入金）
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
        Assert.Equal(3, row.VolumeTotal);          // 未成交 ⇒ 剩余 = 原始量（槽位 @203，2026-10-24 探针定案）
        Assert.Equal('3', (char)row.OrderStatusRaw);
        Assert.Matches(@"^\d{2}:\d{2}:\d{2}$", row.InsertTime);
    }

    [Fact]
    public async Task 查报单_部分成交_剩余量是真剩余()
    {
        // ax001 卖盘只挂 1 手 ⇒ 买 3 手成交 1 手、余 2 手挂住
        _sim.Core.InsertOrder("mm3", new Aether.Exchange.OrderRequest
        {
            BrokerId = FrontLoopback.Broker, InvestorId = "mm3", InstrumentId = Other,
            Direction = Aether.Exchange.Direction.Sell, OffsetFlag = Aether.Exchange.OffsetFlag.Open,
            PriceType = Aether.Exchange.OrderPriceType.Limit, LimitPrice = 3000, Volume = 1,
        });

        using var trader = await LoginAsync();
        await trader.OrderInsertAsync(new OrderRequest
        {
            InvestorId = User, BrokerId = FrontLoopback.Broker, InstrumentId = Other,
            Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
            PriceType = OrderPriceType.Limit, LimitPrice = 3000, Volume = 3, ExchangeId = "DCE",
        });

        var rows = await trader.QueryOrderAsync(FrontLoopback.Broker, User);

        var row = Assert.Single(rows);
        Assert.Equal(3, row.VolumeTotalOriginal);
        Assert.Equal(2, row.VolumeTotal);          // 3 − 成交 1：剩余量槽是真值（旧实现恒回模板 1）
        Assert.Equal('1', (char)row.OrderStatusRaw);   // 部分成交在队
        // VolumeTraded 仍解出 0：整帧 patch-scan 已证本记录帧**不含该槽**（2026-10-24，带阳性对照），
        // 客户端靠 OnRtnTrade 推送收敛成交数——与真前置同款边界，不是本前置的编码缺陷。
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

    /// <summary>
    /// 多行规模回归（open-issues #21）：一次查询回 <b>30 行</b>，官方 SDK 必须全部收到、且**零断连**。
    /// <para>
    /// 为什么单独立一条：SDK 是按「每包流水 = 上一包 +1」校验的（`CFTDCSubEndPoint::HandlePackage`，
    /// 见 #21 反汇编），旧实现每行回显请求计数器 ⇒ 第 2 行起流水重复 ⇒ 客户端停派发并 RST。
    /// 也就是说**这条用例的裁判就是 SDK 本身**：只要序号方案退化成重复/跳号，这里会以断连或行数不足失败。
    /// 线上 200 行只在探针里验过（`probe-diag.ps1`），本用例把同一不变量钉进 CI；行数取 30 是为了
    /// 在「≥2 行」这个真正会炸的边界之上留足余量、又不拖慢测试。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 查报单_三十张挂单_三十行全部到达且零断连()
    {
        const int n = 30;
        _sim.SeedMaker();
        using var trader = await LoginAsync();
        for (var i = 0; i < n; i++)
            await RestAsync(trader, FrontLoopback.Instrument, 2900 + i, 1);   // 低价买 ⇒ 全部挂在簿上

        var rows = await trader.QueryOrderAsync(FrontLoopback.Broker, User);

        Assert.Equal(n, rows.Count);
        Assert.All(rows, r => Assert.Equal('3', (char)r.OrderStatusRaw));
        Assert.All(rows, r => Assert.Equal(FrontLoopback.Instrument, r.InstrumentId));
    }
}
