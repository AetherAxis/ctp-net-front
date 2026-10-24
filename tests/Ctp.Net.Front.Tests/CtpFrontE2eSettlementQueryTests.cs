using System.Diagnostics;
using System.Runtime.CompilerServices;
using Ctp.Net.Core.Trader;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// 查结算单（<c>ReqQrySettlementInfo</c>）E2E：官方 SDK 发请求 → 前置回真前置同形 13B tag 空表 →
/// SDK 以「空记录 + isLast」完成查询，客户端 0 条且**立即返回**（旧行为：前置不识别 ⇒ 30s 超时）。
/// <para>
/// 帧来源：2026-10-24 真前置实抓——请求 `E104E2803C`（39B）、应答 `E104E2803D E301 E705`（17B 线上/13B payload）。
/// SimNow 测试账户当日无结算单，真前置就回这一帧；**非空正文（有结算单时）**尚未逆向，见 DESIGN 6c。
/// </para>
/// </summary>
public sealed class CtpFrontE2eSettlementQueryTests : IDisposable
{
    private readonly FrontLoopback _sim = FrontLoopback.Start();

    public void Dispose() => _sim.Dispose();

    private static string NewFlowDir([CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), "ctp-front-e2e-settle", test, Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task 查结算单_空表应答_立即返回零条()
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
        var parts = await trader.QuerySettlementInfoAsync(FrontLoopback.Broker, "e2eusr");
        sw.Stop();

        Assert.Empty(parts);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"查结算单空表耗时 {sw.Elapsed}（疑似未应答/超时）");
    }
}
