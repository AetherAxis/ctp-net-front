namespace Ctp.Net.Front.Tests;

/// <summary>
/// 分帧器的纯内存用例（`DESIGN.md` §6 #4 / `doc/open-issues.md` #17）。
/// <para>
/// 这些边界以前只被「整条链路的 E2E」间接覆盖——一旦破了，表现出来是会话静默错位/超时，
/// 排查要从前置日志反推。这里把「一段 TCP 里多帧、半帧跨段、心跳夹在中间、未知帧头、
/// 握手帧、垃圾兜底」逐条钉死，回归时不依赖网络。
/// </para>
/// </summary>
public sealed class FrameParserTests
{
    /// <summary>[02 00][len 2B 大端][payload]</summary>
    private static byte[] Std(params byte[] payload)
        => [0x02, 0x00, (byte)(payload.Length >> 8), (byte)(payload.Length & 0xFF), .. payload];

    private static readonly byte[] Route = [0x01, 0x03, 0x33, 0x4C];

    [Fact]
    public void 单帧_解析出路由与消息体()
    {
        var bytes = Std([.. Route, 0xE1, 0x01, 0xE2, 0x40, 0x14]);
        var frames = new CtpFrameParser().Feed(bytes);

        var f = Assert.Single(frames);
        Assert.False(f.IsHandshake);
        Assert.False(f.IsHeartbeat);
        Assert.False(f.IsGarbage);
        Assert.Equal(bytes.Length, f.Raw.Length);
        Assert.Equal(9, f.Payload.Length);          // payload 不含 4B 头（= 4B 路由 + 5B 字段）
        Assert.Equal(Route, f.Route.ToArray());
        Assert.Equal(new byte[] { 0xE1, 0x01, 0xE2, 0x40, 0x14 }, f.Body.ToArray());
    }

    [Fact]
    public void 一段TCP含两帧_一次全部产出()
    {
        var a = Std([.. Route, 0xE1, 0x01]);
        var b = Std([.. Route, 0xE2, 0x40, 0x13]);
        var frames = new CtpFrameParser().Feed([.. a, .. b]);

        Assert.Equal(2, frames.Count);
        Assert.Equal(new byte[] { 0xE1, 0x01 }, frames[0].Body.ToArray());
        Assert.Equal(new byte[] { 0xE2, 0x40, 0x13 }, frames[1].Body.ToArray());
    }

    [Fact]
    public void 半帧跨段_凑齐后才产出()
    {
        var parser = new CtpFrameParser();
        var full = Std([.. Route, 0xE1, 0x02, 0x03]);

        Assert.Empty(parser.Feed(full.AsSpan(0, 6)));          // 头 + 半个 payload
        var frames = parser.Feed(full.AsSpan(6));
        Assert.Single(frames);
        Assert.Equal(full, frames[0].Raw);
    }

    [Fact]
    public void 心跳夹在两帧之间_被剥离且不吃掉相邻帧()
    {
        // [帧A][心跳][帧B] 同批到达——只入口剥一次会漏掉中间那个（历史坑，见 CtpFrameParser 注释）
        var a = Std([.. Route, 0xE1, 0x01]);
        var b = Std([.. Route, 0xE2, 0x40, 0x13]);
        var frames = new CtpFrameParser().Feed([.. a, .. CtpFrame.Heartbeat, .. b]);

        Assert.Equal(3, frames.Count);
        Assert.False(frames[0].IsHeartbeat);
        Assert.True(frames[1].IsHeartbeat);
        Assert.False(frames[2].IsHeartbeat);
        Assert.Equal(new byte[] { 0xE2, 0x40, 0x13 }, frames[2].Body.ToArray());
    }

    [Fact]
    public void 心跳跨TCP段_凑齐后仍被剥离()
    {
        // 注释里写着「跨段概率趋零、不覆盖」——本用例把它钉住：被切开也要能认出来，
        // 否则残余字节会被当成未知帧头逐字节跳过，进而静默错位。
        var parser = new CtpFrameParser();
        Assert.Empty(parser.Feed(CtpFrame.Heartbeat.AsSpan(0, 3)));
        var frames = parser.Feed(CtpFrame.Heartbeat.AsSpan(3));
        Assert.Single(frames);
        Assert.True(frames[0].IsHeartbeat);
    }

    [Fact]
    public void 握手帧_整帧作为payload且标记为handshake()
    {
        // 00 06 开头，内嵌一个 0200 帧：边界 = 内嵌帧起点 + 4 + 内嵌长度
        byte[] handshake = [0x00, 0x06, 0x00, 0x00, 0x02, 0x00, 0x00, 0x05, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE];
        var frames = new CtpFrameParser().Feed(handshake);

        var f = Assert.Single(frames);
        Assert.True(f.IsHandshake);
        Assert.Equal(handshake.Length, f.Raw.Length);
        Assert.Equal(handshake, f.Payload);      // 握手帧整个作为 payload
    }

    [Fact]
    public void 未知帧头逐字节跳过_不吞掉紧随其后的真帧()
    {
        var good = Std([.. Route, 0xE1, 0x01]);
        var frames = new CtpFrameParser().Feed([0xAA, 0xBB, .. good]);

        var f = Assert.Single(frames);
        Assert.Equal(good, f.Raw);
    }

    [Fact]
    public void 超长垃圾_按64字节兜底丢弃且不冒充握手帧()
    {
        // 00 06 开头但 64B 内找不到 0200 边界 ⇒ IsGarbage（旧版会标成 IsHandshake，
        // 让会话在中间重发挑战、状态机重置）
        var junk = new byte[70];
        junk[0] = 0x00; junk[1] = 0x06;
        for (var i = 2; i < junk.Length; i++) junk[i] = 0x11;

        var f = Assert.Single(new CtpFrameParser().Feed(junk));
        Assert.True(f.IsGarbage);
        Assert.False(f.IsHandshake);
        Assert.Equal(64, f.Raw.Length);
    }
}
