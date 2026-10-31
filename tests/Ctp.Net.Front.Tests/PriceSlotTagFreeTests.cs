using System;
using System.Buffers.Binary;

using Ctp.Net.Front;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// 价格槽「标记字节必须被转义」钉子（2026-10-31，open-issues #33 的根因）。
/// <para>
/// 事故链：MM 在 99.703125 挂单 → <see cref="CtpFrontTemplates.PriceSlot"/> 量化出 <c>40 58 ED</c>
/// → 旧实现的「避 tag 递减」只循环 4 次，停在 <c>40 58 E9</c>（E9 仍在标记字母表 E0..EF 里、且没有转义）
/// → 官方 SDK 按标记切字段，在价格处断句（<c>40 58</c> 右补零 = 假价 96.0）→ 其后的量/状态/合约整体错位
/// → 客户端只看到 <c>BuyOpen 0@96 成0 状态=(空)</c> → 报单永远进不了终态 → MM 撤腿后空转、市场静默。
/// 2026-10-31 13:40 我用本地 probe1 走前置复现，前置出站帧里该价格槽正是 <c>E4 40 58 E9 E8</c>；
/// 同一批 99.796875 → <c>40 58 F3</c>（F3 不在字母表），解得好好的 —— 「相邻一笔好、这一笔全空」。
/// </para>
/// <para>
/// 为什么原来的测试抓不到：<c>RtnOrderWidthShapeTests</c> 只断言帧总宽，而价格槽恒 3 字节（写坏的值
/// 也是 3 字节）；而且它的价位全挑整数，一个都没落进「x.56–x.73」这条泄漏带。
/// </para>
/// </summary>
public sealed class PriceSlotTagFreeTests
{
    private static bool IsTag(byte b) => b is >= 0xE0 and <= 0xEF or 0xFE;

    /// <summary>
    /// 按 SDK 的规则切一段字节：<c>E0</c> 转义其后的字面字节；{E0..EF,FE} 起新字段；其余是字段值。
    /// 返回 (字段数, 值字节总数)。价格槽应当**只有一个字段、三个值字节**——被切断就说明标记泄漏了。
    /// </summary>
    private static (int Fields, int ValueBytes, byte[] Value) Walk(byte[] bytes)
    {
        var fields = 0;
        var valueBytes = 0;
        var value = new System.Collections.Generic.List<byte>();
        var inField = false;
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b == 0xE0 && i + 1 < bytes.Length)          // 转义：下一个字节是纯值
            {
                i++;
                valueBytes++;
                value.Add(bytes[i]);
                continue;
            }
            if (IsTag(b))                                    // 标记：新字段开始
            {
                fields++;
                inField = true;
                continue;
            }
            _ = inField;
            valueBytes++;
            value.Add(b);
        }
        return (fields, valueBytes, value.ToArray());
    }

    /// <summary>槽 → double（值字节右补零，与 SDK 读单价的形态一致）。</summary>
    private static double Decode(byte[] value)
    {
        var full = new byte[8];
        for (var i = 0; i < value.Length && i < 8; i++) full[i] = value[i];
        return BinaryPrimitives.ReadDoubleBigEndian(full);
    }

    /// <summary>ax001 的实际价位带（90–110）按 1/64 走一遍：每个 tick 的槽都必须「单字段、三值字节」。</summary>
    [Fact]
    public void 全价位带每个tick的价格槽都不被标记切断()
    {
        var leaks = new System.Collections.Generic.List<string>();
        for (var ticks = 90 * 64; ticks <= 110 * 64; ticks++)
        {
            var price = ticks / 64.0;
            var (fields, valueBytes, _) = Walk(CtpFrontTemplates.PriceSlot(price));
            if (fields != 0 || valueBytes != 3)
                leaks.Add($"{price} → {Convert.ToHexString(CtpFrontTemplates.PriceSlot(price))}（字段 {fields}、值字节 {valueBytes}）");
        }
        Assert.True(leaks.Count == 0, "价格槽被标记字节切断（SDK 会在此断句）：\n" + string.Join("\n", leaks));
    }

    /// <summary>非二进小数价（真实 tick 0.01 / 0.0001 量级）同样不许被切断。</summary>
    [Fact]
    public void 非二进小数价也不被标记切断()
    {
        for (var cents = 9000; cents <= 11000; cents++)
        {
            var price = cents / 100.0;
            var (fields, valueBytes, _) = Walk(CtpFrontTemplates.PriceSlot(price));
            Assert.True(fields == 0 && valueBytes == 3, $"{price} 的价格槽被标记切断");
        }
    }

    /// <summary><b>口径钉死</b>：修复后价格一字不差地保留（只差量化到 3 字节的 ±1/128），
    /// 旧实现「让位」最多会偏 0.25（16 个 tick）。</summary>
    [Fact]
    public void 泄漏带的价格仍按真实价回显且误差不超过量化步长()
    {
        for (var ticks = 90 * 64; ticks <= 110 * 64; ticks++)
        {
            var price = ticks / 64.0;
            var (_, _, value) = Walk(CtpFrontTemplates.PriceSlot(price));
            var error = Math.Abs(Decode(value) - price);
            Assert.True(error <= 1.0 / 128 + 1e-9, $"{price} 回显 {Decode(value)}，差 {error}");
        }
    }

    /// <summary>指纹钉死：99.703125 是线上打穿 MM 的那个价（旧实现输出 <c>40 58 E9</c>，E9 未转义）。</summary>
    [Theory]
    [InlineData(99.703125, "4058E0ED", 99.703125)]   // ED 是标记字节 ⇒ 必须带 E0 转义，且价格不变
    [InlineData(99.7, "4058E0ED", 99.703125)]   // 3B 槽 ulp = 1/64 ⇒ 量化回显 99.703125（价格不因避 tag 再漂）
    [InlineData(99.796875, "4058F3", 99.796875)]     // 本来就在字母表外 ⇒ 不加字节（帧宽不变）
    [InlineData(99.390625, "4058D9", 99.390625)]
    [InlineData(96.0, "405800", 96.0)]
    public void 泄漏带与相邻价位的槽字节被钉死(double price, string expectedHex, double expectedValue)
    {
        var slot = CtpFrontTemplates.PriceSlot(price);
        Assert.Equal(expectedHex, Convert.ToHexString(slot));
        var (_, _, value) = Walk(slot);
        Assert.Equal(expectedValue, Decode(value), 3);
    }

    /// <summary>宽度纪律：干净价位仍是 3 字节（宽度一变就是 2026-10-13 那场断连风暴）；
    /// 只有含标记字节的价才允许 +1B/每个被转义字节。</summary>
    [Fact]
    public void 干净价位恒三字节_被转义的价才加宽()
    {
        Assert.Equal(3, CtpFrontTemplates.PriceSlot(96.0).Length);
        Assert.Equal(3, CtpFrontTemplates.PriceSlot(99.796875).Length);
        Assert.Equal(4, CtpFrontTemplates.PriceSlot(99.703125).Length);
    }
}
