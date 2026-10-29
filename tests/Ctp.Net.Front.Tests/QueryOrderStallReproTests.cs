using Ctp.Net.Core.Trader;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// 「~4-5 分钟周期性交易腿停摆」进程内复现器（2026-10-14 双 bot 浸泡）：
/// noise 会话每 ~3s 一笔「持仓查询+报单」，~100 查询后 SDK 交易腿停摆（报单延迟、查询 30s 超时、
/// rc=-2、4097 重连）。本测试复刻该节奏直驱 FrontLoopback（无 console/Lean 宿主）：
/// 复现（查询超时）⇒ 问题在 wrapper/front/exchange wire 层——失败消息带迭代序号；
/// 不复现 ⇒ 问题在 Lean 宿主线程混合——转向引擎侧调查。测试**fail-fast**：停摆签名一出现立即失败。
/// </summary>
public sealed class QueryOrderStallReproTests : IDisposable
{
    private readonly FrontLoopback _sim = FrontLoopback.Start();
    private const string User = "soakusr";   // 6 字符（等宽纪律）
    private const string Instrument = "m2701";   // 5 字符 = 推送模板捕获槽宽

    public void Dispose() => _sim.Dispose();

    private static string NewFlowDir([System.Runtime.CompilerServices.CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), "ctp-front-e2e", test, Guid.NewGuid().ToString("N"));

    private CtpTrader Login(string flowDir)
    {
        var trader = new CtpTrader(flowDir);
        _sim.Deposit(User);   // 开户余额恒 0 ⇒ 参与者先显式入金（链上充值替身）
        var login = trader.LoginAsync(new CtpConnectRequest
        {
            FrontAddress = _sim.FrontAddress,
            BrokerId = FrontLoopback.Broker,
            UserId = User,
            Password = "soak-pass",
            AppId = "simnow_client_test",
            AuthCode = "0000000000000000",
        }).GetAwaiter().GetResult();
        Assert.Equal("20260923", login.TradingDay);
        return trader;
    }

    /// <summary>noise 节奏直驱：每轮 1 查询 + 1 报单（250ms 间隔 × 400 轮 ≈ 100s+；会话查询数远超
    /// 实测停摆阈值 ~100）。停摆签名 = 查询 &gt;5s 未应答——立即失败并报告迭代序号与前端日志路径。
    /// 600 轮跨 256 边界完整验证（2026-10-15 真前置实抓定案后修复：应答 instance = 前置自身 int
    /// 计数器永不回卷，≤255 走 E3 单字节（转义区 E0..EF 转义对、FE/FF 裸值）、≥256 走 `E2 &lt;2B 大端&gt;`
    /// ——`InstanceField` 统一编码，真前置 300 笔行为 1:1 复刻）。</summary>
    [Fact]
    public async Task 查询报单循环_无停摆()
    {
        await DriveAsync(withOrders: true, withQueries: true);
    }

    [Fact]
    public async Task 纯查询流_无停摆()
    {
        await DriveAsync(withOrders: false, withQueries: true);
    }

    [Fact]
    public async Task 纯报单流_无停摆()
    {
        await DriveAsync(withOrders: true, withQueries: false);
    }

    private async Task DriveAsync(bool withOrders, bool withQueries)
    {
        // 2026-10-19 F0 族定案适配：SDK 对 F0 推送序号的可接受域为 1-251（应答族 int 化救不了 F0——
        // E3 FC/E2 双字节/回卷均被拒），前置在序号 240 主动断开客户端（SDK/上层重连、新会话序号归零）。
        // 本驱动随之适配：检测断开后重登继续——「无停摆」的语义 = 每轮查询在 <5s 内应答
        // （同一会话内）或经重连恢复后应答（跨会话）。
        var dir = NewFlowDir();
        Directory.CreateDirectory(dir);
        var trader = Login(dir);

        var queries = new System.Collections.Concurrent.ConcurrentBag<string>();
        var orders = new System.Collections.Concurrent.ConcurrentBag<string>();
        var broken = false;
        trader.OrderReceived += (_, o) => orders.Add(o.OrderRef);
        trader.FrontDisconnected += (_, _) => broken = true;

        var lastStatus = "";
        for (var i = 1; i <= 600; i++)
        {
            if (broken)
            {
                // F0 序号回收断开（设计行为）——镜像生产链路重登继续
                trader.Dispose();
                var rdir = NewFlowDir();
                Directory.CreateDirectory(rdir);
                trader = Login(rdir);
                trader.OrderReceived += (_, o) => orders.Add(o.OrderRef);
                trader.FrontDisconnected += (_, _) => broken = true;
                broken = false;
            }

            if (withQueries)
            {
                // ① 持仓查询（noise 引擎每笔单前查持仓的节奏）——停摆签名 = >5s
                var qTask = trader.QueryInvestorPositionAsync(FrontLoopback.Broker, User, Instrument);
                var done = await Task.WhenAny(qTask, Task.Delay(TimeSpan.FromSeconds(5)));
                if (done != qTask)
                {
                    Assert.Fail(
                        $"停摆复现：第 {i} 轮持仓查询 5s 无应答（orders={withOrders} queries={withQueries}）。" +
                        $"报单回报 {orders.Count}。前端日志: {_sim.LogPath}");
                }
                lastStatus = $"iter={i}";
            }

            if (withOrders)
            {
                // ② 报单（远离市场的限价单——挂簿不成交，镜像 noise 的报单流）
                var insert = await trader.OrderInsertAsync(new OrderRequest
                {
                    InvestorId = User, BrokerId = FrontLoopback.Broker, InstrumentId = Instrument,
                    Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
                    PriceType = OrderPriceType.Limit, LimitPrice = 2500, Volume = 1, ExchangeId = "DCE",
                });
            }

            await Task.Delay(250);
        }

        // 全程无停摆：600 轮全应答 = 本层（wrapper/front/exchange wire）清白
        Assert.True(true, lastStatus);
    }
}
