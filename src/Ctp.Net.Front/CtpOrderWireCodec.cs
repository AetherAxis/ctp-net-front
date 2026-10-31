using System.Buffers.Binary;
using System.Text;

namespace Ctp.Net.Front;

/// <summary>
/// ⑲ 报单 / ㉑ 撤单请求的 wire 解析 + 数值解码（M2）。
/// 按 run 形状分类而非固定偏移（SDK 构造的请求形状稳定；oracle = 20260929-031656 + 20261001-024350/024121 抓包，
/// 形状规则经 FrontGen 同款查找器验证）。数值解码是模板 <c>TrimmedDouble/MinimalInt</c> 的逆操作。
/// </summary>
internal static class CtpOrderWireCodec
{
    internal sealed record ParsedOrderInsert(
        byte ReqId, string OrderRef, string InstrumentId,
        char PriceType, char Direction, char OffsetFlag,
        double LimitPrice, int Volume, char TimeCondition);

    internal sealed record ParsedOrderAction(byte ReqId, string OrderRef);

    /// <summary>tag 判定 = E1..EF（**不含 FE**——2026-10-13 双 bot 本地实测：SDK 对 99.984375 价
    /// （double 含 0xFE + 0xE1）只转义 E1（E0 前插）、**FE 原样保留** ⇒ FE 不在其转义字母表 ⇒
    /// 不是 wire tag；旧版把 FE 当 tag ⇒ 价格 run 在 FE 处截断 ⇒ 尾部 6B 被当价格解析成
    /// 5.11E-210 垃圾价入簿（垃圾卖单穿透买侧 + tick ask 垃圾反馈环）。E0 = 转义前缀亦非 tag。</summary>
    private static bool IsTag(byte b) => b is >= 0xE1 and <= 0xEF;
    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    /// <summary>报单/撤单共用的路由头（<c>E1 01 E2 40</c>）。</summary>
    private static ReadOnlySpan<byte> Route40 => [0xE1, 0x01, 0xE2, 0x40];

    /// <summary>报单请求独有的 reqId 标记的**尾三字节** <c>01 01 7B</c>（撤单是 <c>01 01 3B</c>）。
    /// <para>不用完整的 <c>E1 01 01 7B</c>：实例号是 0x0100 的整数倍时（256/512/768…，实测
    /// <c>E3 01 E2</c>、<c>E3 02 E2</c>）SDK 会把这格前面的 <c>E1</c> 一并吃掉，帧变成
    /// <c>E1 01 E2 40 E3 01 E2 01 01 7B …</c>——按 4 字节锚点会漏。</para></summary>
    private static ReadOnlySpan<byte> InsertMarkerTail => [0x01, 0x01, 0x7B];

    /// <summary>撤单请求的 op 字节：<c>E1 01 E2 40 <b>06</b> E3 &lt;实例号&gt;</c>。</summary>
    internal const byte OrderActionOp = 0x06;

    /// <summary>报单的 op 字节两副形态：单字节实例 <c>E4</c>、双字节实例 <c>E3</c>。</summary>
    internal const byte OrderInsertOp1B = 0xE4;
    internal const byte OrderInsertOp2B = 0xE3;

    /// <summary>三字节实例的 op 标签——<b>外推值，尚无实抓样本</b>：变宽规律是「宽度 +1 ⇒ 标签字母减 1」
    /// （1B=`E4`、2B=`E3`，2026-10-30 报单实测；应答侧同款 `E3`/`E2` 位移）。计数 ≥65536 时才会用到，
    /// 按当前速率（报单/查询各约 0.2-0.3 次/秒）两三天内就会碰到——先认下来，碰上了看日志确认形态。</summary>
    internal const byte OpTag3B = 0xE2;

    /// <summary>查报单请求的路由头 <c>E1 04 E2 80</c>（应答 op=01）。同族其它查询类型的第 5 字节
    /// 是固定类型码——02 成交 / 04 持仓 / 06 资金 / 2A 交易所 / 2E 合约 / 3C 结算单——全部 &lt;0xE0，
    /// 与 op 标签 `E4`/`E3`/`E2` 不撞。</summary>
    private static ReadOnlySpan<byte> RouteQryOrder => [0xE1, 0x04, 0xE2, 0x80];

    /// <summary>查报单请求里的 reqId 家族标记的**尾 4 字节**（`01 E1 CD E3`，完整形是
    /// <c>E1 01 E1 CD E3 &lt;reqId&gt;</c>）。不用带前导 E1 的 5 字节：实例号恰为 0x0100 的整数倍时
    /// （256/512/…，与⑲报单 <see cref="InsertMarkerTail"/> 同一个 SDK 行为）SDK 会把标记前的
    /// <c>E1</c> 一并吃掉，帧变成 <c>E3 01 E2 | 01 E1 CD E3 …</c>——按 5 字节锚点会漏，
    /// 而 256 这一帧正是本用例的必中边界（2026-10-31 进程内复现，见 QueryOrderHeaderWidthTests）。</summary>
    private static ReadOnlySpan<byte> QryOrderReqIdNeedle => [0x01, 0xE1, 0xCD, 0xE3];

    /// <summary>
    /// 是不是一张<b>报单</b>请求帧——判据**不依赖 op 字段的字节宽度**。
    /// <para>
    /// 2026-10-30 实测（probe1 单会话连发 300 笔报单，逐帧对账前置日志与交易所库）：路由头
    /// <c>E1 01 E2 40</c> 后面的 op/实例字段是<b>变宽</b>的。买/撤共用一条逐请求自增的计数器，
    /// 买落偶数、撤落奇数；实例号 ≤254 时写成 <c>E4 &lt;1B&gt;</c>（抓包里 <c>E4 04/06/…/FE</c>），
    /// 一旦 ≥255 改写成 <c>E3 &lt;2B&gt;</c>（实测 <c>E3 01 02</c> = 258、<c>E3 01 8E</c> = 398）。
    /// 旧判据把签名钉成 5 字节 <c>E101E240E4</c>，越界之后每一张报单都掉进「未知业务帧」被静默丢弃：
    /// 会话第 127 单起不再进交易所（两场实测分别停在 128/126 笔），随后该单的撤单回 90008
    /// 「报单不存在」——客户端看到的就是「跑一阵就不干活了，重连又好了」。
    /// </para>
    /// </summary>
    internal static bool IsOrderInsert(ReadOnlySpan<byte> body)
        => body.Length > 8
           && body[..4].SequenceEqual(Route40)
           && body[4] is OrderInsertOp1B or OrderInsertOp2B or OpTag3B   // 撤单/结算确认的 op 不在这一族
           && body.IndexOf(InsertMarkerTail) >= 0;

    /// <summary>
    /// 是不是<b>查报单</b>（<c>ReqQryOrder</c>）请求帧——与⑲报单同一套变宽规则，判据**不依赖 op 宽度**。
    /// <para>
    /// 2026-10-31 浸泡实测：SDK 的链路计数 ≤254 时 op 写 <c>E4 &lt;1B&gt;</c>，≥255 改写
    /// <c>E3 &lt;2B&gt;</c>（前置日志实抓 <c>E104E280E30340…</c>=832、<c>E104E280E32645…</c>=9797）。
    /// 旧判据把签名钉成 5 字节 <c>E104E280E4</c> ⇒ 越界后每一条查报单都掉进「未知业务帧」**永不应答**：
    /// SDK 查询管线堵死（30s 白等）⇒ 后续持仓查询 rc=-2 ⇒ 交易腿周期 4097 重连；两个 bot 夜跑十小时
    /// 全在「持仓吃缓存快照」的盲飞状态（open-issues #26）。
    /// </para>
    /// <para>
    /// 这就是 <c>CtpTrader.QueryRequestCount</c> 注释里 2026-10-15 记下的那个「第 256 个同族请求起换编码」
    /// 陷阱——当年的规避手段（宿主逼近 256 主动重连）从未实现，正确修法是在这里把加宽形态认下来。
    /// </para>
    /// </summary>
    internal static bool IsQueryOrderRequest(ReadOnlySpan<byte> body)
        => body.Length > 8
           && body[..4].SequenceEqual(RouteQryOrder)
           && body[4] is OrderInsertOp1B or OrderInsertOp2B or OpTag3B
           // reqId 家族标记——把其它 80 族帧挡在外面（缺它时也无从回 reqId，判未知与不判等价）
           && body.IndexOf(QryOrderReqIdNeedle) >= 0;

    internal static ParsedOrderInsert? ParseOrderInsert(ReadOnlySpan<byte> body)
    {
        // 针尾取 3 字节：实例号是 0x0100 倍数时 SDK 会吃掉前面的 E1（见 InsertMarkerTail）
        var reqId = ByteAfter(body, "01017BE3");
        // **全形状锚定**（2026-10-12 三轮探针定案）：SDK 的 tag 是动态值（实测随用户名长度/字段值宽联动漂移——
        // orderRef 的 tag = 0xEE−len(user)（6字符⇒E8、5⇒E9、3⇒EB）；枚举 tag = orderRef tag+2；量的 tag 实测
        // 出现 E3/E8/E9 三种；价格的 tag 实测恒 E4 但不保证）。按固定 tag 找字段天然脆弱——除 reqId 外全部
        // 改为「任意 tag + 形状谓词」锚定（形状在全帧内的唯一性见各谓词注释）。
        var orderRef = RunLast(body, 0, OrderRefShape);
        // 枚举三元组（PriceType/Direction/OffsetFlag 的 ASCII '0'-'4'）——3 字节纯 [0-4] run 唯一
        //（orderRef 12 位、量 run 含低字节、价格 run 含非数字）。
        var enums = RunLast(body, 0, r => r.Length == 3 && r.ToArray().All(b => b is >= (byte)'0' and <= (byte)'4'));
        // 量：VolShape（前缀含 <0x20 低字节 + 尾字节 TimeCondition '1'-'4'）——任意 tag（实测 E3/E8/E9
        // 均出现过；帧内无其它「低字节+尾 TimeCondition」形状的 run）。先定位它的 tag 索引——价格锚点用。
        // **扫描起点必须越过请求头**（2026-10-30 probe1 实测）：头的 op/实例字段随实例号变宽，
        // 一旦 ≥256 就写成 `E3 <hi> <lo>`，而 `hi=0x01` 是个 <0x20 的字节、`lo` 又可能正好落在
        // ASCII '1'-'4'——`E3 01 32` / `E3 01 34` 于是整段满足 VolShape（"量 1 + TimeCondition '2'/'4'"），
        // FindRunIndex 命中的是帧头而不是量，价格锚点随之前移 ⇒ ParseOrderInsert 返回 null ⇒ 该单静默丢弃。
        // 实例号每笔 +2（买偶/撤奇），低字节命中 '2'/'4' 的恰好是 306/308/562/564 这几个值——
        // 与实测丢单的 ref 406/407/534/535 一一对应。
        var payloadStart = body.IndexOf(InsertMarkerTail);
        if (payloadStart < 0) return null;
        payloadStart += InsertMarkerTail.Length;
        var payload = body[payloadStart..];
        var volTagIdx = FindRunIndex(payload, VolShape);
        if (volTagIdx < 0) return null;
        var volRun = ExtractRunAt(payload, volTagIdx);
        // 价格：尾剥零大端 double——解码后 2-8B、非纯 ASCII 数字（4049='@I' 之类）、解析值在 (0,1e9) 合理区间。
        // **位置锚定**：价格紧跟在量 run 之前（帧序：…枚举 → "1" → 价格 → 量 → …）——在量的前缀里取
        // 最后一个合格 run（防帧头 reqId 族 01017B / 量后 0131 等小字节垃圾 run 误中——形状谓词对
        // 1e-303 量级的小值不设防，位置才是硬闸）。低价（<1024，如 ax001 的 100.x）的 double 全字节
        // 可 <0x80——旧谓词 Any(b≥0x80) 对其失效是首轮丢单根因。
        // **E0 转义感知（2026-10-13）**：SDK 对含 tag 字母表字节（E0..EF/FE）的价格值做 E0 前插转义
        // （实测 92.03 → 40 57 01 [E0] EB 85 1E B8 52，线上 run 比逻辑值长 1-8B）——谓词按**去转义后**
        // 的长度/值判形状（旧版按裸 run 判 ⇒ 9B 转义 run 被长度帽 ≤8 拒收 ⇒ 价格误锚到 broker 字段
        // 解出 1.35E-301 垃圾价入簿）。
        var price = volTagIdx > 0
            ? RunLast(payload[..volTagIdx], 0, r => UnescapeRun(r).Length is >= 2 and <= 8
                && !r.ToArray().All(b => b is >= (byte)'0' and <= (byte)'9')
                && ParseTrimmedDouble(r) is > 0 and < 1e9)
            : null;
        // 合约形状 ^[A-Za-z]{1,2}\d{3,4}$（官方命名——1-2 字母；本地品种已改名 zx 对齐，排除用户名/交易所码）
        // 取**最后一个**匹配（合约近帧尾、用户近帧头——避免 3 字母+3 数字的用户名误中）
        var inst = RunLast(body, 0, r =>
        {
            var letters = 0;
            while (letters < r.Length && char.IsAsciiLetter((char)r[letters])) letters++;
            return letters is >= 1 and <= 2 && r.Length - letters is >= 3 and <= 4
                && r[letters..].ToArray().All(IsDigit);
        });
        if (reqId is null || orderRef is null || enums is null || price is null || volRun is null || inst is null) return null;
        return new ParsedOrderInsert(reqId.Value, Encoding.ASCII.GetString(orderRef), Encoding.ASCII.GetString(inst),
            (char)enums[0], (char)enums[1], (char)enums[2],
            ParseTrimmedDouble(price), (int)ParseMinimalInt(volRun[..^1]), (char)volRun[^1]);
    }

    /// <summary>定位第一个满足形状谓词的 run 的 tag 起始索引（找不到 −1）——量字段定位 + 价格位置锚点。</summary>
    private static int FindRunIndex(ReadOnlySpan<byte> body, Func<ReadOnlySpan<byte>, bool> match)
    {
        for (var i = 0; i < body.Length; i++)
        {
            if (!IsTag(body[i])) continue;
            var j = i + 1;
            while (j < body.Length)
            {
                if (body[j] == 0xE0 && j + 1 < body.Length) { j += 2; continue; }
                if (IsTag(body[j])) break;
                j++;
            }
            if (j > i + 1 && match(body[(i + 1)..j])) return i;
        }
        return -1;
    }

    /// <summary>提取指定 tag 索引处的 run 值（E0 转义感知）。</summary>
    private static byte[]? ExtractRunAt(ReadOnlySpan<byte> body, int tagIdx)
    {
        var j = tagIdx + 1;
        while (j < body.Length)
        {
            if (body[j] == 0xE0 && j + 1 < body.Length) { j += 2; continue; }
            if (IsTag(body[j])) break;
            j++;
        }
        return j > tagIdx + 1 ? body[(tagIdx + 1)..j].ToArray() : null;
    }

    internal static ParsedOrderAction? ParseOrderAction(ReadOnlySpan<byte> body)
    {
        var reqId = ByteAfter(body, "E101013BE3");
        // OrderRef 不锚 tag：㉒ 帧 OrderRef 前的 tag 随 user 长度/SessionID 形态漂移（实测对照——
        // E2E m2user(6B)+SessionId=1 → EB；cancelp1(8B)+4B 大端 SessionId → E9）。旧版硬编码 EB 只在
        // E2E 形状下成立，botmm(5B)/botnoise(7B)/真实会话全部失配 ⇒ 静默无应答 ⇒ 客户端 15s 撤单超时、
        // 交易所侧撤单根本没发（2026-10-16 双 bot 换防全瘫根因）。改「任意 tag 的 12 位零填充数字 run」
        // 形状匹配（帧内唯一——reqId/序号 1B、broker 4 位、user 混编、SessionID 大端非 ASCII 均不撞形；
        // 与 DebugFields 探针同款）。
        var orderRef = RunLast(body, 0, OrderRefShape);
        if (reqId is null || orderRef is null) return null;
        return new ParsedOrderAction(reqId.Value, Encoding.ASCII.GetString(orderRef));
    }

    /// <summary>探针用：逐字段解析结果（每行一个字段，null 即缺口）——CodecProbeTests 消费。</summary>
    internal static List<string> DebugFields(ReadOnlySpan<byte> body)
    {
        var lines = new List<string>();
        var reqId = ByteAfter(body, "01017BE3");
        lines.Add($"reqId={(reqId is { } r ? r.ToString() : "null")}");
        var orderRef = RunLast(body, 0, OrderRefShape);
        lines.Add($"orderRef={(orderRef is null ? "null" : Encoding.ASCII.GetString(orderRef))}");
        var enums = RunAfter(body, 0xEA, r => r.Length == 3 && r.ToArray().All(b => b is >= (byte)'0' and <= (byte)'4'));
        lines.Add($"enums(EA)={(enums is null ? "null" : Encoding.ASCII.GetString(enums))}");
        var price = RunAfter(body, 0xE4, r => r.Length is >= 2 and <= 8 && !r.ToArray().All(b => b is >= (byte)'0' and <= (byte)'9'));
        lines.Add($"price(E4)={(price is null ? "null" : $"{ParseTrimmedDouble(price)} (hex {Convert.ToHexString(price)})")}");
        var vol = RunAfter(body, 0xE8, VolShape) ?? RunAfter(body, 0xE7, VolShape) ?? RunAfter(body, 0xE9, VolShape);
        lines.Add($"vol(E8/E7/E9)={(vol is null ? "null" : $"{ParseMinimalInt(vol[..^1])} tc={(char)vol[^1]} (hex {Convert.ToHexString(vol)})")}");
        var inst = RunLast(body, 0, r =>
        {
            var letters = 0;
            while (letters < r.Length && char.IsAsciiLetter((char)r[letters])) letters++;
            return letters is >= 1 and <= 2 && r.Length - letters is >= 3 and <= 4
                && r[letters..].ToArray().All(IsDigit);
        });
        lines.Add($"inst={(inst is null ? "null" : Encoding.ASCII.GetString(inst))}");
        return lines;
    }

    /// <summary>量字段 run 形状：len 2-5、尾字节 = TimeCondition（'1' IOC / '2' FOK / '3' GFD / '4' PostOnly——本地协议扩展），
    /// 前缀含低字节。</summary>
    private static bool VolShape(ReadOnlySpan<byte> r)
        => r.Length is >= 2 and <= 5
           && r[^1] is (byte)'1' or (byte)'2' or (byte)'3' or (byte)'4'
           && r[..^1].ToArray().Any(b => b < 0x20);

    /// <summary>orderRef run 形状：恒 12 位零填充数字（D12）。</summary>
    private static bool OrderRefShape(ReadOnlySpan<byte> r)
        => r.Length == 12 && r.ToArray().All(IsDigit);

    /// <summary>needle（hex）后紧跟的 1 字节值——<b>E0 转义感知</b>（2026-10-14：SDK 对值字节落在
    /// tag 字母表 {E0..EF,FE} 的写法是 [E0][值] 前插转义——实测 ⑰ reqId=224 → `E1A6E3 E0E0`；
    /// 旧版裸读会拿到转义前缀 E0（224 恰好同值纯属巧合，225 起错位）。</summary>
    private static byte? ByteAfter(ReadOnlySpan<byte> body, string needleHex)
    {
        var needle = Convert.FromHexString(needleHex);
        var hit = body.IndexOf(needle);
        if (hit < 0 || hit + needle.Length >= body.Length) return null;
        var v = body[hit + needle.Length];
        if (v == 0xE0 && hit + needle.Length + 1 < body.Length)
            return body[hit + needle.Length + 1];   // 剥转义取真值
        return v;
    }

    /// <summary>任意 tag 下满足形状谓词的**最后一个** run（合约字段近帧尾——用户字段近帧头的形状冲突防误中）。</summary>
    private static byte[]? RunLast(ReadOnlySpan<byte> body, byte tag, Func<ReadOnlySpan<byte>, bool> match)
    {
        byte[]? last = null;
        for (var i = 0; i < body.Length; i++)
        {
            if (tag != 0 ? body[i] != tag : !IsTag(body[i])) continue;
            var j = i + 1;
            while (j < body.Length)
            {
                if (body[j] == 0xE0 && j + 1 < body.Length) { j += 2; continue; }
                if (IsTag(body[j])) break;
                j++;
            }
            if (j > i + 1 && match(body[(i + 1)..j])) last = body[(i + 1)..j].ToArray();
            i = j - 1;
        }
        return last;
    }

    /// <summary>tag（0 = 任意 tag）后满足形状谓词的第一个 run（E0 转义感知；值长 = tag 到下一 tag）。</summary>
    private static byte[]? RunAfter(ReadOnlySpan<byte> body, byte tag, Func<ReadOnlySpan<byte>, bool> match, string what = "")
    {
        for (var i = 0; i < body.Length; i++)
        {
            if (tag != 0 ? body[i] != tag : !IsTag(body[i])) continue;
            var j = i + 1;
            while (j < body.Length)
            {
                if (body[j] == 0xE0 && j + 1 < body.Length) { j += 2; continue; }
                if (IsTag(body[j])) break;
                j++;
            }
            if (j > i + 1 && match(body[(i + 1)..j])) return body[(i + 1)..j].ToArray();
            i = j - 1;
        }
        return null;
    }

    /// <summary>尾剥零大端 double 的解码（右补零到 8B）——模板 TrimmedDouble 的逆。
    /// E0 转义感知：值内 {E0..EF,FE} 字节前插 E0（SDK 请求侧实证 92.03 → 40 57 01 [E0] EB 85 1E B8 52；
    /// 旧版直读转义字节 ⇒ 非二进小数价被解成 1.35E-301 量级垃圾价入簿——修前实测踩中）。</summary>
    internal static double ParseTrimmedDouble(ReadOnlySpan<byte> wire)
    {
        var un = UnescapeRun(wire);
        var b = new byte[8];
        un.AsSpan(0, Math.Min(un.Length, 8)).CopyTo(b);
        return BinaryPrimitives.ReadDoubleBigEndian(b);
    }

    /// <summary>剥离 run 值内的 E0 转义（E0 + 下一字节 = 一个字面值字节；E0 自身亦被转义为 E0 E0）。</summary>
    private static byte[] UnescapeRun(ReadOnlySpan<byte> wire)
    {
        var r = new byte[wire.Length];
        var n = 0;
        for (var i = 0; i < wire.Length; i++)
        {
            if (wire[i] == 0xE0 && i + 1 < wire.Length) i++;   // 丢弃转义前缀，保留被转义的字面字节
            r[n++] = wire[i];
        }
        return r[..n];
    }

    /// <summary>去前导零大端 int 的解码（左补零到 8B）——模板 MinimalInt 的逆。</summary>
    internal static long ParseMinimalInt(ReadOnlySpan<byte> wire)
    {
        var b = new byte[8];
        wire.CopyTo(b.AsSpan(8 - wire.Length));
        return BinaryPrimitives.ReadInt64BigEndian(b);
    }
}
