using System.Collections.Concurrent;
using Ctp.Net.Core.Trader;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// 推送价格槽等宽钉子（2026-10-13 ax001 双 bot 实测踩中的根因回归）：
/// F001/⑳ 帧的价格槽按抓包 = 3B，而 <c>TrimmedDouble</c> 宽度随值——88/92/96/100（整数价，
/// double 第 3 字节为 0）剥尾零后 2B、92.03 等非二进小数价 8B ⇒ 帧缩/撑 ⇒ SDK 记录遍历失步 ⇒
/// 其后全部推送静默失效 + 连接 4097 重连循环。服务器实测：MM 阶梯 90/89（3B）能收回报、
/// 88 起全丢（10s 看门狗判无效 → 无限重挂-断连循环）。m2701 E2E 一直绿是因为 2785/3001 等
/// 4 位价恰好 3B——本钉子用 ax001 形状的价格补盲区。
/// </summary>
public sealed class CtpFrontPriceWidthTests : IDisposable
{
    private readonly FrontLoopback _sim = FrontLoopback.Start();
    private const string User = "pxuser";   // 6 字符（等宽纪律）
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
            Password = "px-pass",
            AppId = "simnow_client_test",
            AuthCode = "0000000000000000",
        }).GetAwaiter().GetResult();
        Assert.Equal("20260923", login.TradingDay);
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

    /// <summary>
    /// MM 阶梯价格的完整回调到达：10 档限价单（含三个 2B 剥尾零价 88/92/96 与一个 8B 非二进小数价 92.03）
    /// 全部收到 OnRtnOrder '3'。修复前：前两档（90/89 恰 3B）到达、第三档 88 起全静默丢失。
    /// </summary>
    [Fact]
    public async Task MM阶梯价格_全部订单回报到达()
    {
        var dir = NewFlowDir();
        Directory.CreateDirectory(dir);
        using var trader = Login(dir);

        var orders = new ConcurrentBag<Order>();
        trader.OrderReceived += (_, o) => orders.Add(o);

        // MM 阶梯（服务器实测原样）：买 90/89/88/87/86 + 卖 94/95/96/97/98 + 一个 92.03 噪声价
        var ladder = new[] { 90, 89, 88, 87, 86, 94, 95, 96, 97, 98, 92.03 };
        foreach (var px in ladder)
        {
            await trader.OrderInsertAsync(new OrderRequest
            {
                InvestorId = User, BrokerId = FrontLoopback.Broker, InstrumentId = Instrument,
                Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
                PriceType = OrderPriceType.Limit, LimitPrice = px, Volume = 1, ExchangeId = "DCE",
            });
        }

        Assert.True(await UntilAsync(() => orders.Count >= ladder.Length),
            $"OnRtnOrder 只到了 {orders.Count}/{ladder.Length}——推送帧把 SDK 解析失步了（价格槽变宽）。\n" +
            $"已到 ref: {string.Join(",", orders.Select(o => $"{o.OrderRef}@{o.LimitPrice}"))}\n服务端日志: {_sim.LogPath}");
    }
}
