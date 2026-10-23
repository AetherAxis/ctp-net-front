using System.Net;
using Ctp.Net;
using Ctp.Net.Front;
using Aether.Exchange.Rpc;
using Aether.Exchange;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// 测试进程内回环（<b>进程分离形态</b>，2026-10-05 起与生产同构）：<see cref="SimTradingCore"/> +
/// <see cref="SimRpcServer"/>（交易所，临时端口）+ <see cref="CtpFrontServer"/>/<see cref="CtpFrontMdServer"/>
/// （前置，经真实 loopback TCP 连交易所）——官方 SDK 客户端（<c>CtpTrader</c>，P/Invoke 官方 C++ 库）
/// 走 客户端 → 前置 → RPC → 交易所 全链路。E2E 跑的即分离架构本身（SDK 即裁判不变）。
/// <para>价格发现归簿（walk/回放已删除）：需要成交的测试先 <see cref="SeedMaker"/> 挂双边流动性。</para>
/// </summary>
public sealed class FrontLoopback : IDisposable
{
    public const string Broker = "9999";
    public const double InitialBalance = 1_000_000;

    /// <summary>确定性时钟：2026-09-23 周三 10:00（日盘）⇒ <c>TradingDay == "20260923"</c>。</summary>
    public static readonly DateTime TradingNow = new(2026, 9, 23, 10, 0, 0);

    public const string Instrument = "m2701";   // 5 字符 = 推送模板捕获槽宽（等宽纪律）

    public SimTradingCore Core { get; }

    public SimRpcServer Exchange { get; }

    public CtpFrontServer Server { get; }

    /// <summary>行情腿前置直连地址（M3 E2E 用——CtpMarketData SDK 客户端）。</summary>
    public string MdAddress { get; }

    /// <summary>官方 SDK 直连地址（tcp:// 前缀——<c>CtpConnectRequest.FrontAddress</c> 契约）。</summary>
    public string FrontAddress { get; }

    /// <summary>服务端逐帧日志的实时落盘路径（xunit 会扣留 Console 输出到测试结束——挂死时唯一诊断面）。</summary>
    public string LogPath { get; }

    private CtpFrontMdServer? _mdServer;

    private FrontLoopback(SimTradingCore core, SimRpcServer exchange, CtpFrontServer server, string frontAddress, string logPath, CtpFrontMdServer mdServer, string mdAddress)
    {
        Core = core;
        Exchange = exchange;
        Server = server;
        FrontAddress = frontAddress;
        LogPath = logPath;
        MdAddress = mdAddress;
        _mdServer = mdServer;
    }

    /// <summary>
    /// maker 账户双边挂单（外部做市客户端语义——价格发现归簿下流动性唯一来源是挂单）：
    /// 买 N@2999 / 卖 N@3001（替身旧 InjectQuote 的「庄家价 2999/3001」）。
    /// </summary>
    public void SeedMaker(int volumePerSide = 20)
    {
        Core.InsertOrder("mm", MakerOrder(Direction.Buy, volumePerSide, 2999));
        Core.InsertOrder("mm", MakerOrder(Direction.Sell, volumePerSide, 3001));
    }

    internal static OrderRequest MakerOrder(Direction direction, int volume, double price) => new()
    {
        BrokerId = Broker,
        InvestorId = "mm",
        InstrumentId = Instrument,
        Direction = direction,
        OffsetFlag = OffsetFlag.Open,
        PriceType = OrderPriceType.Limit,
        LimitPrice = price,
        Volume = volume,
        TimeCondition = TimeCondition.GFD,
    };

    private static FrontLoopback StartCore()
    {
        var options = new SimOptions
        {
            DatabasePath = null,    // 纯内存——绝不碰 ~/.aether/ctp-sim.db（用户真实账本）
            // 上市表双品种：查合约多行钉子（wrapper 静默期收尾，2026-10-05——两行都应到达 SDK）；m2701 = 交易腿测试合约
            ListedInstruments = new[] { "ax001", Instrument },
            InitialBalance = InitialBalance,   // 沙盒水龙头显式开（平台默认已改 0）
        };
        var core = new SimTradingCore(options, new SimClock(() => TradingNow));
        core.InitializeAsync().GetAwaiter().GetResult(); // xunit 无同步上下文，安全

        // 进程分离形态（2026-10-05 真实世界对齐）：交易所（core + SimRpcServer）与前置（CTP 线协议双腿）
        // 经真实 loopback TCP 相连——前置每客户端会话一条交易所 RPC 连接。E2E 跑的即分离架构本身
        // （SDK 即裁判不变：客户端 → 前置 → RPC → 交易所全链路）。交易所监听临时端口（port=0）。
        var exchange = new SimRpcServer(core, "127.0.0.1", 0);
        exchange.Start();
        var exchangePort = (exchange.LocalEndpoint as IPEndPoint)?.Port
            ?? throw new InvalidOperationException("SimRpcServer 未绑定端口");

        // 双写日志：Console（xunit 测试结束时随结果输出）+ 实时文件（xunit 扣留 Console 到测试结束——
        // 测试挂死时文件是唯一的诊断面；服务端逐帧日志是「模板偏移错/签名漏配」定位依据）。
        var logPath = Path.Combine(Path.GetTempPath(), "ctp-front-e2e", $"front-{Guid.NewGuid():N}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);   // macOS 临时目录清空后父目录不存在 ⇒ 12 例全挂（2026-10-07 实踩）
        var logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };
        var server = new CtpFrontServer("127.0.0.1", exchangePort, "127.0.0.1", 0, msg =>
        {
            Console.WriteLine($"[CTP-Front] {msg}");
            logWriter.WriteLine(msg);
        });
        server.Start();
        var port = (server.LocalEndpoint as IPEndPoint)?.Port
            ?? throw new InvalidOperationException("CtpFrontServer 未绑定端口");
        var mdServer = new CtpFrontMdServer("127.0.0.1", exchangePort, "127.0.0.1", 0, msg =>
        {
            Console.WriteLine($"[CTP-Front-MD] {msg}");
            logWriter.WriteLine(msg);
        });
        mdServer.Start();
        var mdPort = (mdServer.LocalEndpoint as IPEndPoint)?.Port
            ?? throw new InvalidOperationException("CtpFrontMdServer 未绑定端口");
        return new FrontLoopback(core, exchange, server, $"tcp://127.0.0.1:{port}", logPath, mdServer, $"tcp://127.0.0.1:{mdPort}");
    }

    /// <summary>
    /// 启动回环柜台（价格发现归簿——无模式开关；tick 由簿事件驱动）。
    /// </summary>
    public static FrontLoopback Start() => StartCore();

    public void Dispose()
    {
        try { Server.Dispose(); } catch { }
        try { _mdServer?.Dispose(); } catch { }
        try { Exchange.Dispose(); } catch { }
        try { Core.Dispose(); } catch { }
        Console.WriteLine($"[CTP-Front] 本测试服务端日志: {LogPath}");
    }
}
