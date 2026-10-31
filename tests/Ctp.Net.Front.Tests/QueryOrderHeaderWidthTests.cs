using Ctp.Net.Front;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// 查报单帧判定的宽度钉子（2026-10-31 浸泡实抓）。
/// <para>
/// 现象：两个 console bot 在 22:16 切到前置后，约 12 分钟起进入「查询管线半死」——查报单每轮
/// 8s 超时、持仓查询 rc=-2、持仓只能吃缓存快照、交易腿周期 4097 重连（客户端自己掐的连接，
/// 前置侧没有任何 <c>会话关闭</c> 记录）。
/// </para>
/// <para>
/// 根因：<c>E1 04 E2 80</c> 之后的 op/实例字段<b>随 SDK 链路计数变宽</b>——≤254 写
/// <c>E4 &lt;1B&gt;</c>，≥255 写 <c>E3 &lt;2B&gt;</c>（与⑲报单的 #23 同款）。旧判据把签名钉成
/// 5 字节 <c>E104E280E4</c>，越界后每一条查报单都掉进「未知业务帧」<b>永不应答</b>，
/// 而查报单**必须应答**——否则 SDK 查询管线堵死 30s，之后的所有查询跟着陪葬。
/// </para>
/// <para>
/// 下面的宽形态帧与负例都是从前置日志原样抄的（<c>未知业务帧: body前缀</c> 行）。
/// </para>
/// </summary>
public sealed class QueryOrderHeaderWidthTests
{
    /// <summary>窄形态（计数 ≤254）：<c>E4 05</c>，reqId=0x0B。</summary>
    private static readonly byte[] Qry1B = Convert.FromHexString(
        "E104E280E405E101E1CDE30B07E2C9");

    /// <summary>宽形态第一个实抓样本（2026-10-30 22:09:12，计数 0x0340=832），reqId=0x2E。</summary>
    private static readonly byte[] Qry2B = Convert.FromHexString(
        "E104E280E30340E101E1CDE32E07E2C9");

    /// <summary>宽形态第二个实抓样本（2026-10-31 09:08:57，计数 0x2645=9797），reqId=0x0B。</summary>
    private static readonly byte[] Qry2BHigh = Convert.FromHexString(
        "E104E280E32645E101E1CDE30B07E2C9");

    /// <summary>临界帧（进程内复现 2026-10-31，第 256 次查报单）：实例号 0x0100 的倍数 ⇒ SDK 把
    /// reqId 标记前的 <c>E1</c> 吃掉（<c>E1 01 E1 CD E3</c> → <c>01 E1 CD E3</c>），与⑲报单同款。
    /// 前置日志原样：<c>未知业务帧: body前缀=E104E280E301E201E1CDE31707E2C939</c>，reqId=0x17。</summary>
    private static readonly byte[] Qry2B_256 = Convert.FromHexString(
        "E104E280E301E201E1CDE31707E2C939");

    // —— 同族其它查询（第 5 字节是固定类型码，全部 <0xE0）——
    private static readonly byte[] QryPosition = Convert.FromHexString("E104E28004E101E1A6E30B07E2C9");
    private static readonly byte[] QryAccount = Convert.FromHexString("E104E28006E101E12EE30B07E2C9");
    private static readonly byte[] QryTrade = Convert.FromHexString("E104E28002E101E1CDE30B07E2C9");
    private static readonly byte[] QryExchange = Convert.FromHexString("E104E2802AE101E10DE30B07E2C9");
    private static readonly byte[] QryInstrument = Convert.FromHexString("E104E2802EE101E15DE30B07E2C9");
    private static readonly byte[] QrySettlement = Convert.FromHexString("E104E2803CE101E136E30B07E2C9");

    [Fact]
    public void 窄形态_E4_一字节计数_判为查报单()
        => Assert.True(CtpOrderWireCodec.IsQueryOrderRequest(Qry1B));

    [Fact]
    public void 宽形态_E3_两字节计数_同样判为查报单()
    {
        Assert.True(CtpOrderWireCodec.IsQueryOrderRequest(Qry2B));
        Assert.True(CtpOrderWireCodec.IsQueryOrderRequest(Qry2BHigh));
    }

    [Fact]
    public void 计数恰为0x0100倍数_标记前E1被吃掉_仍判为查报单且能取到reqId()
    {
        // 第 256 帧：E1 被吃掉后仍是查报单——这是进程内 300 连打的必中边界。
        Assert.True(CtpOrderWireCodec.IsQueryOrderRequest(Qry2B_256));
        // 尾 4 字节锚点命中后紧跟的就是 reqId
        var needle = Convert.FromHexString("01E1CDE3");
        var hit = Qry2B_256.AsSpan().IndexOf(needle);
        Assert.True(hit > 0);
        Assert.Equal(0x17, Qry2B_256[hit + needle.Length]);
    }

    [Fact]
    public void 旧判据在宽形态上必然失配_这就是查询管线堵死的成因()
    {
        // 旧写法：前 5 字节 == E1 04 E2 80 E4。窄形态过得去，宽形态过不去
        // ⇒ 落在「未知业务帧」的 else 分支 ⇒ 一帧不应答 ⇒ SDK 查询管线从此白等。
        static bool OldSignature(byte[] b) => b.Length >= 5 && b[0] == 0xE1 && b[1] == 0x04 && b[2] == 0xE2 && b[3] == 0x80 && b[4] == 0xE4;

        Assert.True(OldSignature(Qry1B));
        Assert.False(OldSignature(Qry2B));
        Assert.False(OldSignature(Qry2BHigh));
    }

    [Theory]
    [MemberData(nameof(OtherQueries))]
    public void 同族其它查询不判为查报单(byte[] body)
        => Assert.False(CtpOrderWireCodec.IsQueryOrderRequest(body));

    public static TheoryData<byte[]> OtherQueries => new()
    {
        QryPosition, QryAccount, QryTrade, QryExchange, QryInstrument, QrySettlement,
    };

    [Fact]
    public void 报单帧不判为查报单()
    {
        // 路由头不同（E1 01 E2 40 vs E1 04 E2 80）——两族互不吞
        var insert = Convert.FromHexString("E101E240E4FEE101017BE31104E1017739393939E770726F626531");
        Assert.False(CtpOrderWireCodec.IsQueryOrderRequest(insert));
    }

    [Fact]
    public void 缺reqId家族标记的80族帧不判为查报单()
    {
        // op 宽形态对上、但没有 E1 01 E1 CD E3 ⇒ 无从回 reqId，按未知处理（与旧行为等价）
        var noNeedle = Convert.FromHexString("E104E280E32645E101E1AAE30B07E2C9");
        Assert.False(CtpOrderWireCodec.IsQueryOrderRequest(noNeedle));
    }

    [Fact]
    public void 三字节计数外推形态_E2_也认_兜住下一档加宽()
    {
        // 尚无实抓样本：只是把「宽度+1 ⇒ 标签-1」的规律先兜住（计数 ≥65536 才用得上）。
        // 真碰上了（日志里 E104E280E2 开头），看值确认形态是 3B 大端再补字节级钉子。
        var qry3B = Convert.FromHexString("E104E280E20100E1E101E1CDE30B07E2C9");
        Assert.True(CtpOrderWireCodec.IsQueryOrderRequest(qry3B));
    }
}
