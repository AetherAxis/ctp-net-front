using System.Diagnostics;
using System.Runtime.CompilerServices;
using Ctp.Net.Core.Trader;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// 查交易所（<c>ReqQryExchange</c>）E2E：前置按真前置实抓形状回「1 帧 N 条记录」，官方 SDK 逐条回调。
/// <para>
/// 帧来源：2026-10-24 真前置实抓——请求 `E104E2802A`、应答 `E104E2802B`（255B payload，6 条记录）。
/// 本地前置的交易所取自**上架合约的 ExchangeId**：`ax001`（AETHER，仿真自有）与 `m2701`（DCE）。
/// </para>
/// </summary>
public sealed class CtpFrontE2eExchangeQueryTests : IDisposable
{
    private readonly FrontLoopback _sim = FrontLoopback.Start();

    public void Dispose() => _sim.Dispose();

    private static string NewFlowDir([CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), "ctp-front-e2e-exch", test, Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task 查交易所_两条_立即返回且名称可读()
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
        var rows = await trader.QueryExchangeAsync();
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"查交易所耗时 {sw.Elapsed}（疑似未应答/超时）");
        Assert.Equal(new[] { "AETHER", "DCE" }, rows.Select(r => r.ExchangeId).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal("大连商品交易所", rows.Single(r => r.ExchangeId == "DCE").ExchangeName);
        Assert.Equal("合成期货交易所", rows.Single(r => r.ExchangeId == "AETHER").ExchangeName);
        Assert.All(rows, r => Assert.Equal((byte)'1', r.PropertyRaw));
    }
}
