using System.Collections.Concurrent;

using Ctp.Net;
using Ctp.Net.Core.Trader;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// 价格槽转义 E2E（SDK 即裁判）：**标记字节被 E0 转义后，官方 SDK 仍能正确读出价格与其后的字段**。
/// <para>
/// 这是 #33 修复的阳性对照。2026-10-31 13:40 本机走前置复现：MM 在 99.703125 挂腿，价格槽被写成
/// <c>40 58 E9</c>（E9 是标记字节、未转义）⇒ SDK 在价格处断句 ⇒ 客户端只看到
/// <c>BuyOpen 0@96 成0 状态=(空)</c> ⇒ 腿永远进不了终态 ⇒ 市场静默。
/// 修复走的是「值内有标记字节就 E0 转义（+1B）」，但**转义后的价格槽能否被官方 SDK 正常读回，
/// 只有让 SDK 自己读一遍才算数**——本文件就是那一遍，不需要碰线上前置。
/// </para>
/// </summary>
public sealed class PriceSlotEscapedPushE2eTests : IDisposable
{
    private readonly FrontLoopback _sim = FrontLoopback.Start();
    private const string User = "escusr";        // 6 字符（等宽纪律）
    private const string Instrument = "m2701";   // 5 字符 = 推送模板捕获槽宽

    public void Dispose() => _sim.Dispose();

    private static string NewFlowDir([System.Runtime.CompilerServices.CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), "ctp-front-e2e", test, Guid.NewGuid().ToString("N"));

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

    private CtpTrader Login(string flowDir)
    {
        _sim.Deposit(User);
        var trader = new CtpTrader(flowDir);
        var login = trader.LoginAsync(new CtpConnectRequest
        {
            FrontAddress = _sim.FrontAddress,
            BrokerId = FrontLoopback.Broker,
            UserId = User,
            Password = "esc-pass",
            AppId = "simnow_client_test",
            AuthCode = "0000000000000000",
        }).GetAwaiter().GetResult();
        Assert.Equal("20260923", login.TradingDay);
        return trader;
    }

    /// <summary>
    /// 2164.5 的三字节价格槽是 <c>40 A0 E9</c>——**旧实现（递减 4 次）在这里仍然脏**：E9→E5 还是标记字节，
    /// SDK 把价格截成 <c>40 A0</c> 右补零 = 假价 32.0，其后的量/状态/合约全错。所以这条用例在旧代码上必红、
    /// 在新代码（E0 转义成 <c>40 A0 E0 E9</c>）上必绿——它钉的正是 #33 的那个 bug，不是「转义能不能被接受」而已。
    /// 这份价格可被 3 字节槽精确表示（[2048,4096) 区间 ulp = 0.5），回显应当一字不差。
    /// </summary>
    [Fact]
    public async Task 价格槽含E0的挂单_报单回报与撤单终态都能被SDK正确解析()
    {
        const double leakyPrice = 2164.5;   // < maker bid 2999 ⇒ 静默挂单
        var dir = NewFlowDir();
        Directory.CreateDirectory(dir);
        using var trader = Login(dir);
        _sim.SeedMaker();

        var orders = new ConcurrentBag<Order>();
        trader.OrderReceived += (_, o) => orders.Add(o);

        var rest = await trader.OrderInsertAsync(new OrderRequest
        {
            InvestorId = User, BrokerId = FrontLoopback.Broker, InstrumentId = Instrument,
            Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
            PriceType = OrderPriceType.Limit, LimitPrice = leakyPrice, Volume = 1, ExchangeId = "DCE",
        });

        Assert.True(await UntilAsync(() => orders.Any(o => o.OrderRef == rest.OrderRef && (char)o.OrderStatusRaw == '3')),
            "受理推送未到达或状态槽被读成别的值（价格槽转义没被 SDK 接受？）");

        var accepted = orders.Single(o => o.OrderRef == rest.OrderRef && (char)o.OrderStatusRaw == '3');
        Assert.Equal(Instrument, accepted.InstrumentId);                       // 旧实现这里读到空串
        Assert.Equal(leakyPrice, accepted.LimitPrice);                // 旧实现这里读到 32.0
        Assert.Equal(1, accepted.VolumeTotalOriginal);
        Assert.Equal(Direction.Buy, accepted.Direction);

        // 撤单 → 终态 '5'：这是 tracker 收敛撤单的唯一信号（缺它 = #33 的撤单空转）
        await trader.OrderActionAsync(FrontLoopback.Broker, User, 1, 1, rest.OrderRef, "DCE");
        Assert.True(await UntilAsync(() => orders.Any(o => o.OrderRef == rest.OrderRef && (char)o.OrderStatusRaw == '5')),
            "撤单 '5' 终态推送未到达");

        var cancelled = orders.Single(o => o.OrderRef == rest.OrderRef && (char)o.OrderStatusRaw == '5');
        Assert.Equal(Instrument, cancelled.InstrumentId);
        Assert.Equal(leakyPrice, cancelled.LimitPrice);

        // 转义帧（比干净帧长 1B）之后，同一会话里的后续报单仍然正常——证明没有把记录遍历搞失步
        var after = await trader.OrderInsertAsync(new OrderRequest
        {
            InvestorId = User, BrokerId = FrontLoopback.Broker, InstrumentId = Instrument,
            Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
            PriceType = OrderPriceType.Limit, LimitPrice = 2900, Volume = 1, ExchangeId = "DCE",
        });
        Assert.True(await UntilAsync(() => orders.Any(o => o.OrderRef == after.OrderRef && (char)o.OrderStatusRaw == '3')),
            "转义帧之后的报单回报未到达（记录遍历已失步）");
        var afterReport = orders.Single(o => o.OrderRef == after.OrderRef && (char)o.OrderStatusRaw == '3');
        Assert.Equal(Instrument, afterReport.InstrumentId);
        Assert.Equal(2900d, afterReport.LimitPrice);
    }
}
