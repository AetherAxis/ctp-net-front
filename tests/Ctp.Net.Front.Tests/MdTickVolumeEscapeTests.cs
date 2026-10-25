namespace Ctp.Net.Front.Tests;

/// <summary>
/// 量槽转义钉子（2026-10-25 双 bot 长跑事故根因回归）：
/// 成交量 11749 = <c>00 2D E5</c>，低位 0xE5 落在标记字母表 {E0..EF,FE} 里。旧版裸插字节 ⇒
/// 客户端按标记切字段错位 ⇒ native 侧进入**同一笔 tick 的无限重投**（bot 日志 30 秒 160MB、
/// 行情时间戳冻结）。真前置按 E0 转义（与文本/金额槽同一字母表），量槽必须照做——不能像价格槽
/// 那样靠「避 tag 递减 1 ulp」（那会谎报成交量）。
/// </summary>
public class MdTickVolumeEscapeTests
{
    [Fact]
    public void 量槽低位命中标记字节时插E0()
    {
        // 11749 = 0x002DE5：低位 0xE5 ∈ {E0..EF} ⇒ 前插 E0（宽度 3B → 4B）
        Assert.Equal("002DE0E5", Convert.ToHexString(CtpFrontTemplates.FixedInt3(11749)));
        // 2B 槽同规则：225 = 0x00E1
        Assert.Equal("00E0E1", Convert.ToHexString(CtpFrontTemplates.FixedInt2(225)));
    }

    [Fact]
    public void 无标记字节的量槽保持定宽()
    {
        // 抓包原值：成交量 802254、买一档量 1105、卖一档量 808——一个字节都不许变
        Assert.Equal("0C3DCE", Convert.ToHexString(CtpFrontTemplates.FixedInt3(802254)));
        Assert.Equal("0451", Convert.ToHexString(CtpFrontTemplates.FixedInt2(1105)));
        Assert.Equal("0328", Convert.ToHexString(CtpFrontTemplates.FixedInt2(808)));
    }

    [Fact]
    public void E0自身也转义()
    {
        // 224 = 0x00E0：E0 是转义引导符，数据里的 E0 同样要前插一个 E0
        Assert.Equal("00E0E0", Convert.ToHexString(CtpFrontTemplates.FixedInt2(224)));
    }

    [Fact]
    public void 载荷随转义膨胀_帧头由会话按真实长度写()
    {
        var payload = CtpFrontTemplates.BuildMdTick("m2701", "20260923", "10:00:00",
            lastPrice: 3001, bidPrice: 2999, askPrice: 3001, volume: 11749,
            bidVolume: 225, askVolume: 808, openInterest: 1);

        // 转义只在量槽有命中时膨胀：volume 低字节 E5 + bidVolume 低字节 E1 ⇒ 各多 1 字节
        // （4B 帧头由 CtpFrontMdSession.BuildFrame 按真实载荷长度写，客户端按长度读帧）
        Assert.Equal(109 + 2, payload.Length);
    }
}
