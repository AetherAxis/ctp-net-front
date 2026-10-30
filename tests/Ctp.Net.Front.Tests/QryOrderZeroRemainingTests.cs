using System.Text;
using Ctp.Net.Front;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// 「剩余未成交量为 0」的编码钉子（2026-10-30 probe1 真单实测）。
/// <para>
/// 现象：查报单行帧的这一格写 <c>0x00</c> 时，SDK 仍然派发该行，但**整行字段全空**
/// （<c>OnRspQryOrder</c> 的 <c>pOrder.InstrumentID</c> 为空串、量为 0）。客户端侧看到的就是
/// 一行空白委托，且 <c>isLast=True</c>，不会有任何错误码可查。
/// </para>
/// <para>
/// 根因：这套去前导零编码里 **0 就是零字节**（<see cref="CtpOrderWireCodec.ParseMinimalInt"/>
/// 解空 span 即 0，请求侧抓包的逆运算同款）。写一个实际为零的字节等于多塞了一个值字节，
/// 其后整条记录错位，SDK 丢弃整行。
/// </para>
/// <para>
/// 对照量级：修复前 botmm 的 200 行查询里 189 行解空（恰是全部剩余量为 0 的历史单），
/// 修复后 186 行恢复（剩 4 行为 SDK 多行派发层面的残余，与本格无关）。
/// </para>
/// </summary>
public sealed class QryOrderZeroRemainingTests
{
    static QryOrderZeroRemainingTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static byte[] Row(int volumeTotal) =>
        CtpFrontTemplates.BuildQryOrderRsp(
            instance: 7, reqId: 9, brokerId: "9999", investorId: "botmm", instrumentId: "ax001",
            exchangeId: "AET", orderRef: "000000000001", priceType: '1', direction: '0', offsetFlag: '0',
            limitPrice: 99.33, volume: 5, volumeTotal: volumeTotal, timeCondition: '3', orderStatus: '3',
            statusMsg: "已报入", tradingDay: "20261030", insertTime: "15:28:34");

    [Fact]
    public void 零值编码为零字节_非零值保持最小大端()
    {
        Assert.Empty(CtpFrontTemplates.ZeroableInt(0));
        Assert.Equal([0x05], CtpFrontTemplates.ZeroableInt(5));
        Assert.Equal([0x01, 0x2C], CtpFrontTemplates.ZeroableInt(300));
        // 旧写法（MinimalInt）对 0 会吐一个 0x00——正是 SDK 解不出整行的成因
        Assert.Equal([0x00], CtpFrontTemplates.MinimalInt(0));
    }

    [Fact]
    public void 剩余量为零的行比非零行正好短一字节()
    {
        var zero = Row(0);
        var one = Row(1);
        Assert.Equal(one.Length - 1, zero.Length);
    }

    [Fact]
    public void 剩余量为零的行在记录区内不含裸零字节()
    {
        var frame = Row(0);
        // 前 8B 是 4B 长度前缀（0200011F 之类）+ 4B 路由，长度前缀里本来就可能有 0x00
        var body = frame.AsSpan(8);
        for (var i = 0; i < body.Length; i++)
            Assert.True(body[i] != 0x00, $"记录区第 {i} 字节是裸 0x00——SDK 会丢弃整行");
    }
}
