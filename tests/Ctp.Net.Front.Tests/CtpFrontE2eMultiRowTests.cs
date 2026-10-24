using Ctp.Net.Core.Trader;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// 多行查询 E2E（SDK 即裁判）：<b>标准 CTP 客户端对查询类请求是「逐条回调 + bIsLast 收尾」</b>
/// （官方 <c>ThostFtdcTraderApi.h</c>：<c>OnRspQryInvestorPosition(..., bool bIsLast)</c>、
/// <c>OnRspQryOrder(..., bool bIsLast)</c>）——多行不是扩展，是协议原生语义。
/// <para>
/// 本测试钉住前置侧的多行出帧：账户在两个合约上各有持仓 ⇒ 持仓查询必须回两行、且两行都到达客户端。
/// （2026-10-24：真前置凭据到手后先补的正是这块的基础验证——多行收集此前只有单行样本。）
/// </para>
/// </summary>
public sealed class CtpFrontE2eMultiRowTests : IDisposable
{
    private readonly FrontLoopback _sim = FrontLoopback.Start();
    private const string User = "mrowusr";   // 6 字符（等宽纪律）
    private const string Other = "ax001";    // 第二个上市合约（板子默认 ax001 + m2701）

    public void Dispose() => _sim.Dispose();

    [Fact]
    public async Task 持仓查询_两个合约各一仓_两行全部到达()
    {
        // ① 两个合约都要有 maker 流动性（价格发现归簿：没有挂单就没有成交，没有成交就没有持仓）
        _sim.SeedMaker();                                   // m2701：买 20@2999 / 卖 20@3001
        SeedMaker(Other, 2998, 3000);

        // ② 同一账户在两个合约上各吃 1 手 ⇒ 两条持仓
        TakerBuy(Other, 3000);
        TakerBuy(FrontLoopback.Instrument, 3001);

        // ③ 标准客户端查持仓——期望两行
        var flowDir = Path.Combine(Path.GetTempPath(), "ctp-front-e2e", "multirow", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(flowDir);
        using var trader = new CtpTrader(flowDir);
        await trader.LoginAsync(new CtpConnectRequest
        {
            FrontAddress = _sim.FrontAddress,
            BrokerId = FrontLoopback.Broker,
            UserId = User,
            Password = "mrow-pass",
            AppId = "simnow_client_test",
            AuthCode = "0000000000000000",
        });

        var positions = await trader.QueryInvestorPositionAsync(FrontLoopback.Broker, User);

        var instruments = positions.Select(p => p.InstrumentId).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { Other, FrontLoopback.Instrument }, instruments);
    }

    private void SeedMaker(string instrumentId, double bid, double ask)
    {
        foreach (var (dir, price) in new[] { (Aether.Exchange.Direction.Buy, bid), (Aether.Exchange.Direction.Sell, ask) })
        {
            _sim.Core.InsertOrder("mm2", new Aether.Exchange.OrderRequest
            {
                BrokerId = FrontLoopback.Broker,
                InvestorId = "mm2",
                InstrumentId = instrumentId,
                Direction = dir,
                OffsetFlag = Aether.Exchange.OffsetFlag.Open,
                PriceType = Aether.Exchange.OrderPriceType.Limit,
                LimitPrice = price,
                Volume = 20,
                TimeCondition = Aether.Exchange.TimeCondition.GFD,
            });
        }
    }

    private void TakerBuy(string instrumentId, double price) =>
        _sim.Core.InsertOrder(User, new Aether.Exchange.OrderRequest
        {
            BrokerId = FrontLoopback.Broker,
            InvestorId = User,
            InstrumentId = instrumentId,
            Direction = Aether.Exchange.Direction.Buy,
            OffsetFlag = Aether.Exchange.OffsetFlag.Open,
            PriceType = Aether.Exchange.OrderPriceType.Limit,
            LimitPrice = price,
            Volume = 1,
            TimeCondition = Aether.Exchange.TimeCondition.GFD,
        });
}
