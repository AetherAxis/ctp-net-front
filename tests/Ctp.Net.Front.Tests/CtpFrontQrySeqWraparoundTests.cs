using System.Collections.Concurrent;
using Ctp.Net.Core.Trader;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// 80 族应答流水**越过 255** 的连续性钉子（2026-10-31 22:5x 线上定位）：
/// 线上每 30s 断一次、断前「持仓查询 5s 未返回（会话半死）」成百上千次；前置日志里每个会话都在
/// 第二个 200 行查报单上把 80 族流水从 207 推到 406（跨 255）。SDK 的反汇编判据是
/// <c>HandlePackage: pkg[+0x30] != 上一包+1 ⇒ 整包丢弃</c>，一旦跨 255 的编码有个字节对不上，
/// 后面的应答就会被**静默丢掉**——表现正是「查询无应答 / 会话半死 / 随后 RST」。
/// <para>
/// 本用例在回环里连打 4 轮查报单，让前置的 80 族流水必然越过 255（每轮消耗 ~80 个号），
/// 每一轮都必须拿回行、且全程不得断连。
/// </para>
/// </summary>
public sealed class CtpFrontQrySeqWraparoundTests : IDisposable
{
    private const string User = "seq1";
    private readonly FrontLoopback _sim = FrontLoopback.Start();
    public void Dispose() => _sim.Dispose();

    [Fact]
    public async Task 连续四轮查报单跨越流水255_每轮都要回行且不断连()
    {
        _sim.SeedMaker();
        var dir = Path.Combine(Path.GetTempPath(), "ctp-front-e2e", "seq", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var trader = new CtpTrader(dir);
        _sim.Deposit(User);

        var drops = new ConcurrentQueue<int>();
        trader.FrontDisconnected += (_, reason) => drops.Enqueue(reason);

        await trader.LoginAsync(new CtpConnectRequest
        {
            FrontAddress = _sim.FrontAddress, BrokerId = FrontLoopback.Broker, UserId = User,
            Password = "seq-pass", AppId = "simnow_client_test", AuthCode = "0000000000000000",
        });
        for (var i = 0; i < 200; i++)
            await trader.OrderInsertAsync(new OrderRequest
            {
                InvestorId = User, BrokerId = FrontLoopback.Broker, InstrumentId = FrontLoopback.Instrument,
                Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
                PriceType = OrderPriceType.Limit, LimitPrice = 100 + i, Volume = 1, ExchangeId = "DCE",
            });

        var rounds = new List<int>();
        for (var round = 0; round < 4; round++)
        {
            var q = trader.QueryOrderAsync(FrontLoopback.Broker, User);
            var done = await Task.WhenAny(q, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(done == q, $"第 {round + 1} 轮查报单 10s 无应答（前几轮行数：{string.Join(",", rounds)}）");
            rounds.Add((await q).Count);
            await Task.Delay(300);
        }
        Assert.True(drops.IsEmpty, "查询期间断连 reason=" + string.Join(",", drops) + $"\n前置日志: {_sim.LogPath}");
        Assert.All(rounds, n => Assert.True(n > 0, "有一轮查报单回了 0 行：" + string.Join(",", rounds)));
    }
}
