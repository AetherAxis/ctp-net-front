using System.Collections.Concurrent;
using Ctp.Net.Core.Trader;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// 线上「登录后约 30s 被客户端 RST（reason=4097）」复刻（2026-10-31 22:5x）：
/// 线上每个会话恰好活 ~28-30s 就收到客户端 RST，而前端日志里既没有 Kick 也没有超时——
/// 即断开是**客户端侧**发起的。本用例在回环里把「登录 + 查 200 行报单」之后**静置 150s**，
/// 看这条链路能不能自己活过 30s：能活过就说明前置/交易所侧没有问题，问题在客户端那 30s 的某个东西。
/// </summary>
public sealed class CtpFrontLongIdleTests : IDisposable
{
    private const string User = "idle1";
    private readonly FrontLoopback _sim = FrontLoopback.Start();
    public void Dispose() => _sim.Dispose();

    [Fact]
    public async Task 登录加查两百行后静置150秒_期间不得断连()
    {
        _sim.SeedMaker();
        var dir = Path.Combine(Path.GetTempPath(), "ctp-front-e2e", "idle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var trader = new CtpTrader(dir);
        _sim.Deposit(User);

        var drops = new ConcurrentQueue<(DateTime At, int Reason)>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        trader.FrontDisconnected += (_, e) => drops.Enqueue((DateTime.UtcNow, e));
        trader.FrontConnected += (_, _) => Console.WriteLine($"[client] FrontConnected @{sw.Elapsed.TotalSeconds:0.0}s");

        await trader.LoginAsync(new CtpConnectRequest
        {
            FrontAddress = _sim.FrontAddress, BrokerId = FrontLoopback.Broker, UserId = User,
            Password = "idle-pass", AppId = "simnow_client_test", AuthCode = "0000000000000000",
        });
        Console.WriteLine($"[client] 登录完成 @{sw.Elapsed.TotalSeconds:0.0}s");

        for (var i = 0; i < 200; i++)
            await trader.OrderInsertAsync(new OrderRequest
            {
                InvestorId = User, BrokerId = FrontLoopback.Broker, InstrumentId = FrontLoopback.Instrument,
                Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
                PriceType = OrderPriceType.Limit, LimitPrice = 100 + i, Volume = 1, ExchangeId = "DCE",
            });
        Console.WriteLine($"[client] 200 单发完 @{sw.Elapsed.TotalSeconds:0.0}s");

        try
        {
            var q = trader.QueryOrderAsync(FrontLoopback.Broker, User);
            var done = await Task.WhenAny(q, Task.Delay(TimeSpan.FromSeconds(15)));
            Console.WriteLine(done == q
                ? $"[client] 查报单 {(await q).Count} 行 @{sw.Elapsed.TotalSeconds:0.0}s"
                : $"[client] 查报单 15s 无应答 @{sw.Elapsed.TotalSeconds:0.0}s");
        }
        catch (Exception ex) { Console.WriteLine($"[client] 查报单异常 {ex.GetType().Name}: {ex.Message}"); }

        // 静置：线上是「活到 ~30s 就断」，这里等到 150s 看有没有第二幕
        for (var i = 0; i < 15; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            Console.WriteLine($"[client] t={sw.Elapsed.TotalSeconds:0}s 断连累计={drops.Count}");
            if (drops.Count > 0) break;
        }

        Assert.True(drops.IsEmpty,
            "静置期间发生断连：" + string.Join(" | ", drops.Select(d => $"@{d.At:HH:mm:ss} reason={d.Reason}"))
            + $"\n前置日志: {_sim.LogPath}");
    }
}
