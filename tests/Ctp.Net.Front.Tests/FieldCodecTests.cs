using System.Text;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// 字段编解码器用例（`doc/open-issues.md` #17）。
/// <para>
/// 标记字母表（E1..EF + FE）既是字段分隔符也是值字节的禁区——`0xE5` 之类的裸值会把字段切断，
/// 这类事故在量槽上真实发生过（见 `DESIGN.md` §3.7）。这里把「提取/查找/转义形态」逐条钉住。
/// </para>
/// </summary>
public sealed class FieldCodecTests
{
    [Fact]
    public void 提取E7与FE字符串_带偏移且跳过空run()
    {
        // E7 "9999" / E7 "" (紧跟标记，空) / FE "botmm "
        byte[] payload = [0xE7, (byte)'9', (byte)'9', (byte)'9', (byte)'9', 0xE7, 0xFE,
                          (byte)'b', (byte)'o', (byte)'t', (byte)'m', (byte)'m', (byte)' ', 0xE1, 0x01];

        var strings = CtpFieldCodec.ExtractStrings(payload);

        Assert.Equal(2, strings.Count);                       // 空 run 不入结果
        Assert.Equal(0, strings[0].Offset);
        Assert.Equal("9999", strings[0].Value);
        Assert.Equal(6, strings[1].Offset);                   // FE 的位置
        Assert.Equal("botmm ", strings[1].Value);
    }

    [Fact]
    public void 按索引取串与按内容查找()
    {
        byte[] payload = [0xE7, (byte)'a', (byte)'x', (byte)'0', (byte)'0', (byte)'1',
                          0xE7, (byte)'9', (byte)'9', (byte)'9', (byte)'9'];

        Assert.Equal("ax001", CtpFieldCodec.GetString(payload, 0));
        Assert.Equal("9999", CtpFieldCodec.GetString(payload, 1));
        Assert.Null(CtpFieldCodec.GetString(payload, 2));
        Assert.Equal(0, CtpFieldCodec.FindString(payload, "AX0"));    // 忽略大小写
        Assert.Equal(-1, CtpFieldCodec.FindString(payload, "zzz"));
    }

    [Fact]
    public void ExtractField_跳过过短的签名字节run()
    {
        // E5 后紧跟 op 签名字节（run 长度 1，可打印）——minLen 默认 3 应跳过它，继续找真正的值。
        // 这是「op 签名误报」的实际形态：E2 30 10 里的 E2 后面只有一个字节。
        byte[] payload = [0xE4, 0x30, 0x24, 0xE5, (byte)'A', 0xE1, 0xE5,
                          (byte)'s', (byte)'i', (byte)'m', (byte)'n', (byte)'o', (byte)'w'];

        Assert.Equal("simnow", CtpFieldCodec.ExtractField(payload, 0xE5));
        Assert.Null(CtpFieldCodec.ExtractField(payload, 0xE5, minLen: 10));
    }

    [Fact]
    public void 提取E6密文_按Base64解码()
    {
        var cipher = new byte[] { 0x01, 0x02, 0x03, 0xFF };
        var b64 = Encoding.ASCII.GetBytes(Convert.ToBase64String(cipher));
        byte[] payload = [0xE6, .. b64, 0xE5, (byte)'x'];

        Assert.Equal(cipher, CtpFieldCodec.ExtractEncrypted(payload));
        Assert.Null(CtpFieldCodec.ExtractEncrypted([0xE7, (byte)'a']));
    }

    [Fact]
    public void 编码族的字节形态()
    {
        Assert.Equal(new byte[] { 0xE7, (byte)'a', (byte)'b' }, CtpFieldCodec.EncodeString("ab"));
        Assert.Equal(new byte[] { 0xFE, (byte)'a', (byte)'b' }, CtpFieldCodec.EncodeStringFe("ab"));
        Assert.Equal(new byte[] { 0xE4, 0x03 }, CtpFieldCodec.EncodeInt(3));
        Assert.Equal(new byte[] { 0xEF, 0xEF, 0xEF }, CtpFieldCodec.EncodeEmptyFields(3));
    }

    [Fact]
    public void 裸插标记字节会切断字段_这是量槽必须转义的根因()
    {
        // 量 11749 = 00 2D E5：低位 0xE5 落在标记字母表里 ⇒ 读端把它当字段边界。
        // 本用例锁住「解码器就是这么切的」，防止有人以为 ExtractStrings 会兜住这种值。
        byte[] payload = [0xE7, (byte)'9', (byte)'9', 0x00, 0x2D, 0xE5, (byte)'X'];

        var strings = CtpFieldCodec.ExtractStrings(payload);
        // 值被**污染**成 "99\0-"（0x00 0x2D 也进了值）再在 0xE5 处截断——不是干净地读到 "99"。
        // 这正是「裸插量槽 = 字段错位」的形态：读端既拿错值、又把后面切碎。
        Assert.Equal("99\0-", strings[0].Value);
        // 0xE5 之后残落的 "X" 没有标记前缀，被整体丢弃 —— 后续字段就此**少一个**（错位）。
        Assert.Single(strings);
    }
}
