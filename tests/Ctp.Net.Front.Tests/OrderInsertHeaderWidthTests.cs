using Ctp.Net.Front;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// 报单帧判定的宽度钉子（2026-10-30 probe1 单会话 200 笔报单实测）。
/// <para>
/// 现象：会话跑到第 127 单就不再有单子进交易所，日志里满屏「未知业务帧」，
/// 客户端侧表现为「跑一阵就不干活了，重连又好了」。
/// </para>
/// <para>
/// 根因：<c>E1 01 E2 40</c> 之后的 op/实例字段随实例号变宽——买/撤共用一条逐请求自增的计数器
/// （买偶数、撤奇数），≤254 写 <c>E4 &lt;1B&gt;</c>，≥255 改写 <c>E3 &lt;2B&gt;</c>。
/// 旧判据把签名钉成 5 字节 <c>E101E240E4</c>，越界后整类帧失去匹配。
/// </para>
/// <para>
/// 下面两组帧都是前置日志里原样抄下来的（hex 前缀 + 长度足够走完判定）。
/// </para>
/// </summary>
public sealed class OrderInsertHeaderWidthTests
{
    /// <summary>实例号 254（末一个窄形态，<c>E4 FE</c>）。</summary>
    private static readonly byte[] Insert1B = Convert.FromHexString(
        "E101E240E4FEE101017BE31104E1017739393939E770726F626531");

    /// <summary>实例号 258（跨过 255 后的宽形态，<c>E3 01 02</c>）——</summary>
    private static readonly byte[] Insert2B = Convert.FromHexString(
        "E101E240E30102E101017BE31304E1017739393939E770726F626531");

    /// <summary>实例号 398，同一形态的第二个样本（<c>E3 01 8E</c>）。</summary>
    private static readonly byte[] Insert2BHigh = Convert.FromHexString(
        "E101E240E3018EE101017BE3A104E1017739393939E770726F626531");

    /// <summary>撤单：op 固定在 body[4]，实例号在 <c>E3</c> 之后 1 字节。</summary>
    private static readonly byte[] Action = Convert.FromHexString(
        "E101E24006E3FFE101013BE3120404013739393939E770726F626531");

    [Fact]
    public void 窄形态_E4_一字节实例_判为报单()
        => Assert.True(CtpOrderWireCodec.IsOrderInsert(Insert1B));

    [Fact]
    public void 宽形态_E3_两字节实例_同样判为报单()
    {
        Assert.True(CtpOrderWireCodec.IsOrderInsert(Insert2B));
        Assert.True(CtpOrderWireCodec.IsOrderInsert(Insert2BHigh));
    }

    [Fact]
    public void 撤单不判为报单()
        => Assert.False(CtpOrderWireCodec.IsOrderInsert(Action));

    [Fact]
    public void 结算确认帧不判为报单()
    {
        // ⑪⑬ 结算确认也走 40 路由，op = 0x13；帧内没有报单 reqId 标记 ⇒ 不该被报单分支吞掉
        var settle = Convert.FromHexString("E101E24013E101E143E3100404013739393939E770726F626531");
        Assert.False(CtpOrderWireCodec.IsOrderInsert(settle));
    }

    [Fact]
    public void 旧判据在宽形态上必然失配_这就是断流成因()
    {
        // 旧写法：前 5 字节 == E1 01 E2 40 E4。窄形态过得去，宽形态过不去。
        static bool OldSignature(byte[] b) => b.Length >= 5 && b[0] == 0xE1 && b[1] == 0x01 && b[2] == 0xE2 && b[3] == 0x40 && b[4] == 0xE4;

        Assert.True(OldSignature(Insert1B));
        Assert.False(OldSignature(Insert2B));
        Assert.False(OldSignature(Insert2BHigh));
    }

    // —— 第二层：表头变宽后「量」被锚到帧头 ——

    /// <summary>ref=406，实例号 0x0132=306：`E3 01 32` 里 `0x01<0x20` + 尾字节 '2' 满足 VolShape，
    /// 修复前 FindRunIndex 锚到帧头，价格锚空 ⇒ 整帧 null（实测线上丢单）。</summary>
    private static readonly byte[] RealRef406 = Convert.FromHexString(
        "E101E240E30132E101017BE34504E1017739393939E770726F626531EFEF" +
        "E8303030303030303030343036E170726F626531EA323030E431E4405680E80133E931" +
        "E30131E830EFEFEFEFEFEFEFEFE86178303031EFEFEFEFEFEFEFE4");

    /// <summary>ref=407，实例号 0x0134=308（尾字节 '4'）。</summary>
    private static readonly byte[] RealRef407 = Convert.FromHexString(
        "E101E240E30134E101017BE34704E1017739393939E770726F626531EFEF" +
        "E8303030303030303030343037E170726F626531EA323030E431E4405680E80133E931" +
        "E30131E830EFEFEFEFEFEFEFEFE86178303031EFEFEFEFEFEFEFE4");

    /// <summary>ref=408，实例号 0x0136=310（尾字节 '6'）——同一批里的对照组，修复前就是好的。</summary>
    private static readonly byte[] RealRef408 = Convert.FromHexString(
        "E101E240E30136E101017BE34904E1017739393939E770726F626531EFEF" +
        "E8303030303030303030343038E170726F626531EA323030E431E4405680E80133E931" +
        "E30131E830EFEFEFEFEFEFEFEFE86178303031EFEFEFEFEFEFEFE4");

    [Fact]
    public void 实例号是0x0100整数倍时_连接帧头的E1被吃掉_仍能判为报单并解析()
    {
        // ref=681 的实抓帧（实例号 256）：SDK 把 `<c>E1 01 E2 40 E3 01</c>` 后面的 reqId 家族标记
        // 写成 `01 01 7B`——少了那个 E1，与 258 的 `E1 01 01 7B` 不同。两次实测（256/512）都丢在这里。
        var body681 = Convert.FromHexString(
            "E101E240E301E201017BE31304E1017739393939E770726F626531EFEFE8" +
            "303030303030303030363831E170726F626531EA323030E431E4405680E80133E931E30131E830" +
            "EFEFEFEFEFEFEFEFE86178303031EFEFEFEFEFEFEFE4");

        Assert.True(CtpOrderWireCodec.IsOrderInsert(body681));

        var o = CtpOrderWireCodec.ParseOrderInsert(body681);
        Assert.NotNull(o);
        Assert.Equal("000000000681", o!.OrderRef);
        Assert.Equal("ax001", o.InstrumentId);
        Assert.Equal(90.0, o.LimitPrice, 6);
        Assert.Equal(1, o.Volume);
        Assert.Equal('3', o.TimeCondition);
    }

    [Theory]
    [InlineData(406)]
    [InlineData(407)]
    [InlineData(408)]
    public void 表头实例号低字节撞上TimeCondition_仍能解析出完整报单(int expectedRef)
    {
        var body = expectedRef switch
        {
            406 => RealRef406,
            407 => RealRef407,
            _ => RealRef408,
        };

        var o = CtpOrderWireCodec.ParseOrderInsert(body);

        Assert.NotNull(o);
        Assert.Equal("000000000406".Substring(0, 9) + expectedRef.ToString("D3"), o!.OrderRef);
        Assert.Equal("ax001", o.InstrumentId);
        Assert.Equal(90.0, o.LimitPrice, 6);
        Assert.Equal(1, o.Volume);
        Assert.Equal('3', o.TimeCondition);   // 真实 TimeCondition 是 '3'(GFD)，不是表头那个假 '2'/'4'
    }
}
