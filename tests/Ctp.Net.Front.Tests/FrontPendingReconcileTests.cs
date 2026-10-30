using Ctp.Net;
using Ctp.Net.Core.Trader;
// 交易所域类型与 SDK 客户端类型同名（OrderRequest/Direction/…）——种子走交易所域，显式限定
using SimTradingCore = Aether.Exchange.SimTradingCore;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// 断连遗留动作的对账（2026-10-29，#1/#2）：撤单在途断连 ⇒ 记账 ⇒ 下次登录重放；
/// 报单结果不确定 ⇒ 记账 ⇒ 下次登录查实后清掉孤儿单。
/// <para>
/// 这里不模拟「RPC 在往返中途断掉」的时序（那需要可控的网络切断注入），而是**直接向账本
/// 补一手**——等价于旧会话在传输失败那一刻记下的条目，然后验证「新会话登录即对账」这条链路。
/// 记账点本身（<c>IsUncertain</c> 分支）在 <see cref="CtpFrontSession"/> 里，由构造保证不会被
/// 业务拒绝误记（90001-90011/90016-90019 都不进账本）。
/// </para>
/// </summary>
public sealed class FrontPendingReconcileTests : IDisposable
{
    private readonly FrontLoopback _sim = FrontLoopback.Start();
    private const string Instrument = FrontLoopback.Instrument;

    public void Dispose() => _sim.Dispose();

    private static string NewFlowDir([System.Runtime.CompilerServices.CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), "ctp-front-e2e", test, Guid.NewGuid().ToString("N"));

    /// <summary>登录（前置域密码随意——首登即设密）。</summary>
    private static CtpTrader Login(FrontLoopback sim, string user)
    {
        var dir = NewFlowDir();
        Directory.CreateDirectory(dir);
        var trader = new CtpTrader(dir);
        trader.LoginAsync(new CtpConnectRequest
        {
            FrontAddress = sim.FrontAddress,
            BrokerId = FrontLoopback.Broker,
            UserId = user,
            Password = "reconcile-pass",
            AppId = "simnow_client_test",
            AuthCode = "0000000000000000",
        }).GetAwaiter().GetResult();
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

    /// <summary>在交易所账本里直接挂一张远离市场的买单（本金来自 <see cref="FrontLoopback.Deposit"/>）。</summary>
    private string SeedRestingOrder(string account)
    {
        var req = new Aether.Exchange.OrderRequest
        {
            BrokerId = FrontLoopback.Broker, InvestorId = account, InstrumentId = Instrument,
            Direction = Aether.Exchange.Direction.Buy, OffsetFlag = Aether.Exchange.OffsetFlag.Open,
            PriceType = Aether.Exchange.OrderPriceType.Limit, LimitPrice = 2000, Volume = 1,
            TimeCondition = Aether.Exchange.TimeCondition.GFD,
        };
        _sim.Core.InsertOrder(account, req);
        // 交易所自己分配 OrderRef（OrderInsertResult.ClientOrderRef 是客户端引用）——回查账本取活单号
        return _sim.Core.QueryOrders(SimTradingCore.AccountIdOf(account))
            .Last(o => (char)o.OrderStatusRaw is '3').OrderRef;
    }

    [Fact]
    public async Task 断连期间丢失的撤单_下一次登录时自动重放()
    {
        const string user = "rcuser";
        _sim.Deposit(user);

        using (var first = Login(_sim, user))
        {
            // 用前置会话下一张不成交的限价单，确认链路正常（前置正常路径仍然工作）
            var orderRef = SeedRestingOrder(user);
            Assert.Contains(_sim.Core.QueryOrders(SimTradingCore.AccountIdOf(user)),
                o => o.OrderRef == orderRef && (char)o.OrderStatusRaw is '3');
            first.Dispose();
        }

        // 断连期间丢掉的撤单：等价于旧会话在 RPC 传输失败时的记账
        var pending = _sim.Core.QueryOrders(SimTradingCore.AccountIdOf(user))
            .Single(o => (char)o.OrderStatusRaw is '3').OrderRef;
        _sim.Server.PendingLedger.RecordCancel(user, pending);
        Assert.Equal(1, _sim.Server.PendingLedger.CountFor(user).Cancels);

        // 下一次登录 = 对账时点
        using var second = Login(_sim, user);
        Assert.True(await UntilAsync(() => _sim.Core.QueryOrders(SimTradingCore.AccountIdOf(user))
            .Single(o => o.OrderRef == pending).OrderStatusRaw is (byte)'5'),
            "登录后应把断连期间丢失的撤单重放掉（订单状态应变成 '5' 已撤销）");
        Assert.Equal((0, 0), _sim.Server.PendingLedger.CountFor(user));   // 账本已清空
    }

    [Fact]
    public async Task 结果不确定的报单_下一次登录时清掉孤儿单()
    {
        const string user = "orphuser";
        _sim.Deposit(user);

        // 交易所侧「其实已经落地」的那张单，对应一条「报单结果不确定」的账本记录
        var orphanRef = SeedRestingOrder(user);
        _sim.Server.PendingLedger.RecordInsert(user, orphanRef, Instrument);

        using var trader = Login(_sim, user);

        Assert.True(await UntilAsync(() => _sim.Core.QueryOrders(SimTradingCore.AccountIdOf(user))
            .Single(o => o.OrderRef == orphanRef).OrderStatusRaw is (byte)'5'),
            "登录后应把「客户端以为失败、交易所里还活着」的孤儿单撤掉");
        Assert.Equal((0, 0), _sim.Server.PendingLedger.CountFor(user));
    }

    [Fact]
    public async Task 结果不确定的报单_交易所里不存在时不做任何事()
    {
        const string user = "ghostuser";
        _sim.Deposit(user);

        // 记账但交易所里没有这张单（当时压根没落地）
        _sim.Server.PendingLedger.RecordInsert(user, "999999999999", Instrument);

        using var trader = Login(_sim, user);

        Assert.True(await UntilAsync(() => _sim.Server.PendingLedger.CountFor(user) == (0, 0)),
            "对账后账本应清空");
        Assert.DoesNotContain(_sim.Core.QueryOrders(SimTradingCore.AccountIdOf(user)), o => o.OrderRef == "999999999999");
    }

    [Fact]
    public async Task 同账户重复登录_踢掉旧会话()
    {
        const string user = "kickuser";
        _sim.Deposit(user);

        var first = Login(_sim, user);
        var disconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.FrontDisconnected += (_, _) => disconnected.TrySetResult(true);

        // 同账户第二条会话登录 ⇒ 前置应主动关闭旧会话（真柜台语义，防 OrderRef/SessionID 互踩）
        using var second = Login(_sim, user);

        var kicked = await Task.WhenAny(disconnected.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(disconnected.Task, kicked);
        first.Dispose();
    }
}
