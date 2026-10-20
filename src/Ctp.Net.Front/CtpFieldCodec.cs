using System.Text;

namespace Ctp.Net.Front;

/// <summary>
/// CTP FTDc payload 字段编解码器（从 2026-09-29 抓包逆向推导）。
/// <para>
/// 编码规则（tag-based，非固定长度——区别于 C 结构体的 char[N] 声明）：
/// <list type="bullet">
/// <item><c>E1 xx</c> — 字段标记（xx = 字段编号或类型指示）</item>
/// <item><c>E7 [ASCII...]</c> — 字符串值（到下一标记字节止）</item>
/// <item><c>E4 xx / E4 [ASCII...]</c> — 整数/枚举值，或字符串值（⑤ 的 AppID 即 E4 标记）</item>
/// <item><c>E5 [ASCII...]</c> — 字符串值（⑦ 的 AppID 即 E5 标记）</item>
/// <item><c>E6 [Base64...]</c> — 加密/二进制数据（⑦ 登录密码密文）</item>
/// <item><c>EF</c> — 空字段（连续 EF = 连续空字段）</item>
/// <item><c>FE [ASCII...]</c> — 字符串值（另一种标记——BrokerID 等字段观察到的模式）</item>
/// </list>
/// 高位标记集 E1..EF + FE 统一作为字段终止符（抓包机器 dump 验证）——
/// 若缺 E5/E8..EE，⑦ 的 E6 密文 run 会吞掉紧随的 E5+AppID。
/// </para>
/// </summary>
public static class CtpFieldCodec
{
    /// <summary>标记字节集合——字段值的终止符（E1..EF + FE 全集）。</summary>
    private static readonly HashSet<byte> Markers = new()
    {
        0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8,
        0xE9, 0xEA, 0xEB, 0xEC, 0xED, 0xEE, 0xEF, 0xFE,
    };

    /// <summary>从 payload 中提取所有可读字符串及其偏移。</summary>
    public static List<(int Offset, string Value)> ExtractStrings(ReadOnlySpan<byte> payload)
    {
        var result = new List<(int, string)>();
        var i = 0;
        while (i < payload.Length)
        {
            if (payload[i] is 0xE7 or 0xFE)
            {
                // 字符串标记——读取到下一个标记字节
                var j = i + 1;
                while (j < payload.Length && !Markers.Contains(payload[j])) j++;
                if (j > i + 1)
                {
                    var text = Encoding.ASCII.GetString(payload[(i + 1)..j]);
                    if (text.Length > 0)
                        result.Add((i, text));
                }
                i = j;
            }
            else
            {
                i++;
            }
        }
        return result;
    }

    /// <summary>从 payload 中按位置提取第 N 个字符串（0-based）。</summary>
    public static string? GetString(ReadOnlySpan<byte> payload, int index)
    {
        var strings = ExtractStrings(payload);
        return index < strings.Count ? strings[index].Value : null;
    }

    /// <summary>查找 payload 中包含指定子串的字段。</summary>
    public static int FindString(ReadOnlySpan<byte> payload, string needle)
    {
        var strings = ExtractStrings(payload);
        for (var i = 0; i < strings.Count; i++)
        {
            if (strings[i].Value.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>构造一个 E7 标记的字符串字段。</summary>
    public static byte[] EncodeString(string value)
    {
        var bytes = new List<byte> { 0xE7 };
        bytes.AddRange(Encoding.ASCII.GetBytes(value));
        return bytes.ToArray();
    }

    /// <summary>构造一个 FE 标记的字符串字段。</summary>
    public static byte[] EncodeStringFe(string value)
    {
        var bytes = new List<byte> { 0xFE };
        bytes.AddRange(Encoding.ASCII.GetBytes(value));
        return bytes.ToArray();
    }

    /// <summary>构造一个 E4 标记的整数字段。</summary>
    public static byte[] EncodeInt(int value)
    {
        return [(byte)0xE4, (byte)value];
    }

    /// <summary>构造空字段填充。</summary>
    public static byte[] EncodeEmpty(int count = 1)
    {
        return new byte[count];   // 全 0x00? 不对——应该全 0xEF
    }

    /// <summary>构造空字段填充（EF）。</summary>
    public static byte[] EncodeEmptyFields(int count)
    {
        var bytes = new byte[count];
        Array.Fill(bytes, (byte)0xEF);
        return bytes;
    }

    /// <summary>
    /// 提取首个 marker+可打印 ASCII 串（≥minLen）的字段值——E4/E5 等非 E7/FE 标记的字段用。
    /// minLen 防 op 签名字节误报（如 <c>E2 30 10</c> 中 E2 后 run 长度仅 1）。
    /// Base64 密文不含高位标记字节 ⇒ 对 E5/FE 不会误命中 ⑦ 密文中的内容。
    /// </summary>
    public static string? ExtractField(ReadOnlySpan<byte> payload, byte marker, int minLen = 3)
    {
        var i = 0;
        while (i < payload.Length)
        {
            if (payload[i] == marker)
            {
                var j = i + 1;
                while (j < payload.Length && !Markers.Contains(payload[j]) && 0x20 <= payload[j] && payload[j] < 0x7F) j++;
                if (j - (i + 1) >= minLen)
                    return Encoding.ASCII.GetString(payload[(i + 1)..j]);
                i = j;   // run 过短（op 签名字节）——跳过继续扫
            }
            else
            {
                i++;
            }
        }
        return null;
    }

    /// <summary>提取 E6 标记的加密数据（Base64 → 原始字节）。</summary>
    public static byte[]? ExtractEncrypted(ReadOnlySpan<byte> payload)
    {
        var i = 0;
        while (i < payload.Length)
        {
            if (payload[i] == 0xE6)
            {
                var j = i + 1;
                while (j < payload.Length && !Markers.Contains(payload[j])) j++;
                if (j > i + 1)
                {
                    try
                    {
                        var b64 = Encoding.ASCII.GetString(payload[(i + 1)..j]);
                        return Convert.FromBase64String(b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '='));
                    }
                    catch { /* 非有效 Base64 */ }
                }
                i = j;
            }
            else
            {
                i++;
            }
        }
        return null;
    }
}
