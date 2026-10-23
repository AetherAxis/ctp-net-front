using System.Runtime.CompilerServices;
using Ctp.Net.Core.Trader;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// M1 全链路 E2E：官方 CTP SDK 客户端（<see cref="CtpTrader"/>——P/Invoke 官方 C++ 库）直连
/// <c>CtpFrontServer</c>，走 握手→挑战→认证→登录→结算→查资金→查持仓 全链路。
/// <para>
/// 验收语义 = <b>SDK 即裁判</b>：官方 C++ SDK 自己解析我们回放的帧，字段值对不上/帧形状错
/// ⇒ 回调不触发 ⇒ <c>CtpTrader</c> 15~30s 超时抛异常 ⇒ 测试失败。这就是
/// 「让所有 CTP Client 认为自己连的是真实 CTP Front」的机器判定。
/// </para>
/// </summary>
public sealed class CtpFrontE2eTests : IDisposable
{
    // xunit 每测试方法新建类实例 ⇒ 逐测试独立的 FrontLoopback（核心+端口互不污染）
    private readonly FrontLoopback _sim = FrontLoopback.Start();

    public void Dispose() => _sim.Dispose();

    /// <summary>每个 CtpTrader 实例一个全新流文件目录（CTP SDK 落流文件；复用目录会重放旧流）。</summary>
    private static string NewFlowDir([CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), "ctp-front-e2e", test, Guid.NewGuid().ToString("N"));

    // 用户名长度是承重约束：模板对 user 字段的 Edit 槽宽 = 抓包值 "247163" 的 6 字符
    // （⑥/⑧/⑩/⑫ 的 user Edit 均 OldLen=6）。等宽用户下这些替换零增长；更长/更短的用户
    // 会撑大/缩短 payload——若帧内有内部长度/计数字段未同步，SDK 记录遍历会失步挂死。
    // 真实用户 ID 常为 7~8 位 ⇒ 终局方案需生成器层重算长度字段，等宽只是本实验的隔离变量。
    // （⑯ 资金应答槽#1 已由生成器做等宽守卫：accountId[..5] 截断，5 进 5 出零增长。）
    private const string User6 = "e2eusr";
    private const string User6B = "twiceu";

    private static CtpConnectRequest Connect(FrontLoopback sim, string user, string password) => new()
    {
        FrontAddress = sim.FrontAddress,
        BrokerId = FrontLoopback.Broker,
        UserId = user,
        Password = password,
        AppId = "simnow_client_test",
        AuthCode = "0000000000000000",
    };

    [Fact]
    public async Task 官方SDK直连前置_登录结算资金持仓全链路()
    {
        var flowDir = NewFlowDir();
        Directory.CreateDirectory(flowDir);
        using var trader = new CtpTrader(flowDir);

        // 登录（SDK 内部：⑤认证→⑦登录→⑧预响应→⑨系统信息→⑩登录结果→⑪自动结算确认）
        var login = await trader.LoginAsync(Connect(_sim, User6, "e2e-pass"));
        Assert.Equal("20260923", login.TradingDay);
        // 客户端契约：MaxOrderRef 必须可 int.Parse（登录后拿它做起始单号）
        Assert.True(int.Parse(login.MaxOrderRef) >= 0, $"MaxOrderRef 非数值: {login.MaxOrderRef}");

        // 显式结算确认（⑬→⑭，DirectTest 同款流程；instance 自增 ②）
        await trader.ConfirmSettlementAsync(FrontLoopback.Broker, User6);

        // 资金查询（⑮→⑯：六个余额字段全 ← Balance，E0 转义大端 double——tag 字母表字节前插 E0；
        // 首登开户 = InitialBalance）
        var account = await trader.QueryTradingAccountAsync(FrontLoopback.Broker, User6);
        Assert.Equal(User6, account.AccountId);
        Assert.Equal(FrontLoopback.InitialBalance, account.Balance);

        // 持仓查询（⑰→⑱：空表——新账户无持仓）。
        // 真实前置的空表行为（SDK 即裁判实证）：⑱ 是真 SimNow 对无持仓账户的原样应答，
        // 官方 SDK 解析后回调一次「非空指针但全零」的持仓结构体（isLast=true），包装层
        // 无条件入列 ⇒ 空表呈现为 1 条全零占位记录而非 0 条。语义断言 = 无任何记录持有实际仓位。
        var positions = await trader.QueryInvestorPositionAsync(FrontLoopback.Broker, User6);
        Assert.DoesNotContain(positions, p => p.TotalPosition != 0 || p.TodayPosition != 0 || p.YdPosition != 0);
    }

    [Fact]
    public async Task 同账户同密码二次登录_固定挑战密文当哈希匹配()
    {
        // 第一次登录 = 开户（密文 hex 前缀存档为账户密码）
        var dir1 = NewFlowDir();
        Directory.CreateDirectory(dir1);
        using (var trader1 = new CtpTrader(dir1))
        {
            var login1 = await trader1.LoginAsync(Connect(_sim, User6B, "twice-pass"));
            Assert.Equal("20260923", login1.TradingDay);
        }

        // 第二次登录（全新流文件目录 = 全新 SDK 实例）：固定挑战 ⇒ 同密码同密文 ⇒ 存档比对通过
        var dir2 = NewFlowDir();
        Directory.CreateDirectory(dir2);
        using var trader2 = new CtpTrader(dir2);
        var login2 = await trader2.LoginAsync(Connect(_sim, User6B, "twice-pass"));
        Assert.Equal("20260923", login2.TradingDay);
    }

    /// <summary>
    /// 桌面端连接器实测踩中的形态（2026-09-30：连接器表单 AppId/AuthCode 留空 + 4 字符用户号
    /// ⇒ 连接失败）。修复前 ⑥ 的 18B AppId 槽 + ⑥⑧⑩⑫ 的 6B user 槽被变长替换缩帧 ⇒ SDK
    /// 记录遍历失步 ⇒ LoginAsync 超时。修复 = 模板 <c>F()</c> 等宽归一化（截断/右补空格到抓包
    /// 槽宽）——任意宽度用户/AppId 都归一到抓包帧形状。注意不断言 AccountId 精确值（4 字符
    /// 用户在 ⑯ 回显为补空格的 6 字符——回显仅语义展示，账户身份以核心账本为准）。
    /// </summary>
    [Fact]
    public async Task 桌面端连接器形态_空AppId短用户_等宽归一化后全链路()
    {
        var flowDir = NewFlowDir();
        Directory.CreateDirectory(flowDir);
        using var trader = new CtpTrader(flowDir);

        var login = await trader.LoginAsync(new CtpConnectRequest
        {
            FrontAddress = _sim.FrontAddress,
            BrokerId = FrontLoopback.Broker,
            UserId = "tst1",            // 4 字符——短于抓包 6B user 槽
            Password = "desk-pass",
            AppId = "",                 // 桌面端连接器表单留空——⑥ 的 18B AppId 槽
            AuthCode = "",
        });
        Assert.Equal("20260923", login.TradingDay);
        Assert.True(int.Parse(login.MaxOrderRef) >= 0, $"MaxOrderRef 非数值: {login.MaxOrderRef}");

        await trader.ConfirmSettlementAsync(FrontLoopback.Broker, "tst1");

        var account = await trader.QueryTradingAccountAsync(FrontLoopback.Broker, "tst1");
        Assert.Equal(FrontLoopback.InitialBalance, account.Balance);

        var positions = await trader.QueryInvestorPositionAsync(FrontLoopback.Broker, "tst1");
        Assert.DoesNotContain(positions, p => p.TotalPosition != 0 || p.TodayPosition != 0 || p.YdPosition != 0);
    }

    /// <summary>
    /// 非 6 字符用户：客户端 SDK 把 <b>⑦ 登录帧的 user 序列化成定长 6 字节槽</b>（2026-10-12 服务器实测踩中：
    /// botmm(5)→"botmm "补空格、botnoise(8)→"botnoi"截断 ⇒ 前置绑定被污染的账户名 ⇒ 连到 0 余额幽灵户，
    /// 真身 300 万永远查不到）。修复 = <b>绑定账户名以 ⑤ 认证帧的变长完整 user 为准</b>（⑦ 的解析结果丢弃）。
    /// 分辨手段 = 资金差：真身充 7 AQ（Balance=1000007）；错绑幽灵户 = 首登开户 1000000。
    /// </summary>
    [Theory]
    [InlineData("botmm")]      // 5 字符——⑦ 定长槽补空格场景
    [InlineData("botnoise")]   // 8 字符——⑦ 定长槽截断场景
    public async Task 非6字符用户_绑定以认证帧完整user为准(string user)
    {
        var flowDir = NewFlowDir();
        Directory.CreateDirectory(flowDir);
        using var trader = new CtpTrader(flowDir);

        _sim.Core.CreditQuoteDeposit(user, 700_000_000);   // 真身 +7 AQ（1 AQ = 1 现金）

        var login = await trader.LoginAsync(Connect(_sim, user, "e2e-pass"));
        Assert.Equal("20260923", login.TradingDay);

        await trader.ConfirmSettlementAsync(FrontLoopback.Broker, user);

        var account = await trader.QueryTradingAccountAsync(FrontLoopback.Broker, user);
        Assert.Equal(1_000_007, account.Balance, 2);   // 绑定真身（错绑幽灵 = 1000000）
    }
}
