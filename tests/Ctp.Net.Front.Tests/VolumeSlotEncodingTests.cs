using Ctp.Net.Front;
using Xunit;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// 持仓量槽的字节编码（2026-10-30 探针定案）。
/// <para>
/// 探针做法：起真前置 + 用 <c>QryPositionRecordHook</c> 把记录里的量槽字节逐组替换，再让**官方 SDK** 重登回读
/// ——SDK 读得出、且**同一行的合约/交易所字段还在**才算编码合法。实测矩阵：
/// </para>
/// <list type="table">
/// <item><term>总仓槽 payload@53</term><description>`E7`+1B 读 0..255；`E6`+2B 大端读 256..65535（65535 实证）；
/// `E6` + 4B/8B 整条丢行 ⇒ 可加宽，但只有 2 字节这一档</description></item>
/// <item><term>今仓槽 payload@149</term><description>`E1`+1B 正常；只要后面跟 2B/3B/4B/5B/8B 值，
/// **E1..EF、FE 全 16 个 tag 都试过**，SDK 一律解析失步（总仓读得出 300、合约变空）⇒ <b>不可加宽，上限 255</b></description></item>
/// <item><term>裸标记字节</term><description>值字节落进 {E0..EF,FE} 必须用 `E0` 转义：`E7 E0` 丢行，`E7 E0 E0` 读回 224</description></item>
/// </list>
/// <para>
/// 这组断言钉「生成侧输出什么字节」，可读性由 <see cref="FrontPositionFieldsTests"/> 用官方 SDK 端到端兜。
/// </para>
/// </summary>
public sealed class VolumeSlotEncodingTests
{
    private const byte TotalTag = 0xE7;   // 模板 payload@53 = 总仓 tag
    private const byte TodayTag = 0xE1;   // 模板 payload@149 = 今仓 tag

    private static byte[] Total(int value, Action<string>? warn = null)
        => CtpFrontTemplates.VolumeSlot(value, TotalTag, widen: true, 53, "总仓", warn);

    private static byte[] Today(int value, Action<string>? warn = null)
        => CtpFrontTemplates.VolumeSlot(value, TodayTag, widen: false, 149, "今仓", warn);

    [Theory]
    [InlineData(0, new byte[] { 0xE7, 0x00 })]
    [InlineData(1, new byte[] { 0xE7, 0x01 })]
    [InlineData(255, new byte[] { 0xE7, 0xFF })]
    [InlineData(256, new byte[] { 0xE6, 0x01, 0x00 })]
    [InlineData(300, new byte[] { 0xE6, 0x01, 0x2C })]
    [InlineData(5000, new byte[] { 0xE6, 0x13, 0x88 })]
    [InlineData(65535, new byte[] { 0xE6, 0xFF, 0xFF })]
    public void 总仓按宽度选tag_大端写入(int value, byte[] expected) => Assert.Equal(expected, Total(value));

    [Fact]
    public void 不超宽时沿用模板原tag_每个槽的tag不同()
    {
        // 总仓 tag = E7、今仓 tag = E1（2026-10-29 逐字节 patch 定位）。tag 必须从模板原样带上：
        // 只在值字节写、另加一个 E7 前缀，线上就成了 `E7 E7 xx`，客户端读到 0（2026-10-30 踩过）。
        Assert.Equal(new byte[] { 0xE7, 0x05 }, Total(5));
        Assert.Equal(new byte[] { 0xE1, 0x05 }, Today(5));
    }

    [Theory]
    [InlineData(224)]   // E0 —— 转义前缀本身
    [InlineData(226)]   // E2
    [InlineData(239)]   // EF —— 标记区上界
    [InlineData(254)]   // FE
    public void 单字节值撞标记区时转义(int value)
    {
        // 旧实现直接写裸字节 ⇒ 这些持仓值整条丢行（SDK：Sequence contains no matching element）
        Assert.Equal(new byte[] { 0xE7, 0xE0, (byte)value }, Total(value));
        Assert.Equal(new byte[] { 0xE1, 0xE0, (byte)value }, Today(value));
    }

    [Fact]
    public void 两字节值的高字节撞标记区也转义()
    {
        // 480 = 01 E0 ⇒ 低字节 E0 要转义
        Assert.Equal(new byte[] { 0xE6, 0x01, 0xE0, 0xE0 }, Total(480));
    }

    [Fact]
    public void 今仓超255按255上报并告警()
    {
        // 今仓槽不可加宽（见类注释的 16 个 tag 实测），协议上限就是 255。
        // 告警是必须的：botmm 真实持仓 487 / botnoise -457，今仓被削顶必须能从日志看出来。
        var warned = new List<string>();
        Assert.Equal(new byte[] { 0xE1, 0xFF }, Today(487, warned.Add));
        Assert.Contains("487", Assert.Single(warned));
        Assert.Contains("255", warned[0]);
    }

    [Fact]
    public void 总仓超出两字节上限按65535上报并告警()
    {
        var warned = new List<string>();
        Assert.Equal(new byte[] { 0xE6, 0xFF, 0xFF }, Total(70_000, warned.Add));
        Assert.Contains("70000", Assert.Single(warned));
    }

    [Fact]
    public void 负持仓按0上报并告警()
    {
        // 槽是无符号量，负值意味着方向搞错了 ⇒ 报 0 但必须留痕（排查时会看日志）
        var warned = new List<string>();
        Assert.Equal(new byte[] { 0xE7, 0x00 }, Total(-457, warned.Add));
        Assert.Contains("-457", Assert.Single(warned));
    }
}
