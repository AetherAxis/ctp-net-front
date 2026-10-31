using Ctp.Net.Core.Trader;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// 2026-10-31 线上风暴复刻：**同一前置实例**上反复「新会话登录 → 立刻查 200 行 → 断开」。
/// 线上表现为每个新会话的第一条 80 族记录都是流水 1..200，200 行写完客户端即 RST（reason=4097），
/// 重连后重复，形成自持风暴；10:12 自行停止。本用例用同一 FrontLoopback（计数器每会话从 1 起）连打多轮。
/// </summary>
public sealed class CtpFrontReconnectStormReproTests : IDisposable
{
    private const string User = "storm1";
    private readonly FrontLoopback _sim = FrontLoopback.Start();
    public void Dispose() => _sim.Dispose();

    [Fact]
    public async Task 反复新会话登录后立刻查两百行_每轮都要零断连()
    {
        _sim.SeedMaker();
        var failures = new List<string>();
        for (var round = 0; round < 4; round++)
        {
            var dir = Path.Combine(Path.GetTempPath(), "ctp-front-e2e", "storm", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            using var trader = new CtpTrader(dir);
            _sim.Deposit(User);
            var dropped = false;
            trader.FrontDisconnected += (_, e) => dropped = true;
            await trader.LoginAsync(new CtpConnectRequest
            {
                FrontAddress = _sim.FrontAddress, BrokerId = FrontLoopback.Broker, UserId = User,
                Password = "storm-pass", AppId = "simnow_client_test", AuthCode = "0000000000000000",
            });
            for (var i = 0; i < 200; i++)
                await trader.OrderInsertAsync(new OrderRequest
                {
                    InvestorId = User, BrokerId = FrontLoopback.Broker, InstrumentId = FrontLoopback.Instrument,
                    Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
                    PriceType = OrderPriceType.Limit, LimitPrice = 100 + i, Volume = 1, ExchangeId = "DCE",
                });
            await Task.Delay(800);
            IReadOnlyList<Order> rows = Array.Empty<Order>();
            try
            {
                var q = trader.QueryOrderAsync(FrontLoopback.Broker, User);
                var done = await Task.WhenAny(q, Task.Delay(TimeSpan.FromSeconds(10)));
                if (done == q) rows = await q; else failures.Add($"轮{round}: 查询 10s 无应答");
            }
            catch (Exception ex) { failures.Add($"轮{round}: 查询异常 {ex.GetType().Name} {ex.Message}"); }
            if (dropped) failures.Add($"轮{round}: 前置断开");
            if (rows.Count != 200 && !failures.Any(f => f.StartsWith($"轮{round}")))
                failures.Add($"轮{round}: 只回 {rows.Count} 行");
        }
        Assert.True(failures.Count == 0, string.Join(" | ", failures) + $"\n前置日志: {_sim.LogPath}");
    }
}
