using System.Text;
using Ctp.Net.Front;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// 查交易所记录帧的形状钉子（2026-10-24 真前置 255B/6 条实抓逆向的三条硬约束，见 DESIGN P1#6d）。
/// <para>
/// 这里锁的是**离线可判**的部分：记录按 <c>E1</c> 切出的字段序、ID+简称槽 = 7 字节、名称固定落在第 13 字节、
/// 记录总长只能是 39 或 41。真 SDK 是否真解得出由 <see cref="CtpFrontE2eExchangeQueryTests"/> 兜底——
/// 两类测试缺一不可：形状对但长度错时 SDK 会静默丢记录，只看字节是看不出来的。
/// </para>
/// </summary>
public sealed class QryExchangeShapeTests
{
    private static readonly string[] RealExchanges = ["CFFEX", "SHFE", "DCE", "CZCE", "INE", "GFEX"];

    static QryExchangeShapeTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static List<byte[]> Records(byte[] frame)
    {
        // 记录起始 = E1 01 E1 47；头部（E104E2802B + E3 流水 + E1 条数 + 01C2 + E3 reqId）在第一条之前
        var start = IndexOf(frame, [0xE1, 0x01, 0xE1, 0x47], 0);
        Assert.True(start > 0, "帧里找不到记录起始标记 E1 01 E1 47");
        var recs = new List<byte[]>();
        while (start >= 0)
        {
            var next = IndexOf(frame, [0xE1, 0x01, 0xE1, 0x47], start + 4);
            var end = next < 0 ? frame.Length : next;
            recs.Add(frame[start..end]);
            start = next;
        }
        return recs;
    }

    private static int IndexOf(byte[] hay, byte[] needle, int from)
    {
        for (var i = from; i <= hay.Length - needle.Length; i++)
        {
            var ok = true;
            for (var k = 0; k < needle.Length; k++)
                if (hay[i + k] != needle[k]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    /// <summary>去掉 E0 转义（值内 {E0..EF,FE} 前插了 E0）——宽度与偏移都按未转义字节判。</summary>
    private static byte[] Unescape(byte[] raw)
    {
        var r = new List<byte>(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == 0xE0 && i + 1 < raw.Length && raw[i + 1] is (>= 0xE0 and <= 0xEF) or 0xFE) continue;
            r.Add(raw[i]);
        }
        return [.. r];
    }

    /// <summary>按 <c>E1</c> 切字段（先解转义）；索引 1 = (0x47 + ID)、2 = 简称槽、3 = 名称、4 = Property。</summary>
    private static List<byte[]> Fields(byte[] record)
    {
        var rec = Unescape(record);
        Assert.Equal(0xE1, rec[0]);
        var fields = new List<byte[]>();
        var cur = new List<byte>();
        for (var i = 1; i < rec.Length; i++)   // 跳过首字节 E1，按 E1 分隔收集
        {
            if (rec[i] == 0xE1) { fields.Add([.. cur]); cur.Clear(); }
            else cur.Add(rec[i]);
        }
        fields.Add([.. cur]);
        return fields;
    }

    [Fact]
    public void 六家真交易所_ID加简称恒为7字节_名称固定在第13字节_记录39或41()
    {
        var rows = RealExchanges
            .Select(id => { var (s, f) = CtpFrontTemplates.ExchangeNameOf(id); return (ExchangeId: id, ShortName: s, FullName: f, Property: (byte)'1'); })
            .ToList();
        var frame = CtpFrontTemplates.BuildQryExchangeRsp(1, 7, rows);
        var recs = Records(frame);
        Assert.Equal(6, recs.Count);

        for (var i = 0; i < recs.Count; i++)
        {
            var rec = recs[i];
            Assert.Contains(rec.Length, new[] { 39, 41 });          // 约束②：长度只有 39/41
            var f = Fields(rec);
            var id = Encoding.ASCII.GetString(f[1][1..]);            // f[1] = 0x47 + ExchangeID
            Assert.Equal(RealExchanges[i], id);
            Assert.Equal(7, f[1].Length - 1 + f[2].Length);          // 约束①：ID + 简称槽 = 7
            Assert.Equal(12, 3 + f[1].Length + 1 + f[2].Length);    // 名称字段的 E1 恒在 @12（值在 @13）
            Assert.Equal(0xE1, Unescape(rec)[12]);
            var (_, expectedName) = CtpFrontTemplates.ExchangeNameOf(id);
            Assert.Equal(expectedName, Encoding.GetEncoding("GB18030").GetString(f[3]));
            Assert.Equal((byte)'1', f[4][0]);                        // 约束③：Property 在名称后首个字段
        }
    }

    [Fact]
    public void 合成交易所_名称14字节_记录39字节且名称可读()
    {
        var (shortName, fullName) = CtpFrontTemplates.ExchangeNameOf("AETHER");
        Assert.Equal(14, Encoding.GetEncoding("GB18030").GetByteCount(fullName));  // 名称长度决定记录总长

        var frame = CtpFrontTemplates.BuildQryExchangeRsp(2, 9, [("AETHER", shortName, fullName, (byte)'1')]);
        var rec = Assert.Single(Records(frame));
        Assert.Equal(39, rec.Length);
        var f = Fields(rec);
        Assert.Equal("AETHER", Encoding.ASCII.GetString(f[1][1..]));
        Assert.Equal(7, f[1].Length - 1 + f[2].Length);
        Assert.Equal(fullName, Encoding.GetEncoding("GB18030").GetString(f[3]));
    }

    [Fact]
    public void 条数槽与可用行数一致_超长ID不出该条()
    {
        var rows = RealExchanges
            .Select(id => { var (s, f) = CtpFrontTemplates.ExchangeNameOf(id); return (ExchangeId: id, ShortName: s, FullName: f, Property: (byte)'1'); })
            .Append(("TOOLONGID", "长", "长名交易所名称", (byte)'1'))   // 9 字符 ⇒ 凑不出 7 字节槽 ⇒ 丢弃
            .ToList();
        var frame = CtpFrontTemplates.BuildQryExchangeRsp(3, 11, rows);

        Assert.Equal(6, Records(frame).Count);
        // 头部 = op(5) + E3 流水(2) + E1 <条数> ⇒ 条数槽就在 op 之后第 5 字节
        var countAt = IndexOf(frame, [0xE1, 0x04, 0xE2, 0x80, 0x2B], 0) + 5 + 2 + 1;
        Assert.Equal(6, frame[countAt]);
        Assert.DoesNotContain("TOOLONGID", Encoding.Latin1.GetString(frame));
    }
}
