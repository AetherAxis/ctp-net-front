using Ctp.Net.Front;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// ⑲ 报单 wire 解析的**形状锚定回归钉**（2026-10-12 服务器三轮丢单实测帧——tag 是动态值，
/// 随用户名长度/字段值宽联动漂移；按固定 tag 解析对非 6 字符用户/低价/不同量形全灭）。
/// 五个帧全部来自服务器前置日志的 body hex（真实 SDK 编码），字段语义已知——防 codec 回退。
/// </summary>
public class OrderCodecShapeTests
{
    // user=e2eusr（6字符）价格 2998 量 5 GFD 买开——M2 形状（orderRef@E8、枚举@EA、量@E8）
    private const string FrameE2Eusr =
        "E101E240E404E101017BE30404E1017739393939E7653265757372EFEFE8303030303030303030303031E1653265757372EA323030E431E440A76CE80533E931E30131E830EFEFEFEFEFEFEFEFE86178303031EFEFEFEFEFEFEFE4";

    // user=mm1（3字符）价格 50 量 1 GFD 买开——orderRef@EB、枚举@ED、量@E9（低价 <1024 场景）
    private const string FrameMm1 =
        "E101E240E404E101017BE30404E1017739393939E76D6D31EFEFEB303030303030303030303031E16D6D31ED323030E431E44049E90133E931E30131E830EFEFEFEFEFEFEFEFE86178303031EFEFEFEFEFEFEFE4";

    // user=botmm（5字符）价格 99.94 量 5 GFD——orderRef@E9、枚举@EB、量@**E3**（2026-10-12 服务器实测
    // 做市商挂单形状——量 tag E3 是第三轮丢单的根因，固定 E8/E7/E9 链救不了）
    private const string FrameBotmmLadder =
        "E101E240E412E101017BE31604E1017739393939E7626F746D6DEFEFE9303030303030303030303231E1626F746D6DEB323030E431E44058FC28F5C28F5CE30533E931E30131E830EFEFEFEFEFEFEFEFE86178303031EFEFEFEFEFEFEFE4";

    // user=pxuser（6字符）价格 **92.03**（非二进小数——double 值内含 0xEB tag 字节，SDK 编码为
    // E0 前插转义：40 57 01 [E0] EB 85 1E B8 52，线上 run 9B > 逻辑 8B）量 1 GFD 买开——
    // 2026-10-13 CtpFrontPriceWidthTests E2E 实测帧（修前 codec 把转义 run 拒于长度帽外、
    // 价格误锚 broker 字段解出 1.35E-301 垃圾价）。
    private const string FrameEscapedPrice =
        "E101E240E40EE101017BE30E04E1017739393939E7707875736572EFEFE8303030303030303030303031E1707875736572EA323030E431E4405701E0EB851EB852E30133E931E30131E830EFEFE7444345EFEFEFEFEFED6D32373031EFEFEFEFEFEFEFE4";

    // user=botmm（5字符）价格 **99.97**（double = 40 58 FE 14 7A E1 47 AE——值内含 0xFE【SDK 不转义，
    // FE 非其字母表】+ 0xE1【E0 前插转义】；对照 99.98 = 40 58 EB B8 … 含 0xEB 亦转义）量 5 GFD 买开——
    // 2026-10-13 双 bot 本地全栈实测帧（修前 IsTag 含 FE ⇒ 价格 run 在 FE 截断 ⇒ 尾 6B 解出
    // 5.11E-210 垃圾价入簿 ⇒ 垃圾卖单穿透买侧 + tick ask 垃圾反馈环）。
    private const string FrameFePrice =
        "E101E240E405E101017BE30804E1017739393939E7626F746D6DEFEFE9303030303030303030303032E1626F746D6DEB323030E431E44058FE147AE0E147AEE30533E931E30131E830EFEFEFEFEFEFEFEFE86178303031EFEFEFEFEFEFEFE4";

    [Theory]
    [InlineData(FrameE2Eusr,   "000000000001", "ax001", '0', '0', '2', 2998.0, 5, '3')]
    [InlineData(FrameMm1,      "000000000001", "ax001", '0', '0', '2', 50.0,   1, '3')]
    [InlineData(FrameBotmmLadder, "000000000021", "ax001", '0', '0', '2', 99.94, 5, '3')]
    [InlineData(FrameEscapedPrice, "000000000001", "m2701", '0', '0', '2', 92.03, 1, '3')]
    [InlineData(FrameFePrice,  "000000000002", "ax001", '0', '0', '2', 99.97, 5, '3')]
    public void 实测帧_形状锚定解析_各字段精确还原(string hex, string refId, string inst,
        char dir, char off, char pt, double price, int vol, char tc)
    {
        var o = CtpOrderWireCodec.ParseOrderInsert(Convert.FromHexString(hex));
        Assert.NotNull(o);
        Assert.Equal(refId, o.OrderRef);
        Assert.Equal(inst, o.InstrumentId);
        Assert.Equal(dir, o.Direction);
        Assert.Equal(off, o.OffsetFlag);
        Assert.Equal(pt, o.PriceType);
        Assert.Equal(price, o.LimitPrice, 2);
        Assert.Equal(vol, o.Volume);
        Assert.Equal(tc, (char)o.TimeCondition);
    }

    // —— ㉑ 撤单 wire 解析的形状锚定回归钉（2026-10-16 双 bot 换防全瘫根因）——
    // OrderRef 前的 tag 随 user 长度/SessionID 形态漂移（与 ⑲ 量/orderRef tag 漂移同族）：
    // 旧版 ParseOrderAction 硬编码 0xEB 只在 E2E 形状（6 字符 user + 1 字节 SessionId）下成立，
    // 其余形状失配 ⇒ 前置静默无应答 ⇒ 客户端 15s 撤单超时、交易所侧撤单未发。两帧均为实测字节。

    // E2E 形状：user=m2user(6B)、sessionId=1（1 字节 E3 01）、exchangeId="DCE"——orderRef@EB（撤单成功形状）
    private const string ActionFrameE2Eusr =
        "E101E24006E306E101013BE3060404013739393939E76D3275736572EB303030303030303030303032E801E301444345EFEC30EC6D3275736572EFEFEFEFEFEFEFEFEFEFEFEFEFEE";

    // console 形状：user=cancelp1(8B)、sessionId=766475010（4B 大端 2DAF7B02）、exchangeId 空——orderRef@E9
    // （2026-10-16 本地全栈实测：撤活单 15s 超时、前置日志「撤单解析失败」——修前复现帧）
    private const string ActionFrameCancelp1 =
        "E101E24006E305E101013BE3050404013739393939E763616E63656C7031E9303030303030303030303035E8012DAF7B02EFEF30EC63616E63656C7031EFEFEFEFEFEFEFEFEFEFEFEFEFEC";

    [Theory]
    [InlineData(ActionFrameE2Eusr,   "000000000002", 6)]
    [InlineData(ActionFrameCancelp1, "000000000005", 5)]
    public void 撤单实测帧_形状锚定解析_tag漂移形状全覆盖(string hex, string refId, byte reqId)
    {
        var a = CtpOrderWireCodec.ParseOrderAction(Convert.FromHexString(hex));
        Assert.NotNull(a);
        Assert.Equal(refId, a.OrderRef);
        Assert.Equal(reqId, a.ReqId);
    }
}
