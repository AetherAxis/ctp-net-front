using System.Diagnostics;
using System.Runtime.CompilerServices;
using Ctp.Net.Core.Trader;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// 查成交（<c>ReqQryTrade</c>）E2E：官方 SDK 发请求 → 前置回真前置同形 13B tag 空表 → SDK 以
/// 「空记录 + isLast」完成查询，客户端 0 行且**立即返回**（旧行为：前置不应答 ⇒ 30s 超时）。
/// </summary>
public sealed class CtpFrontE2eTradeQueryTests : IDisposable
{
    private readonly FrontLoopback _sim = FrontLoopback.Start();

    public void Dispose() => _sim.Dispose();

    private static string NewFlowDir([CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), "ctp-front-e2e-trade", test, Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task 查成交_空表应答_立即返回零行()
    {
        var flowDir = NewFlowDir();
        Directory.CreateDirectory(flowDir);
        using var trader = new CtpTrader(flowDir);
        await trader.LoginAsync(new CtpConnectRequest
        {
            FrontAddress = _sim.FrontAddress, BrokerId = FrontLoopback.Broker,
            UserId = "e2eusr", Password = "e2e-pass",
            AppId = "simnow_client_test", AuthCode = "0000000000000000",
        });

        var sw = Stopwatch.StartNew();
        var trades = await trader.QueryTradeAsync(FrontLoopback.Broker, "e2eusr");
        sw.Stop();

        Assert.Empty(trades);
        // 空表必须走「应答帧 → 空记录回调 → 静默期收尾」的快速路径，而不是 30s 超时
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"查成交空表耗时 {sw.Elapsed}（疑似未应答/超时）");
    }
}
