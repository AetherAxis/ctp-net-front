using Ctp.Net.Front;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// 推送帧形状钉子（等宽纪律的机器断言）：F001/⑳/F002 各值槽的输出宽度必须与抓包模板一致——
/// 任何变宽（缩/撑）都会让 SDK 的记录遍历失步（2026-10-13 ax001 双 bot 服务器实测：
/// 88/92/96 价 ⇒ 2B 价格 ⇒ 其后全部推送静默失效 + 4097 重连循环）。
/// </summary>
public sealed class RtnOrderWidthShapeTests
{
    private static byte[] Frame(byte[] payload) => CtpFrontServerFrameFor(payload);

    private static byte[] CtpFrontServerFrameFor(byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        frame[0] = 0x02; frame[1] = 0x00;
        frame[2] = (byte)(payload.Length >> 8); frame[3] = (byte)payload.Length;
        payload.CopyTo(frame, 4);
        return frame;
    }

    // —— F001 OnRtnOrder：帧总宽必须恒定（= 模板宽 + 4B 头）——

    [Theory]
    [InlineData(90.0)]
    [InlineData(89.0)]
    [InlineData(88.0)]      // 2B 剥尾零价（ax001 MM 阶梯实测踩中）
    [InlineData(87.0)]
    [InlineData(86.0)]
    [InlineData(94.0)]
    [InlineData(95.0)]
    [InlineData(96.0)]      // 2B
    [InlineData(97.0)]
    [InlineData(98.0)]
    [InlineData(92.0)]      // 2B（ax001 last 常驻价）
    [InlineData(100.0)]     // 2B（ax001 基价）
    [InlineData(92.03)]     // 8B 非二进小数（含 E0 转义字节）
    [InlineData(3328.0)]    // 2B（m 档 4 位价也有 2B 形）
    [InlineData(0.0)]       // 1B（市价单回显）
    [InlineData(3001.0)]    // E2E 常用价（3B——回归锚）
    public void RtnOrder3_帧宽恒定_不随价格变(double price)
    {
        var payload = CtpFrontTemplates.BuildRtnOrder3(1, "pxuser", "9999", "m2701",
            "000000000001", '2', '0', '0', price, 1, '3', "20260923");
        var frame = Frame(payload);
        // 模板原宽：385B payload + 4B 头 = 389B（price=3328 形）——所有价格同宽
        Assert.Equal(389, frame.Length);
    }

    [Theory]
    [InlineData(88.0)]
    [InlineData(92.03)]
    [InlineData(96.0)]
    public void RtnOrder0_RtnOrderTerminated_帧宽恒定(double price)
    {
        var p0 = CtpFrontTemplates.BuildRtnOrder0(1, "pxuser", "9999", "m2701",
            "000000000001", '2', '0', '0', price, 1, '3', "20260923");
        Assert.Equal(403, Frame(p0).Length);   // 399B 模板 payload + 4B 头

        var pT = CtpFrontTemplates.BuildRtnOrderTerminated(1, "pxuser", "9999", "m2701",
            "000000000001", '2', '0', '0', price, 1, '3', "20260923", '5', "已撤销");
        Assert.Equal(389, Frame(pT).Length);   // '3' 模板 + 等宽状态编辑
    }

    [Fact]
    public void RtnTrade_帧宽恒定_含非二进小数价()
    {
        foreach (var price in new[] { 88.0, 92.0, 92.03, 3001.0, 0.0 })
        {
            var payload = CtpFrontTemplates.BuildRtnTrade(1, "pxuser", "9999", "m2701",
                "000000000001", "1234", '0', '0', price, 1, "20260923");
            Assert.Equal(287, Frame(payload).Length);   // 283B payload + 4B 头
        }
    }

    [Fact]
    public void 拒单帧_帧宽恒定_含非二进小数价()
    {
        foreach (var price in new[] { 88.0, 92.0, 92.03, 3001.0 })
        {
            var rsp = CtpFrontTemplates.BuildOrderInsertRspErr(1, 4, 90005, "资金不足", "pxuser", "9999",
                "m2701", "000000000001", '2', '0', '0', price, 1, '3');
            Assert.Equal(168, Frame(rsp).Length);   // 164B payload + 4B 头

            var rtn = CtpFrontTemplates.BuildErrRtnOrderInsert(1, 90005, "资金不足", "pxuser", "9999",
                "m2701", "000000000001", '2', '0', '0', price, 1, '3');
            Assert.Equal(167, Frame(rtn).Length);   // 163B payload + 4B 头
        }
    }

    [Fact]
    public void RtnOrderTerminated_状态消息恒6B_转义膨胀安全()
    {
        // 含 E0..EF/FE GBK 字节的消息（转义 +1/字节）不得撑破 6B 槽
        foreach (var msg in new[] { "已撤销", "部成交", "重启后未成交挂单已作废", "终态", "" })
        {
            var payload = CtpFrontTemplates.BuildRtnOrderTerminated(1, "pxuser", "9999", "m2701",
                "000000000001", '2', '0', '0', 88.0, 1, '3', "20260923", '5', msg);
            Assert.Equal(389, Frame(payload).Length);
        }
    }
}
