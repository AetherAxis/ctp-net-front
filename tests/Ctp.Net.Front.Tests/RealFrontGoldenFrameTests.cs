using Ctp.Net.Front;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// 真前置 golden 帧回归（2026-10-24 SimNow 官服实抓，只读会话：登录/结算/查资金/查持仓/查报单——
/// 未下任何单）。抓包路径：⑨ Ctp.Net.Capture 的 `proxy` 模式挡在 Aether.CtpConsole 与
/// tcp://182.254.243.31:40001 之间，落 S→C 原始字节。
/// <para>
/// 用途：把「我们的应答帧 == 真前置应答帧」钉成断言。模板来自 2026-09-29 的旧抓包，靠注释维护
/// 一致性迟早漂移；本文件的期望值来自**真前置实测字节**，任何改模板的改动都必须先过这里。
/// 只收无凭据字段（账户/用户/交易日等替换值不入库，改用长度与固定段断言）。
/// </para>
/// </summary>
public sealed class RealFrontGoldenFrameTests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b);

    // —— 真前置 S→C 应答 payload（已剥掉 4B 帧头 02 00 00 0D；session token/账户字段在空表帧里本就没有）——

    /// <summary>真前置持仓查询·空表应答 payload（13B，`E104E28005` + `E3 02` + `E7 06`）。</summary>
    private const string RealQryPositionEmpty = "0103344CE104E28005E302E706";

    /// <summary>真前置查报单·空表应答 payload（13B，`E104E28001` + `E3 03` + `E7 07`）——
    /// 2026-10-24 实证：真前置在无匹配报单时**确实会回这个 13B tag 帧**（此前只知「SDK 会超时」，
    /// 超时是 SDK 侧对空结果不派发的既有行为，非前置不应答）。</summary>
    private const string RealQryOrderEmpty = "0103344CE104E28001E303E707";

    [Fact]
    public void 持仓空表应答_与真前置逐字节一致()
    {
        // 真帧 = instance 02 / reqId 06（前置自身 80 族流水 + 请求 reqId 回显）
        Assert.Equal(RealQryPositionEmpty, Hex(CtpFrontTemplates.BuildQryPositionEmptyRsp(2, 0x06)));
    }

    [Fact]
    public void 查报单空表应答_与真前置逐字节一致()
    {
        Assert.Equal(RealQryOrderEmpty, Hex(CtpFrontTemplates.BuildQryOrderEmptyRsp(3, 0x07)));
    }

    [Fact]
    public void 查结算单空表应答_与真前置逐字节一致()
    {
        // 真帧：2026-10-24 实抓 `0103344C E104E2803D E301 E705`（instance 01 / reqId 05）
        Assert.Equal("0103344CE104E2803DE301E705", Hex(CtpFrontTemplates.BuildQrySettlementInfoEmptyRsp(1, 0x05)));
    }

    [Fact]
    public void 资金应答_线上宽度与真前置一致()
    {
        // 真帧：balance 999927.69（E0 转义后 10B/槽）、6 字符账户 → payload 134B（+4B 帧头 = 138B 线上）。
        // 宽度漂移是本仓最贵的 bug 类型（帧缩/撑 ⇒ SDK 记录遍历失步 ⇒ 断连风暴），必须钉死。
        var payload = CtpFrontTemplates.BuildQryAccountRsp(
            instance: 1, reqId: 5, brokerId: "9999", accountId: "tstacct", userId: "tstusr",
            tradingDay: "20260101", balance: 999927.69);

        Assert.Equal(134, payload.Length);
        Assert.StartsWith("0103344CE104E28007E301E10101", Hex(payload));
        // 6 个余额槽同一值——真前置每个槽都是 EscapedMoney 编码（41 2E 83 E0 EF 61 47 AE 14）
        Assert.Equal(6, CountOccurrences(Hex(payload), "412E83E0EF6147AE14"));
    }

    [Fact]
    public void 结算确认应答_线上宽度与真前置一致()
    {
        // 真帧：payload 65B（+4B 帧头 = 69B 线上）
        var payload = CtpFrontTemplates.BuildSettleRsp(
            instance: 1, reqId: 3, brokerId: "9999", userId: "tstusr", tradingDay: "20260101");

        Assert.Equal(65, payload.Length);
        Assert.StartsWith("0103344CE101E24014E301E102E19CE303E355E4D5FDC8B7EFEFEFEFEFE2040FE13F39393939E7", Hex(payload));
        Assert.EndsWith("EFE7", Hex(payload));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
