// ─────────────────────────────────────────────────────────────────────────────
// CtpFrontTemplates.cs —— 由抓包自动生成，勿手改。
// 生成器: dotnet run --project apps/CtpSim/Acme.Ctp.ProtoCapture -- gen <trade.txt> <本文件>
//   （FrontGen.cs：抓包 S→C 应答帧 verbatim 复刻 + 标记界定字段替换；全部偏移由
//     生成器计算，needle 唯一命中/编辑区无重叠/余额像钱均生成期断言——零人工转录。）
// 抓包源: /var/folders/wf/6fth7gs95xzfhk1n59kpzhvc0000gn/T/ctp-proto-capture/20260929-031656/trade.txt（22 帧）
// 帧字节为抓包原样（含 SimNow 认证 token/密文/账号）——仅本地仿真用，勿入公网/勿分发。
//
// **2026-10-13 手改批（与 FrontGen.cs 同步更新——再生成须保留同款编码）**：推送/应答帧的
// 价格槽改 PriceSlot（等宽 + 量化 + 避 tag 字节）、orderRef 改 F(,12) 等宽、状态消息改
// EscapedGbkFixed（转义膨胀安全）、量槽 VolumeEdit（≥256 切 E7）——根因：TrimmedDouble 宽度随值
// （88/92/96/100 → 2B、92.03 → 8B 含 tag 字节）⇒ 帧缩/撑 ⇒ SDK 记录遍历失步 ⇒ ax001 双 bot
// 断连风暴（E2E CtpFrontPriceWidthTests + RtnOrderWidthShapeTests 钉死）。原始抓包文件已随临时
// 目录清空，无法再生成——本文件即权威。
// ─────────────────────────────────────────────────────────────────────────────

using System.Buffers.Binary;
using System.Linq;
using System.Text;

namespace Ctp.Net.Front;

/// <summary>CTP 前置应答帧模板（抓包 verbatim + 偏移替换）——由 ProtoCapture gen 生成。</summary>
internal static class CtpFrontTemplates
{
    // GBK 错误消息（EscapedGbk）需要 CodePages provider——.NET 默认只带 UTF/ASCII，macOS/Linux 上必须注册
    static CtpFrontTemplates() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    internal static byte[] H(string hex) => Convert.FromHexString(hex);

    /// <summary>ASCII 字符串值 → 字节（协议字段值为 ASCII）。</summary>
    internal static byte[] A(string s) => Encoding.ASCII.GetBytes(s);

    /// <summary>等宽归一化：值截断/右补空格到抓包槽宽再转 ASCII。verbatim 模板对字段宽度敏感——
    /// 变长替换缩/撑 payload 曾致 SDK 记录遍历失步挂死（>6 字符用户/空 AppId 均实测）；
    /// 任意宽度 broker/user/AppId/账户都归一到抓包形状。回显字段仅语义展示，账户身份以核心账本为准。</summary>
    internal static byte[] F(string s, int width)
    {
        var v = s.Length > width ? s[..width] : s.PadRight(width, ' ');
        return Encoding.ASCII.GetBytes(v);
    }

    /// <summary>
    /// 记录帧里主合约槽的宽度（⑯ 报单 / ⑱ 持仓 / ⑲ 成交 /  tick 全部按 5 字节捕获而定宽）。
    /// 超宽会被 <see cref="F"/> **静默截断**（"rb2510" ⇒ "rb251"）——截断后的合约在客户端能匹配到
    /// 另一个不存在的品种上，比报错危险得多（2026-10-29 盘点出的潜在缺陷 #7）。因此各应答/推送
    /// 构造点用 <see cref="FitsInstrument"/> 先判宽，超宽即跳过该行并落日志（宁可少一行，不给错一行）。
    /// </summary>
    internal const int RecordInstrumentWidth = 5;

    /// <summary>查合约应答记录帧的合约槽宽度（6 字节——真前置抓包 "sc2706" 定宽）。</summary>
    internal const int InstrumentQueryWidth = 6;

    /// <summary>合约号能否塞进指定宽度的槽而不被截断（<see cref="F"/> 会截断，不报错）。</summary>
    internal static bool FitsInstrument(string? instrumentId, int width)
        => !string.IsNullOrEmpty(instrumentId) && instrumentId.Length <= width;

    /// <summary>资金字段值：大端 double（8B）。</summary>
    internal static byte[] Money(double v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(b, v);
        return b;
    }

    /// <summary>资金字段值：大端 double + E0 转义（抓包实证 999927.69 → 逻辑 41 2E 83 EF 61 47 AE 14，
    /// 线上 41 2E 83 E0 EF 61 47 AE 14——tag 字母表 {E0..EF, FE} 字节前插 E0；E0 自身亦须转义。
    /// 转义范围 E0..EF+FE 为实证+推断（EF 由抓包证明，E0/FE 由「读取方按 tag 分界」必然性推出；
    /// F0..FD/FF 未观察到、推定无需转义）。值的线上宽度可变（8B 起，每个转义 +1）。</summary>
    internal static byte[] EscapedMoney(double v)
    {
        var raw = Money(v);
        var n = 0;
        foreach (var b in raw)
            if (b is (>= 0xE0 and <= 0xEF) or 0xFE) n++;
        var r = new byte[raw.Length + n];
        var i = 0;
        foreach (var b in raw)
        {
            if (b is (>= 0xE0 and <= 0xEF) or 0xFE) r[i++] = 0xE0;
            r[i++] = b;
        }
        return r;
    }

    /// <summary>字节拼接（多段值组成一个 Edit 替换块）。</summary>
    internal static byte[] Cat(params byte[][] parts)
    {
        var r = new byte[parts.Sum(p => p.Length)];
        var i = 0;
        foreach (var p in parts) { p.CopyTo(r, i); i += p.Length; }
        return r;
    }

    /// <summary>价格槽等宽填充：TrimmedDouble 右补零到槽宽（解析侧右补零到 8B 还原同值——M3 实证变宽即失步）。
    /// 已被 <see cref="PriceSlot"/> 取代（同宽 + 量化 + 避 tag 字节）——保留供生成器历史形状对照。</summary>
    internal static byte[] PadTrimmedDouble(double v, int width)
    {
        var t = TrimmedDouble(v);
        return t.Length >= width ? t[..width] : Cat(t, new byte[width - t.Length]);
    }

    /// <summary>
    /// 价格槽等宽编码（恒 width 字节）——<b>宽度随值的 TrimmedDouble 是 2026-10-13 ax001 双 bot 断连风暴的根因</b>：
    /// 88/92/96/100（整数价，double 第 3 字节 0x00）剥尾零后 2B、92.03 等非二进小数价 8B 且内含 tag 字节 ⇒
    /// 帧缩/撑 ⇒ SDK 记录遍历失步 ⇒ 其后全部推送静默失效 + 4097 重连循环（服务器实测：MM 阶梯 90/89 恰 3B
    /// 能收回报、88 起全丢；E2E CtpFrontPriceWidthTests 钉死）。编码 = ①量化到 width 字节可精确表示的值
    /// （<paramref name="roundUp"/>：null=四舍五入 / true=向上取整 / false=向下截断——MD tick 的 ask 用 ceil、
    /// bid 用 floor：taker 拿到的对手价回显恒保证越过真实价（3B 槽 [64,128) ulp=1/64 &gt; ax001 的 0.01 tick ⇒
    /// 最近值回显可能比真实价低 0.0044 ⇒ noise 的 IOC 限价够不到真实卖价 ⇒ 双 bot 市场恒不成交——2026-10-13
    /// 本地双 bot 实测；成交仍按 maker 价，ceil 无经济副作用）；②剥/补尾零到恒 width；③避开 tag 字母表字节
    /// （E0..EF/FE——值内出现会被「读到下一 tag」式解析截断，SDK ⑲ 请求侧对含 tag 字节的价格做 E0 转义
    /// 实证了这一点；递减 1 ulp 让位，量化回显本就近似）。
    /// </summary>
    internal static byte[] PriceSlot(double v, int width = 3, bool? roundUp = null)
    {
        var raw = Money(v);
        // ① 量化进位（最近值：第 width+1 字节 ≥ 0x80 → 前缀 +1；ceil：截掉字节非零即进位；floor：恒不进位）
        var carry = roundUp switch
        {
            true => raw[width..].Any(b => b != 0),
            false => false,
            _ => raw[width] >= 0x80,
        };
        if (carry)
        {
            var prefix = raw.ToArray()[..width];
            var carryOn = true;
            for (var i = width - 1; i >= 0 && carryOn; i--)
            {
                prefix[i]++;
                carryOn = prefix[i] == 0;
            }
            if (!carryOn) raw = Cat(prefix, new byte[raw.Length - width]);
        }
        // ② 剥尾零（量化后低位恒 0）→ 不足 width 右补零——恒 width 字节
        var n = width;
        while (n > 1 && raw[n - 1] == 0) n--;
        var slot = n >= width ? raw[..width].ToArray() : Cat(raw[..n], new byte[width - n]);
        // ③ 避开 tag 字母表（E0..EF/FE）：递减 1 ulp 让位（big-endian 减法；全 0 放弃——0 值天然无 tag 字节）
        for (var guardI = 0; guardI < 4 && slot.Any(b => b is (>= 0xE0 and <= 0xEF) or 0xFE); guardI++)
        {
            var done = false;
            for (var i = width - 1; i >= 0; i--)
            {
                if (slot[i] == 0) { slot[i] = 0xFF; continue; }
                slot[i]--;
                done = true;
                break;
            }
            if (!done) break;
        }
        return slot;
    }

    /// <summary>大端 double 剥尾零（8000.0 → 40BF40 三字节——⑲/推送价格实证；整槽替换宽度随值）。</summary>
    internal static byte[] TrimmedDouble(double v)
    {
        var b = Money(v);
        var n = b.Length;
        while (n > 1 && b[n - 1] == 0) n--;
        return b[..n];
    }

    /// <summary>大端 int 剥前导零（2 → 02、148 → 94、量 1 → 01——⑲/㉑ 实证；宽度随值）。</summary>
    internal static byte[] MinimalInt(long v)
    {
        if (v == 0) return [0x00];
        var b = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(b, v);
        var n = 0;
        while (n < 7 && b[n] == 0) n++;
        return b[n..];
    }

    /// <summary>应答计数器字段统一编码（<b>真前置 2026-10-15 300 笔实抓定案</b>，2467163@182.254.243.31:30001
    /// 录制实证）：计数器为 <b>int 永不回卷</b>——值 1-223 = `E3 &lt;裸字节&gt;`；224-239（E0..EF）= `E3 [E0][值]`
    /// 转义对（本地实测客户端接受；真前置实抓为跳段 223→240，两形态皆过）；240-253 = `E3 &lt;裸字节&gt;`；
    /// <b>FE/FF（254/255）也走转义对</b>（2026-10-19 F0 族推送 E2E 实证：254/255 裸值在 F0 族被 SDK 拒收——
    /// 前置发出但客户端停派发 ⇒ 长会话假死；应答族 17B 实证的裸 FE/FF 形态不适用于 F0 推送族的解析路径）；
    /// ≥256 = **`E2 &lt;hi&gt;&lt;lo&gt;` 两字节大端**（真前置 18B 应答 `E2 01 2A`=298 实证——客户端对 ≥256 的校验
    /// 只认此形态；E3 单字节无法表达且骤降回卷必被拒收）。</summary>
    internal static byte[] InstanceField(int value)
    {
        if (value <= 255)
        {
            var b = (byte)value;
            return b is (>= 0xE0 and <= 0xEF) or 0xFE or 0xFF
                ? new byte[] { 0xE3, 0xE0, b }              // 转义对（224-239 + FE/FF）
                : new byte[] { 0xE3, b };                    // 裸值
        }
        // ≥256：E2 + 2B 大端——值内 E0..EF 字节须转义（480=0x01E0 的 E0 被客户端当转义前缀吞下一
        // 字段 ⇒ 帧丢弃——2026-10-15 复现器 480 轮实测；EscapedMoney 同一字母表纪律）；FE/FF 裸值
        var run = new List<byte> { 0xE2 };
        foreach (var b in new[] { (byte)(value >> 8), (byte)value })
        {
            if (b is (>= 0xE0 and <= 0xEF)) { run.Add(0xE0); }
            run.Add(b);
        }
        return run.ToArray();
    }

    /// <summary>单字节/短值转义：值字节落在 tag 字母表 {E0..EF, FE} 内时前插 E0（E0 自身亦转义）——
    /// <b>2026-10-14 4:20 死循环根因的修复面</b>：reqId/instance 等「请求衍生或计数器」值写入响应帧时，
    /// SDK（请求侧实证 224=0xE0 → E3 E0E0）会转义、真前置同款——前置写裸字母表字节（尤其末尾裸 E0
    /// = 转义前缀吃下一字节撞帧尾）⇒ 帧畸形被 SDK 丢弃不派发 ⇒ 查询挂死 rc=-2 ⇒ 4097。槽位是
    /// tag 终结的值 run（或帧尾）——转义 +1B 宽度合法（EscapedMoney 同款「SDK 容忍整槽伸缩」）。</summary>
    internal static byte[] EscapedValue(params byte[] raw)
    {
        var n = 0;
        foreach (var b in raw)
            if (b is (>= 0xE0 and <= 0xEF) or 0xFE) n++;
        if (n == 0) return raw;
        var r = new byte[raw.Length + n];
        var i = 0;
        foreach (var b in raw)
        {
            if (b is (>= 0xE0 and <= 0xEF) or 0xFE) r[i++] = 0xE0;
            r[i++] = b;
        }
        return r;
    }

    /// <summary>GBK 文本 + E0 转义（⑳ 实证：值内 {E0..EF,FE} 字节前插 E0，E0 自身亦转义——与 EscapedMoney 同一字母表）。</summary>
    internal static byte[] EscapedGbk(string text) => EscapedValue(Gbk(text));

    /// <summary>GBK 原始字节（未转义）——查交易所的「简称槽 = 7 − ID 长度」按这个宽度算，转义膨胀不计。</summary>
    internal static byte[] Gbk(string text) => Encoding.GetEncoding("GBK").GetBytes(text);

    internal readonly record struct Edit(int Offset, int OldLen, byte[] NewBytes);

    /// <summary>
    /// 标记界定替换（值两侧有标记 ⇒ 变长替换安全）。偏移倒序应用（高偏移先改，
    /// 低偏移不受影响）——生成期已断言编辑区互不重叠，运行期无需复查。
    /// </summary>
    internal static byte[] Apply(byte[] template, params Edit[] edits)
    {
        var result = template;
        foreach (var e in edits.OrderByDescending(e => e.Offset).ToArray())
        {
            var next = new byte[result.Length - e.OldLen + e.NewBytes.Length];
            result.AsSpan(0, e.Offset).CopyTo(next);
            e.NewBytes.CopyTo(next, e.Offset);
            result.AsSpan(e.Offset + e.OldLen).CopyTo(next.AsSpan(e.Offset + e.NewBytes.Length));
            result = next;
        }
        return result;
    }

    // ===== 帧 ④ 连接确认（19B payload——verbatim 无替换） =====
    internal static readonly byte[] ConfirmPayload = H(
        "0103344CE43028E501E159E755EFEFEFEFEFEA"
    );

    // ===== 帧 ⑥ 认证响应（198B payload：reqId/broker/user/AppId 替换） =====
    internal static readonly byte[] AuthRspTemplate = H(
        "0103344CE43011E502015BE3011012E1FE39393939E7323437313633EFE67053327A777475667372787369526F426B6F43576D50517244436F666C56594B4D596867724C765261336873534632633143366F72584E357A62695334654451635336434341724D424A64746D64466D504D4B66487869654741364B4E487A5841447A616431554F493766326B566F3846366E6B42467966644530306B785753E573696D6E6F775F636C69656E745F74657374EF34EFEFEFE755E44354503AD5FDC8B7EFEFEFEFED"
    );

    /// <summary>⑥ 认证响应（Base64 token 抓包原样——固定挑战下 token 无会话效性，可回放）。</summary>
    internal static byte[] BuildAuthRsp(string brokerId, string userId, string appId, byte reqId) =>
        Apply(AuthRspTemplate,
            new Edit(12, 1, EscapedValue(reqId)),
            new Edit(17, 4, F(brokerId, 4)),
            new Edit(22, 6, F(userId, 6)),
            new Edit(159, 18, F(appId, 18))
        );

    // ===== 帧 ⑧ 登录预响应（77B payload：broker/user 替换） =====
    internal static readonly byte[] LoginRspTemplate = H(
        "0103344CE43011E502015BE301E355E4D5FDC8B7EFEFEFEFEFE21012E1FE39393939E7323437313633EFEFEFEFEFEFEFEFEFEFE30173696D6E6F775F636C69656E745F74657374EF31EFEFEFE4"
    );

    /// <summary>⑧ 登录预响应（SDK 收 ⑧ 后自动发 ⑨ 系统信息请求）。</summary>
    internal static byte[] BuildLoginRsp(string brokerId, string userId) =>
        Apply(LoginRspTemplate,
            new Edit(30, 4, F(brokerId, 4)),
            new Edit(35, 6, F(userId, 6))
        );

    // ===== 帧 ⑩ 登录结果主帧（206B payload：loginReqId/broker/user/交易日×2 替换） =====
    internal static readonly byte[] SysInfoRspTemplate = H(
        "0103344CE43001E503018DE302E355E4D5FDC8B7EFEFEFEFEFE24036E1087FFFFFFFE22710100301243230323630393239E131313A31363A3536E139393939E7323437313633EA54726164696E67486F7374696E67EFEF012DAF7B0231EC31313A31363A3535E131313A31363A3535E131313A31363A3535E131313A31323A3034E131313A31363A3535E176362E372E31335F58435F32303236303431332E31323336352E746B65726E656CE831313A31363A3535E401E301323032363039323931313A31333A3232EFEFEFEFE6"
    );

    /// <summary>⑩ 登录结果主帧（交易日/FrontID/SessionID/MaxOrderRef——⑩ 到达即完成 OnRspUserLogin）。</summary>
    internal static byte[] BuildSysInfoRsp(string brokerId, string userId, byte loginReqId, string tradingDay, string maxOrderRef) =>
        Apply(SysInfoRspTemplate,
            new Edit(12, 1, EscapedValue(loginReqId)),
            new Edit(59, 4, F(brokerId, 4)),
            new Edit(64, 6, F(userId, 6)),
            new Edit(41, 8, F(tradingDay, 8)),
            new Edit(185, 8, F(tradingDay, 8)),
            new Edit(92, 1, A(maxOrderRef))
        );

    /// <summary>⑩ 伴随帧（42B payload + 4B 头 = 46B 整帧——与主帧同一次 TCP 写出）。</summary>
    internal static readonly byte[] SysInfoCompanionFrame = H(
        "0200002A0103344CE4F102E504E128E41001E106E101E41001E106E102E41001E106E103E203A41001E106E104E4"
    );

    // ===== 帧 ⑫ 结算确认应答（65B payload：instance/reqId/broker/user/交易日 替换；⑭ 复用同模板 instance=02） =====
    internal static readonly byte[] SettleRspTemplate = H(
        "0103344CE101E24014E301E102E19CE303E355E4D5FDC8B7EFEFEFEFEFE2040FE13F39393939E7323437313633E73230323630393239E131313A31363A3536EFE7"
    );

    /// <summary>⑫⑭ 结算确认应答（instance 自增：首次 01、二次 02）。</summary>
    internal static byte[] BuildSettleRsp(int instance, byte reqId, string brokerId, string userId, string tradingDay) =>
        Apply(SettleRspTemplate,
            new Edit(9, 2, InstanceField(instance)),
            new Edit(16, 1, EscapedValue(reqId)),
            new Edit(34, 4, F(brokerId, 4)),
            new Edit(39, 6, F(userId, 6)),
            new Edit(46, 8, F(tradingDay, 8))
        );

    // ===== 帧 ⑯ 资金应答（134B payload：reqId/broker/账户×2/交易日 + 6 个 E0 转义大端余额字段） =====
    internal static readonly byte[] QryAccountRspTemplate = H(
        "0103344CE104E28007E301E101018EE305E10C018A39393939E13234373136E1323437313633E130E130E139EFE2412E83E0EF6147AE14412E83E0EF6147AE14EFEFEFEFEFEFEE412E83E0EF6147AE14412E83E0EF6147AE14412E83E0EF6147AE14E83230323630393239E401EFEFEFE3434E59EFEFEB412E83E0EF6147AE14EFEFEFEFEFEE"
    );

    /// <summary>
    /// ⑯ 资金应答（6 个 E0 转义余额字段全部 ← Balance——整槽替换，线上宽度随值内 tag 字节数可变）。
    /// <paramref name="instance"/> = **应答侧 80 族记录流水**（E3 后 1B，逐条递增——2026-10-03 SimNow
    /// 过滤查报单定案：非请求回显）——客户端按它丢弃过期帧：旧版恒回 captured 的 01 ⇒ 同一会话第二次
    /// ⑮ 起应答被客户端静默丢弃（读而不派发——Recv-Q=0、无断线事件、30s 超时），bot 长会话必死。
    /// </summary>
    internal static byte[] BuildQryAccountRsp(int instance, byte reqId, string brokerId, string accountId, string userId, string tradingDay, double balance) =>
        Apply(QryAccountRspTemplate,
            new Edit(9, 2, InstanceField(instance)),
            new Edit(16, 1, EscapedValue(reqId)),
            new Edit(21, 4, F(brokerId, 4)),
            new Edit(26, 5, A(accountId.Length >= 5 ? accountId[..5] : accountId.PadLeft(5, '0'))),
            new Edit(32, 6, F(accountId, 6)),
            new Edit(99, 8, F(tradingDay, 8)),
            new Edit(46, 9, EscapedMoney(balance)),
            new Edit(55, 9, EscapedMoney(balance)),
            new Edit(71, 9, EscapedMoney(balance)),
            new Edit(80, 9, EscapedMoney(balance)),
            new Edit(89, 9, EscapedMoney(balance)),
            new Edit(119, 9, EscapedMoney(balance))
        );

    // ===== 帧 ⑱ 持仓应答·空表（13B payload：仅尾字节 reqId 替换） =====
    internal static readonly byte[] QryPositionEmptyRspTemplate = H(
        "0103344CE104E28005E302E706"
    );

    /// <summary>⑱ 持仓应答·空表（instance = 请求侧 80 族计数回显——见 <see cref="BuildQryAccountRsp"/>）。</summary>
    internal static byte[] BuildQryPositionEmptyRsp(int instance, byte reqId) =>
        Apply(QryPositionEmptyRspTemplate,
            new Edit(9, 2, InstanceField(instance)),
            new Edit(12, 1, EscapedValue(reqId))
        );

    // ===== 帧 ⑱ 持仓应答·非空单行（261B payload——2026-10-03 SimNow 实抓：账户 1 手 m2701 多·今仓。
    // 字段经 tag-walker 解码定位；金额/名称/静态元数据字段保留捕获 verbatim（客户端仅映射
    // 合约/交易所/方向/今昨总仓四组决策字段）；量槽 E7(1B) = 总仓（探针 E2E 定位）=====
    internal static readonly byte[] QryPositionRspTemplate = H(
        "0103344CE104E28005E302E10101BEE306E10D01BA6D32373031EFEB39393939E2B3CCC8BAE1323437313633E139393939E23231"
        + "31E701E302E440F040EFE101E440E0E07FC0EC40E0E07FC0EC40B27A999999999A40C2333333333334E83FD999999999999AE83F"
        + "C999999999999AE8C034E640AA26E540AA62E53230323630393330E40140E0E07FC0E440B27A999999999AEFEFE1013FC1E0EB85"
        + "1EB851E0ECEFE9444345E16D323730E9890C100C497FEFEC6D32373031E1444345E16D32373031E16D32373031E16DE131E13230"
        + "3237E131E131303030E131E131303030E131E13130E131E13230323630313139E13230323630313139E13230323730313135E132"
        + "E9"
    );

    /// <summary>
    /// ⑱ 持仓应答·非空单行（每仓一行——方向三元组 <paramref name="directionChar"/><c>1</c><paramref name="positionDateChar"/>、
    /// 量槽 = 总仓 1B minimal int）。金额字段（保证金/盈亏/成本）保留捕获 verbatim——客户端模型映射但
    /// 券商 offset 决策不消费。多行收集在 wrapper 静默期收尾（2026-10-05——与查合约同路径：tag 帧恒
    /// isLast=true，多仓行全部到达 CtpTrader.OnQueryItem 静默窗）。
    /// </summary>
    /// <summary>
    /// 测试钩子（2026-10-29 槽位探针）：持仓记录帧写完后允许测试逐字节改写，用来定位「客户端真正读的是哪个槽」。
    /// 生产恒为 null。<b>为什么需要它</b>：原「E7 槽 = 总仓」的结论只用过 1 手样本验证，而捕获模板本身
    /// 就是 1 手 ⇒ 写入值与被覆盖的常量同值，假阳性无法区分；这次用 5 手暴露出来（客户端仍读到 1）。
    /// </summary>
    internal static Action<byte[]>? QryPositionRecordHook;

    internal static byte[] BuildQryPositionRsp(int instance, byte reqId, string brokerId, string investorId,
        string instrumentId, string exchangeId, string tradingDay, char directionChar, char positionDateChar,
        int totalPosition, int todayPosition, Action<string>? warn = null) =>
        Hooked(Apply(QryPositionRspTemplate,
            new Edit(9, 2, InstanceField(instance)),
            new Edit(16, 1, EscapedValue(reqId)),
            new Edit(21, 5, F(instrumentId, 5)),
            new Edit(28, 4, F(brokerId, 4)),
            new Edit(38, 6, F(investorId, 6)),
            new Edit(45, 4, F(brokerId, 4)),
            new Edit(50, 3, A($"{directionChar}{'1'}{positionDateChar}")),
            // 量槽捕获 1B（E7 tag + MinimalInt）——总仓 ≥256 时 MinimalInt 2B 撑宽（MM 仓限 ±200 达不到；
            // SDK 请求侧对大整数有 E7 切换惯例但本槽 tag 已是 E7，未实测边界——如实撑宽）。
            new Edit(54, 1, VolumeSlot(totalPosition, 54, "总仓", warn)),
            // 今仓槽 = payload offset 150（2026-10-29 探针定位：该字节 1→7 时客户端 TodayPosition 恰好 1→7，
            // 0/5/100/255 全部逐一验证；同批确认 54 = 总仓）。此前该槽一直是捕获常量 1 ⇒
            // 客户端永远看到「今仓 1」，昨夜仓与今仓的拆分不可信。
            new Edit(150, 1, VolumeSlot(todayPosition, 150, "今仓", warn)),
            new Edit(123, 8, A(tradingDay)),
            new Edit(163, 3, F(exchangeId, 3)),
            new Edit(167, 4, F(instrumentId, 4)),
            new Edit(180, 5, F(instrumentId, 5)),
            new Edit(186, 3, F(exchangeId, 3)),
            new Edit(190, 5, F(instrumentId, 5)),
            new Edit(196, 5, F(instrumentId, 5))
        ));

    /// <summary>
    /// 单字节量槽写入。槽宽 1B 是抓包实证（`E7 01` / `E1 01`），实测 0..255 逐值可用；
    /// 超过 255 时**截到 255 并告警**——旧注释里「MinimalInt 2B 撑宽」是未验证的猜测，
    /// 撑宽会整体后移后续字段（记录帧是定宽遍历的，2026-10-13 那类断连风暴的成因），
    /// 所以这里选择「小而不崩 + 明确日志」，而不是拿帧宽去赌。
    /// </summary>
    private static byte[] VolumeSlot(int value, int offset, string what, Action<string>? warn)
    {
        var v = Math.Clamp(value, 0, 255);
        if (v != value)
            warn?.Invoke($"[CTP-Front] 持仓{what}={value} 超过单字节槽上限，按 255 上报（槽在 payload@{offset}，加宽会失步）");
        return [(byte)v];
    }

    private static byte[] Hooked(byte[] record)
    {
        QryPositionRecordHook?.Invoke(record);
        return record;
    }

    /// <summary>
    /// 查报单空应答（13B payload / 17B 线上——2026-10-03 SimNow 实抓：过滤无匹配报单的合约时真前置回此 tag
    /// 帧而非 LZ4 二进制；**2026-10-24 真前置复抓逐字节一致**（`E104E28001 E303 E707`，见
    /// <c>RealFrontGoldenFrameTests</c>）。大清单（6 行实测 811B）走二进制——「不逆向」，本地前置恒回空表：
    /// 客户端 QueryOrderAsync 管线不再被未应答查询堵死（期货页委托 tab 显示空是已知边界；空结果 SDK 不派发
    /// 回调是其既有行为，真前置同款）。
    /// 结构 = op `E104E28001` + E3 应答流水 + E7 reqId 回显（与 ⑱ 空表同构）。
    /// </summary>
    internal static readonly byte[] QryOrderEmptyRspTemplate = H("0103344CE104E28001E309E708");

    /// <summary>查报单空应答（E3 值槽 = offset 10、E7 reqId 值槽 = offset 12）。op 用 +1 惯例（E4→E5）。</summary>
    internal static byte[] BuildQryOrderEmptyRsp(int e3Value, byte reqId) =>
        Apply(QryOrderEmptyRspTemplate,
            new Edit(9, 2, InstanceField(e3Value)),
            new Edit(12, 1, EscapedValue(reqId))
        );

    /// <summary>
    /// 查成交空应答（13B payload / 17B 线上——2026-10-24 真前置实抓逐字节：请求 op `E104E28002`、
    /// 应答 op `E104E28003`，帧形与查报单空表同构：`0103344C E104E28003 E3 01 E7 04`）。
    /// 账户当日无成交时真前置回此帧；SDK 随即以「空记录 + isLast=true」完成查询（0 行）。
    /// </summary>
    internal static readonly byte[] QryTradeEmptyRspTemplate = H("0103344CE104E28003E301E704");

    /// <summary>查成交空应答（E3 值槽 = offset 10、E7 reqId 值槽 = offset 12——与查报单同槽位）。</summary>
    internal static byte[] BuildQryTradeEmptyRsp(int e3Value, byte reqId) =>
        Apply(QryTradeEmptyRspTemplate,
            new Edit(9, 2, InstanceField(e3Value)),
            new Edit(12, 1, EscapedValue(reqId))
        );

    // ===== 帧 查交易所（真前置 2026-10-24 实抓：请求 op `E104E2802A`（26B）、应答 op `E104E2802B`
    // （255B payload，**1 帧 N 条记录**）。头 = `E1 04 E2 80 2B | E3 <流水> | E1 <条数> | 01 C2 | E3 <reqId>`；
    // 记录 = `E1 01 | E1 47 <ExchangeID> | E1 <简称槽> | E1 <GBK 全称> | E1 <'0'/'1'> | E1 31 | [E2/E3 30 E1 31] | EF EF | E7/E8/E9 <'0'/'1'>`
    // （真帧 6 条里 E2/E3 组时有时无，尾部随名称长度变；本前置按名称长度二选一，见 BuildQryExchangeRsp 备注）。
    /// <summary>查交易所应答（<c>OnRspQryExchange</c>）：1 帧 N 条记录，字段以 <c>E1</c> 分隔。</summary>
    /// <remarks>
    /// 2026-10-24 真前置实抓（255B payload / 6 条）逐字节逆向后定案的三条硬约束——违反任一条，SDK 会静默丢记录或把字段读串：
    /// <list type="number">
    /// <item><b>ExchangeID + 简称槽 = 7 字节</b>：真帧六家分别 CFFEX5+2、SHFE4+3、DCE3+4、CZCE4+3、INE3+4、GFEX4+3。
    /// 简称槽内容是简称的前若干**未转义**字节（不够时续全称前缀；可在汉字中间截断，转义膨胀不算宽度）——SDK 不读它，只用来把名称顶到固定偏移 13。实测 ID 长度 2/4/6 配固定 4 字节简称会整帧错位。</item>
    /// <item><b>记录总长 39 或 41 字节</b>（真帧：名称 14B ⇒ 39、18B ⇒ 39、20B ⇒ 41）。名称过短（如 10B/12B/16B）时记录只有 31~37 字节，SDK 丢该条并连带丢后续记录。</item>
    /// <item><b>尾部形态与名称长度配套</b>：名称 ≤16B 用短尾 <c>E1&lt;prop&gt; E1 31 E3 30 E1 31 EF EF E7&lt;prop&gt;</c>（12B），
    /// &gt;16B 用长名尾 <c>E1&lt;prop&gt; E1 31 EF EF E9&lt;prop&gt;</c>（8B）。塞进多余的字段组会把 Property 读成 0。</item>
    /// </list>
    /// 边界：ExchangeID 需 ≤ 6 字符（真帧 3~5），更长的 ID 无法凑出 7 字节槽，本帧不出该条；
    /// 名称建议 7~10 个汉字（14~20B），更长的组合尚未在真前置上验证。
    /// </remarks>
    internal static byte[] BuildQryExchangeRsp(int counter, byte reqId,
        IReadOnlyList<(string ExchangeId, string ShortName, string FullName, byte Property)> rows)
    {
        var body = new List<byte>();
        body.AddRange(H("0103344CE104E2802B"));
        body.AddRange(Cat(H("E3"), EscapedValue(MinimalInt(counter))));
        var usable = rows.Where(r => System.Text.Encoding.ASCII.GetByteCount(r.ExchangeId) < QryExchangeIdShortWidth).ToList();
        body.AddRange([0xE1, (byte)usable.Count]);        // 条数槽（真帧 6 条 ⇒ 0x06）
        body.AddRange(H("01C2"));
        body.AddRange(Cat(H("E3"), EscapedValue(reqId)));
        foreach (var r in usable)
        {
            var gbk = Gbk(r.FullName);                     // GBK 原始字节（未转义）
            body.AddRange(H("E101E147"));                  // 记录起始 + ExchangeID 字段标记
            body.AddRange(A(r.ExchangeId));
            var padWidth = QryExchangeIdShortWidth - System.Text.Encoding.ASCII.GetByteCount(r.ExchangeId);
            body.AddRange(H("E1"));                        // 简称槽：按**未转义**字节补足 7（内容被 SDK 丢弃）
            body.AddRange(EscapedValue(Cat(Gbk(r.ShortName), gbk).Take(padWidth).ToArray()));
            body.AddRange(H("E1"));
            body.AddRange(EscapedValue(gbk));
            body.AddRange([0xE1, r.Property]);
            body.AddRange(gbk.Length <= 16 ? H("E131E330E131EFEFE7") : H("E131EFEFE9"));
            body.AddRange([r.Property]);
        }
        return body.ToArray();
    }

    /// <summary>查交易所应答里 ExchangeID 与「简称槽」的合计宽度（真帧实测恒为 7 字节）。</summary>
    private const int QryExchangeIdShortWidth = 7;

    /// <summary>交易所中英文名（真前置 GBK 全称；仿真自有交易所直接用 id）——查交易所应答的名称来源。</summary>
    internal static (string Short, string Full) ExchangeNameOf(string exchangeId) => exchangeId.ToUpperInvariant() switch
    {
        "DCE" => ("大连", "大连商品交易所"),
        "SHFE" => ("上海", "上海期货交易所"),
        "CZCE" => ("郑州", "郑州商品交易所"),
        "CFFEX" => ("中国", "中国金融期货交易所"),
        "INE" => ("上海", "上海国际能源交易中心"),
        "GFEX" => ("广州", "广州期货交易所"),
        // 仿真自有交易所（如 AETHER）：名称槽用 GBK 中文，ID 才是身份；ASCII 名未验证，不冒险
        _ => ("合成", "合成期货交易所"),   // 7 个汉字 = 14B，与真帧 DCE/SHFE/CZCE 同形（记录 39B）
    };

    /// <summary>
    /// 查结算单空应答（13B payload / 17B 线上——2026-10-24 真前置实抓逐字节：请求 op `E104E2803C`、
    /// 应答 op `E104E2803D`，帧形与查报单/查成交空表同构：`0103344C E104E2803D E3 01 E7 05`）。
    /// SimNow 测试账户当日无结算单，真前置就回这一帧（客户端 0 条、立即完成）。
    /// </summary>
    internal static readonly byte[] QrySettlementInfoEmptyRspTemplate = H("0103344CE104E2803DE301E707");

    /// <summary>查结算单空应答（E3 值槽 = offset 10、E7 reqId 值槽 = offset 12——与其它空表同槽位）。</summary>
    internal static byte[] BuildQrySettlementInfoEmptyRsp(int e3Value, byte reqId) =>
        Apply(QrySettlementInfoEmptyRspTemplate,
            new Edit(9, 2, InstanceField(e3Value)),
            new Edit(12, 1, EscapedValue(reqId))
        );

    // ===== 帧 ⑲ 查成交应答·非空单行（284B payload——2026-10-24 真前置实抓：账户 1 笔 m2701 成交
    // 1@3279 TradeID=2344。真前置 0 行回 13B 空表、≥2 行走列式二进制；本前置恒用本 tag 记录帧逐行。
    // 槽位按 CTP TThostFtdcTradeField 的字段序解码并与探针实证互证：流水@9 / reqId 值@17 / Broker@23,41,97,168 /
    // Exchange@28,73,198 / Investor@34,66 / UserID@66 / Instrument@46,113,192,202,208,273 / OrderRef@53 /
    // TradeID@77 / Direction@83 / OffsetFlag@120 / HedgeFlag@121 / Price@122(3B PriceSlot) / Volume@126 /
    // TradeDate@127 / TradeTime@136 / TradingDay@176。
    // 偏移是本帧固定值：与报单帧不同构（报单 reqId 值在 @16，本帧 @16 是 E3 标签）——照抄报单帧的偏移
    // 会让整帧被 SDK 丢弃（30s 超时）。
    // 抓包原始账号/会话已换中性等宽值（用户号→`000000`、成交号→`0000`、复合号→`9999000000`）。=====
    internal static readonly byte[] QryTradeRspTemplate = H(
        "0103344CE104E28003E319E10101E0EFE308040901E0EB39393939E1444345E139E1303030303030E139393939E26D323730" +
        "31EFEB303030303030303030303032E1303030303030EA444345E630303030EFE230202020202020203133333132E9393939" +
        "39E739393939303030303030E26D32373031EFEB303140A99EE8013230323631303232E132303A30393A3338E130E1393939" +
        "39616263EE202020202020202020363930E139393939EFEF03C23230323631303233E401E294D530EFE26D32373031E14443" +
        "45E16D32373031E16D32373031E16DE131E132303237E131E131303030E131E131303030E131E13130E131E1323032363031" +
        "3139E13230323630313139E13230323730313135E132E16D32373031EFEFEFEFEFE1"
    );

    /// <summary>成交号槽宽（探针实证：本记录帧内任何槽**改宽即整帧被 SDK 丢弃**——与 ⑯ 的状态消息槽同款纪律）。</summary>
    private const int QryTradeIdWidth = 4;

    /// <summary>成交号定宽填充（左补零；超宽截尾——同日内超过 10^(宽度) 笔会撞号，如实标注）。</summary>
    private static byte[] TradeIdSlot(string tradeId)
    {
        var v = tradeId.Length == 0 ? "0" : tradeId;
        return A(v.Length >= QryTradeIdWidth ? v[^QryTradeIdWidth..] : v.PadLeft(QryTradeIdWidth, '0'));
    }

    /// <summary>
    /// ⑲ 查成交应答·非空单行（真前置 tag 记录帧）。与 ⑯ 查报单记录帧同一族（同一 tag 语法、同一套等宽纪律）。
    /// <para>
    /// <b>成交号必须定宽</b>：2026-10-24 E2E 实测——1 位成交号写成 4 位槽的 1 字节后，整帧被 SDK 静默丢弃
    /// （查询 0 行、`isLast=true`，与 ⑯ 状态消息槽变宽的表现一致）。故走 <see cref="TradeIdSlot"/> 定宽左补零。
    /// </para>
    /// </summary>
    internal static byte[] BuildQryTradeRsp(int instance, byte reqId, string brokerId, string investorId,
        string instrumentId, string exchangeId, string orderRef, string tradeId,
        char direction, char offsetFlag, double price, int volume,
        string tradeDate, string tradeTime, string tradingDay) =>
        Apply(QryTradeRspTemplate,
            new Edit(9, 2, InstanceField(instance)),
            new Edit(17, 1, EscapedValue(reqId)),   // 本帧 @16 是 E3 标签本身，值在 @17（报单帧 @16 即值——两帧不一致）
            new Edit(23, 4, F(brokerId, 4)),
            new Edit(28, 3, F(exchangeId, 3)),
            new Edit(34, 6, F(investorId, 6)),
            new Edit(41, 4, F(brokerId, 4)),
            new Edit(46, 5, F(instrumentId, 5)),
            new Edit(53, 12, F(orderRef, 12)),
            new Edit(66, 6, F(investorId, 6)),
            new Edit(73, 3, F(exchangeId, 3)),
            new Edit(77, 4, TradeIdSlot(tradeId)),
            new Edit(83, 1, [(byte)direction]),
            new Edit(97, 4, F(brokerId, 4)),
            new Edit(113, 5, F(instrumentId, 5)),
            new Edit(120, 1, [(byte)offsetFlag]),
            new Edit(122, 3, PriceSlot(price)),
            new Edit(126, 1, MinimalInt(volume)),
            new Edit(127, 8, F(tradeDate, 8)),
            new Edit(136, 8, F(tradeTime, 8)),
            new Edit(168, 4, F(brokerId, 4)),
            new Edit(176, 8, F(tradingDay, 8)),
            new Edit(192, 5, F(instrumentId, 5)),
            new Edit(198, 3, F(exchangeId, 3)),
            new Edit(202, 5, F(instrumentId, 5)),
            new Edit(208, 5, F(instrumentId, 5)),
            new Edit(273, 5, F(instrumentId, 5))
        );

    // ===== 帧 ⑯ 查报单应答·非空单行（442B payload——2026-10-24 真前置实抓：账户 1 张已撤 m2705 报单。
    // 真前置按行数选格式：1 行 → 本 tag 记录帧；≥2 行 → 列式二进制（不逆向）。多行 = 本帧重复（与持仓同路径：
    // tag 帧恒 isLast=true，多行靠 wrapper 静默期收尾）。记录区字段经逐字节打补丁探针定位（改哪个字节
    // 客户端哪个字段跟着变），可比对推送族 OnRtnOrder（同一记录 schema，整体 +1 偏移）。
    // 抓包原始账号/会话/IP 已换成中性等宽值（用户号→`000000`、IP→`192.168.000.001`、会话号→11223344）；
    // 运行期再由 Build 覆盖 broker/user/流水/合约/价量/时间等字段——字节宽度与抓包完全一致。=====
    internal static readonly byte[] QryOrderRspTemplate = H(
        "0103344CE104E28001E301E1010353E3040401034F39393939E7303030303030E76D32373035EFEB30303030303030303030" +
        "3032E1303030303030EA323130E431E440AA90E80133E931E30131E830E439393939616263E139393939E131E131E1393939" +
        "E5202020202020202020323837E1444345E16D323730E139393939E131E3C9E139393939303030303030E16D32373035EFEB" +
        "39393939616263E139393939E131E131E1393939E40134E3013230323631303233E401E14F52443A3030303030303030E830" +
        "3530E7013230323631303234E131373A32333A3439EFEFEFEFEFE10111223344EB34393AD2D1B3B7B5A5B1A8B5A5B1BBBEDC" +
        "BEF84443453ABCDBB8F1B3ACB3F6D5C7CDA3B0E0E5EFEFEFE444ABE0E3977FE2F0509503E64AA9E17FE2C0CBCBF9997FE2F0" +
        "509503EE7FE22044ABE0E3EFEFE53139322E3136382E3030302E303031EFE76D32373035EFEFEFEFEFE16D32373035E16DE1" +
        "31E132303237E135E131303030E131E131303030E131E13130E131E13230323630353230E13230323630353230E132303237" +
        "30353139E13230323730353139E13230323730353234E13139322E3136382E3030302E303031EFEFE401"
    );

    /// <summary>⑯ 查报单记录帧的状态消息槽宽（GBK 转义后字节数——抓包槽宽，定宽纪律：改宽整帧被 SDK 丢弃）。</summary>
    private const int QryOrderMsgWidth = 38;

    /// <summary>
    /// ⑯ 查报单应答·非空单行（真前置 tag 记录帧）。字段槽位由逐字节探针定死（2026-10-24）：
    /// 流水 @40(12)、方向三元组 @60(3)、价格 @66(3 = <see cref="PriceSlot"/>)、量+TimeCondition @70(2 = <see cref="VolumeEdit"/>)、
    /// 交易所 @114(3)、合约 @331(5 及 33/118/143/342 四个冗余副本)、状态下标 <paramref name="orderStatus"/> @200、
    /// 报单时间 @213(8)、交易日 @175 / 报单日期 @204、状态消息 @232（`EB` + GBK 转义 run，<b>定宽 38B</b>——
    /// 2026-10-24 探针实证：消息槽改等宽可解、改变长则整帧被 SDK 丢弃 ⇒ 超长按 GBK 字节截断、不足补空格）。
    /// <para>
    /// 已知边界（如实标注，不静默）：<c>VolumeTraded</c>/<c>VolumeTotal</c> 的槽位落在消息之后的数值 run 里
    /// （未逐字节定位），本帧沿用抓包值 0/1 —— 部分成交的报单在查报单里仍显示「成交 0 手」。成交回报推送
    /// （OnRtnTrade）不受影响；客户端以推送收敛成交数，查询行只作对账底稿。
    /// </para>
    /// </summary>
    internal static byte[] BuildQryOrderRsp(int instance, byte reqId, string brokerId, string investorId,
        string instrumentId, string exchangeId, string orderRef, char priceType, char direction, char offsetFlag,
        double limitPrice, int volume, int volumeTotal, char timeCondition, char orderStatus, string statusMsg,
        string tradingDay, string insertTime) =>
        Apply(QryOrderRspTemplate,
            new Edit(9, 2, InstanceField(instance)),
            new Edit(16, 1, EscapedValue(reqId)),
            new Edit(21, 4, F(brokerId, 4)),
            new Edit(26, 6, F(investorId, 6)),
            new Edit(33, 5, F(instrumentId, 5)),
            new Edit(40, 12, F(orderRef, 12)),
            new Edit(53, 6, F(investorId, 6)),
            new Edit(60, 3, A($"{priceType}{direction}{offsetFlag}")),
            new Edit(66, 3, PriceSlot(limitPrice)),
            VolumeEdit(70, volume, timeCondition),
            new Edit(88, 4, F(brokerId, 4)),
            new Edit(114, 3, F(exchangeId, 3)),
            new Edit(118, 4, F(instrumentId, 4)),
            new Edit(123, 4, F(brokerId, 4)),
            new Edit(143, 5, F(instrumentId, 5)),
            new Edit(158, 4, F(brokerId, 4)),
            new Edit(331, 5, F(instrumentId, 5)),
            new Edit(342, 5, F(instrumentId, 5)),
            new Edit(175, 8, F(tradingDay, 8)),   // 定宽：空交易日也不许缩帧
            new Edit(200, 1, [(byte)orderStatus]),
            // 剩余未成交量（2026-10-24 整帧 patch-scan 定案：本槽 = payload @203，改它客户端 VolumeTotal 就跟着变）。
            // 同 @54 持仓量槽：1B 捕获宽，量 >127 时 MinimalInt 2B 撑宽（tag 终结的 int run 允许伸缩，未实测大值边界）。
            new Edit(203, 1, MinimalInt(volumeTotal)),
            new Edit(204, 8, F(tradingDay, 8)),
            new Edit(213, 8, F(insertTime, 8)),
            new Edit(232, 39, Cat(H("EB"), EscapedGbkFixed(statusMsg.Length == 0 ? " " : statusMsg, QryOrderMsgWidth)))
        );

    // ===== 查报单二进制应答（807B payload——2026-10-03 SimNow 实抓完整帧。
    // 路由 01 04 21 03 ≠ tag 路由 01 03 34 4C ≠ LZ4 路由 01 04 FA 0F——第三种格式（列式共享字段）。
    // 结构：路由(4B) + 固定头(12B) + 80族流水(4B BE @16) + 固定(4B) + reqId(4B BE @24) + 记录数据(779B)。
    // 记录内字段编码与 tag 格式同族（trimmed double @82 = 3328 = 价格、minimal int、ASCII 字符串）
    // 但为列式（m2701/broker/user 只出现一次，6 条记录共享 header 字段，每条 ~130B）。
    // header-only 28B 试验失败（SDK 解析失步 → 后续 rc=-2）——完整帧是唯一可行形状。
    // <b>2026-10-24 实测：整帧也过不了 SDK 6.7.2</b>——原样作为查报单应答发出 ⇒ 客户端立即断开会话；
    // 把首 4 字节改成会话 cookie（01 03 34 4C，= tag 帧同款路由）⇒ 连接不再立即断，但 `OnRspQryOrder`
    // 始终不派发、客户端 30s 超时。两次试探都没有解出任何一行 ⇒ 这段 812B（注释旧写 807B，实际 812B）
    // 既不是可直接回放的形状，也不足以反推列式布局。仅留作 2026-10-03 抓包标本，<b>不要接线</b>；
    // 注释里「回放会显示 SimNow 报单」是未验证的旧说法，已撤销。补非空报单只能等真前置重抓。=====
    internal static readonly byte[] QryOrderBinaryRspTemplate = H(
        "010421034C00F20E34430004000080010000000300010353000000070401034F39393939000100623234373136330C006300"
        + "6D323730310C000F020000163001001A323900600000003230300800013F002040AA0B0001020014015C00021B0011012000"
        + "000200012E000F020006132001008131373036004443459B001100B80000420011C90B000083002134311B000015000F0200"
        + "0400350031636166320040003100310900002B00019A008101323032363039338E0013017500502038363636230002020005"
        + "C200062D008230323A35323A353521000F02000C0481000F270007A7094B00000001FB79480C240067CEB4B3C9BDBB11000F"
        + "020032B231C10F507F0000F0001D025000B95BFF007F0000C09B3A3A511800040200747F00002031C10F0F000F020008FF"
        + "003138302E3135382E3233322E3138372A00030F63020C0F02001F025100806D00310032303237E101383030300700021A00"
        + "503630313139230005090050373031313512000809002F3230C70013080200170167031F0467033B1F3467030B24A9FE8800"
        + "01CD02020200000B030F67031F2F31326703611037AD000302000F67030435363A331A000F02000A0F6703101052670347FC"
        + "5B486E49000F6703542F5C096703FF2E170367031F0567033B1F3367030B24AAC830020F67032F1F3167034501C2060FCE06"
        + "0411371D00010200001D05300000010C00052D0000CE0640363A323815000F0200085038303236553E07014F070A290011FF"
        + "01008FEF7FFF000000095067030040C8ABB2BFD0068FB1A8B5A5D2D1CCE1DA06330DCE062F5C076703FF2E170267031F0667"
        + "031B1F3067033B0F350A0C0FCE06391F30670345008C0606080A00E90207350A04550B0FCE06091F32350A36104F7E000FCE"
        + "06641F056703FF2E08350A1F0767033B0F350A0C0F9C0D391F3867036207A906008F0301020006940300CE0635333A301A00"
        + "0F02000A0F350A10104E67030F9C0D632F5C036703FF2E3503344C03111F0867033B0F350A0D14CA79020F350A2E2F303767"
        + "034501C20605080A00E90207CE0606C20F0F350A0537323A358C030F02000220383052120F670310104C7E000B67030F350A"
        + "551F016703FF2A500000000002"
    );

    /// <summary>查报单二进制应答（完整 807B 回放——seq @16 / reqId @24 为 4B BE 编辑）。</summary>
    internal static byte[] BuildQryOrderBinaryRsp(int seq, int reqId) =>
        Apply(QryOrderBinaryRspTemplate,
            new Edit(16, 4, [(byte)(seq >> 24), (byte)(seq >> 16), (byte)(seq >> 8), (byte)seq]),
            new Edit(24, 4, [(byte)(reqId >> 24), (byte)(reqId >> 16), (byte)(reqId >> 8), (byte)reqId])
        );

    /// <summary>M1 ⑩ verbatim 会话身份（FrontID+SessionID，5B）——推送/回显帧 Edit 用（D6：与 ⑩ 同源）。</summary>
    internal static readonly byte[] SessionFrontIdSessionId = H("012DAF7B02");

    // ===== M2 帧 OnRtnOrder '3'（385B payload：未成交在队推送——F001） =====
    internal static readonly byte[] RtnOrder3Template = H(
        "0103344CE102E2F001E306E1010353E40401034F39393939E7323437313633E76D32373031EFEB303030303030303030303032E1323437313633EA323030E431E440A9C8E80133E931E30131E830EFEE202020202020202034363536E1444345E16D323730E139393939E131E3C9E139393939323437313431E16D32373031EFEB39393939636166E139393939E131E131E1393939E40133E3013230323630393239E401202020202020202039353037EA33E8013230323630393239E131303A35303A3437EFEFE739393939636166EFEC1341E301C8E0E69C40EBCEB4B3C9BDBBEFEFEFEFEFE510E710E86558E17FE3FDE66F70369F8C7FECFFFFFFE39CEFEFE53138302E3135382E3233322E313837EFE76D32373031EFEFEFEFEFE16D32373031E16DE131E132303237E131E131303030E131E131303030E131E13130E131E13230323630313139E13230323630313139E13230323730313135E13230323730313135E13230323730313230E13138302E3135382E3233322E313837EFEFE401"
    );

    /// <summary>OnRtnOrder '3'（未成交在队——受理/挂单状态推送）。价格/量槽等宽（PriceSlot/VolumeEdit——
    /// 变宽即失步，见 PriceSlot 注释）。</summary>
    internal static byte[] BuildRtnOrder3(int instance, string userId, string brokerId, string instrumentId, string orderRef, char priceType, char direction, char offsetFlag, double limitPrice, int volume, char timeCondition, string tradingDay) =>
        Apply(RtnOrder3Template,
            new Edit(9, 2, InstanceField(instance)),
            new Edit(39, 12, F(orderRef, 12)),
            new Edit(59, 3, A($"{priceType}{direction}{offsetFlag}")),
            new Edit(65, 3, PriceSlot(limitPrice)),
            VolumeEdit(69, volume, timeCondition),
            new Edit(25, 6, F(userId, 6)),
            new Edit(52, 6, F(userId, 6)),
            new Edit(20, 4, F(brokerId, 4)),
            new Edit(102, 4, F(brokerId, 4)),
            new Edit(111, 4, F(brokerId, 4)),
            new Edit(129, 4, F(brokerId, 4)),
            new Edit(137, 4, F(brokerId, 4)),
            new Edit(200, 4, F(brokerId, 4)),
            new Edit(32, 5, F(instrumentId, 5)),
            new Edit(97, 4, F(instrumentId, 4)),
            new Edit(122, 5, F(instrumentId, 5)),
            new Edit(274, 5, F(instrumentId, 5)),
            new Edit(285, 5, F(instrumentId, 5)),
            new Edit(154, 8, A(tradingDay)),
            new Edit(180, 8, A(tradingDay))
        );

    // ===== M2 帧 OnRtnOrder '0'（399B payload：全部成交推送——F001；VolumeTraded 保留捕获值 1，>1 手的全成回报成交量失真（边界）
// 2026-10-24 复核：同族的查报单记录帧经整帧 patch-scan + 阳性对照**证否不含 VolumeTraded 槽**；推送族 '3' 模板
// 扫了 140 个候选字节（多数补丁直接破帧 → NO-PUSH）仍未定位 ⇒ 保持模板值，成交数一律靠 OnRtnTrade 推送收敛。） =====
    internal static readonly byte[] RtnOrder0Template = H(
        "0103344CE102E2F001E309E1010353E40401034F39393939E7323437313633E76D32373031EFEB303030303030303030303033E1323437313633EA323030E431E440AA90E80133E931E30131E830EFEE202020202020202034363537E1444345E16D323730E139393939E131E3C9E139393939323437313431E16D32373031EFEB39393939636166E139393939E131E131E1393939E40130E43230323630393239E401202020202020202039353038E9303030E301E43230323630393239E131303A35303A3530EFEFE739393939636166EFEC1342E301C8E0E69C40EBC8ABB2BFB3C9BDBBB1A8B5A5D2D1CCE0E1BDBBEFEFEFEFE810E710E8655AE17FE3FDE66F70369F8C7FECFFFFFFE39CEFEFE53138302E3135382E3233322E313837EFE76D32373031EFEFEFEFEFE16D32373031E16DE131E132303237E131E131303030E131E131303030E131E13130E131E13230323630313139E13230323630313139E13230323730313135E13230323730313135E13230323730313230E13138302E3135382E3233322E313837EFEFE402"
    );

    /// <summary>OnRtnOrder '0'（全部成交推送）。价格/量槽等宽（PriceSlot/VolumeEdit）。</summary>
    internal static byte[] BuildRtnOrder0(int instance, string userId, string brokerId, string instrumentId, string orderRef, char priceType, char direction, char offsetFlag, double limitPrice, int volume, char timeCondition, string tradingDay) =>
        Apply(RtnOrder0Template,
            new Edit(9, 2, InstanceField(instance)),
            new Edit(39, 12, F(orderRef, 12)),
            new Edit(59, 3, A($"{priceType}{direction}{offsetFlag}")),
            new Edit(65, 3, PriceSlot(limitPrice)),
            VolumeEdit(69, volume, timeCondition),
            new Edit(25, 6, F(userId, 6)),
            new Edit(52, 6, F(userId, 6)),
            new Edit(20, 4, F(brokerId, 4)),
            new Edit(102, 4, F(brokerId, 4)),
            new Edit(111, 4, F(brokerId, 4)),
            new Edit(129, 4, F(brokerId, 4)),
            new Edit(137, 4, F(brokerId, 4)),
            new Edit(202, 4, F(brokerId, 4)),
            new Edit(32, 5, F(instrumentId, 5)),
            new Edit(97, 4, F(instrumentId, 4)),
            new Edit(122, 5, F(instrumentId, 5)),
            new Edit(288, 5, F(instrumentId, 5)),
            new Edit(299, 5, F(instrumentId, 5)),
            new Edit(153, 8, A(tradingDay)),
            new Edit(182, 8, A(tradingDay))
        );

    /// <summary>
    /// OnRtnOrder 终态（'5' 已撤销 / '2' 部成后作废 / '4' 未成交作废——'3' 模板 + 状态槽(177)/消息槽(219) 等宽编辑）。
    /// 客户端（CtpBrokerage tracker）按状态字节收敛订单生命周期——掩成 '3' 会让已撤/已废单成僵尸。
    /// </summary>
    internal static byte[] BuildRtnOrderTerminated(int instance, string userId, string brokerId, string instrumentId, string orderRef, char priceType, char direction, char offsetFlag, double limitPrice, int volume, char timeCondition, string tradingDay, char status, string statusMsg) =>
        Apply(RtnOrder3Template,
            new Edit(9, 2, InstanceField(instance)),
            new Edit(39, 12, F(orderRef, 12)),
            new Edit(59, 3, A($"{priceType}{direction}{offsetFlag}")),
            new Edit(65, 3, PriceSlot(limitPrice)),
            VolumeEdit(69, volume, timeCondition),
            new Edit(25, 6, F(userId, 6)),
            new Edit(52, 6, F(userId, 6)),
            new Edit(20, 4, F(brokerId, 4)),
            new Edit(102, 4, F(brokerId, 4)),
            new Edit(111, 4, F(brokerId, 4)),
            new Edit(129, 4, F(brokerId, 4)),
            new Edit(137, 4, F(brokerId, 4)),
            new Edit(200, 4, F(brokerId, 4)),
            new Edit(32, 5, F(instrumentId, 5)),
            new Edit(97, 4, F(instrumentId, 4)),
            new Edit(122, 5, F(instrumentId, 5)),
            new Edit(274, 5, F(instrumentId, 5)),
            new Edit(285, 5, F(instrumentId, 5)),
            new Edit(154, 8, A(tradingDay)),
            new Edit(180, 8, A(tradingDay)),
            new Edit(177, 1, [(byte)status]),
            new Edit(219, 6, EscapedGbkFixed(statusMsg, 6))
        );

    /// <summary>按 GBK 字节宽截断（消息槽 6B——汉字 2B/个；调用方传 ≤3 汉字时为直通）。</summary>
    private static string TruncateGbk(string text, int maxBytes)
    {
        var used = 0;
        for (var i = 0; i < text.Length; i++)
        {
            used += Encoding.GetEncoding("GBK").GetByteCount([text[i]]);
            if (used > maxBytes) return text[..i];
        }
        return text;
    }

    /// <summary>GBK 文本 E0 转义并<b>锁定输出宽度</b>：逐字符累加（转义 +1/字节计入预算），超预算即止、
    /// 不足右补空格——恒 width 字节。直接 TruncateGbk 再 EscapedGbk 会因转义膨胀撑破槽（值内
    /// E0..EF/FE 字节前插 E0——每字节 +1）⇒ 帧撑失步。</summary>
    private static byte[] EscapedGbkFixed(string text, int width)
    {
        var body = new List<byte>();
        for (var i = 0; i < text.Length; i++)
        {
            var raw = Encoding.GetEncoding("GBK").GetBytes([text[i]]);
            var cost = raw.Length + raw.Count(b => b is (>= 0xE0 and <= 0xEF) or 0xFE);
            if (body.Count + cost > width) break;
            foreach (var b in raw)
            {
                if (b is (>= 0xE0 and <= 0xEF) or 0xFE) body.Add(0xE0);
                body.Add(b);
            }
        }
        while (body.Count < width) body.Add((byte)' ');
        return body.ToArray();
    }

    /// <summary>量槽 Edit（F001 OnRtnOrder 的 [量+TimeCondition] run，模板 @69 宽 2B）：量 ≤255 走捕获形状
    /// （1B 量 + TC）；≥256 切 E7 + 2B 量 + TC（镜像 SDK ⑲ 请求侧自编码惯例——量 tag 随宽度变，E8=1B/E7=2B；
    /// 响应方向未实测——bot 量级 ≤ MaxOrderVolume 远达不到，防御性对齐）。</summary>
    private static Edit VolumeEdit(int offset, int volume, char timeCondition) => volume <= 255
        ? new Edit(offset, 2, Cat(MinimalInt(volume), A(timeCondition.ToString())))
        : new Edit(offset - 1, 3, Cat(H("E7"), MinimalInt(volume), A(timeCondition.ToString())));

    // ===== M2 帧 OnRtnTrade（283B payload：成交回报推送——F002） =====
    internal static readonly byte[] RtnTradeTemplate = H(
        "0103344CE102E2F002E30AE10101E0EFE4040901E0EB39393939E1444345E139E1323437313633E139393939E26D32373031EFEB303030303030303030303033E1323437313633EA444345E635333936EFE230202020202020202039353038E939393939E739393939323437313431E26D32373031EFEB303140AA3AE8013230323630393239E131303A35303A3530E130E139393939636166EE202020202020202034363537E139393939EFEF13433230323630393239E401E2655A30EFE26D32373031E1444345E16D32373031E16D32373031E16DE131E132303237E131E131303030E131E131303030E131E13130E131E13230323630313139E13230323630313139E13230323730313135E132E16D32373031EFEFEFEFEFE1"
    );

    /// <summary>OnRtnTrade（成交回报推送——<b>开平槽 = offset 119（EB 价格 run 的 "01" 前缀首字节——
    /// 2026-10-20 实验室钉死：编辑该字节客户端开平回显随之翻转）；方向槽候选 = offset 82（E2+"0"+空格
    /// 段首字节——④ 的 Trade.Direction 本就是报单归属方的值，⑤ 此前直接丢弃致恒回 BuyOpen ⇒ ⑥ 持仓缓存
    /// （开平决策数据源）单调膨胀 ⇒ 仓位失控；若本槽实验否证则方向回显为未逆向边界，功能路径 ⑥ 已绕开）</b>。
    /// 价格槽 PriceSlot 等宽（变宽即失步）；<b>量槽 = offset 125（模板 `E8 01` 的值字节——2026-10-17 修正：
    /// 旧版编辑 offset 184（`E4 01`，无关字段——量编辑写错位还污染该字段），SDK 从 E8 后读量 ⇒ 恒读模板
    /// 原值 1——多手成交全被读成 1 手 ⇒ 客户端「报单全成但成交回报缺 N 手」高频幻影补齐（双 bot 实测）；
    /// E2E 此前只测 1 手成交（01=1 恰好吻合）掩盖了偏移错误）</b>；量槽 1B——>255 手时 MinimalInt 2B 撑宽
    /// （bot 量级达不到，未实测边界——等宽纪律下撑宽即失步，如实标注）。</summary>
    internal static byte[] BuildRtnTrade(int instance, string userId, string brokerId, string instrumentId, string orderRef, string tradeId, char direction, char offsetFlag, double price, int volume, string tradingDay) =>
        Apply(RtnTradeTemplate,
            new Edit(9, 2, InstanceField(instance)),
            new Edit(52, 12, F(orderRef, 12)),
            new Edit(76, 4, F(tradeId, 4)),
            new Edit(119, 1, A(offsetFlag.ToString())),
            new Edit(82, 1, A(direction.ToString())),
            new Edit(121, 3, PriceSlot(price)),
            new Edit(125, 1, MinimalInt(volume)),
            new Edit(33, 6, F(userId, 6)),
            new Edit(65, 6, F(userId, 6)),
            new Edit(22, 4, F(brokerId, 4)),
            new Edit(40, 4, F(brokerId, 4)),
            new Edit(96, 4, F(brokerId, 4)),
            new Edit(101, 4, F(brokerId, 4)),
            new Edit(146, 4, F(brokerId, 4)),
            new Edit(167, 4, F(brokerId, 4)),
            new Edit(45, 5, F(instrumentId, 5)),
            new Edit(112, 5, F(instrumentId, 5)),
            new Edit(191, 5, F(instrumentId, 5)),
            new Edit(201, 5, F(instrumentId, 5)),
            new Edit(207, 5, F(instrumentId, 5)),
            new Edit(272, 5, F(instrumentId, 5)),
            new Edit(126, 8, A(tradingDay)),
            new Edit(175, 8, A(tradingDay))
        );

    // ===== M2 帧 ⑳-4001（164B payload：OnRspOrderInsert 拒绝——C# 层不可见但真实，复刻） =====
    internal static readonly byte[] OrderInsertRspErrTemplate = H(
        "0103344CE101E24001E303E10201E0E5E307E355E3944354503ACEDED0A7B5C445786368616E67654944D7D6B6CEA3ACC7E0EBCCE0EEC8E0EBD5FDC8B7B5C445786368616E67654944EFEFE304E1018839393939E7323437313633E7616732353132EFEA303030303030303030303032E1323437313633EA323030E431E440BEDCE80133E931E30131E830EFEFEFEFEFEFEFEFE8616732353132EFEFEFEFEFEFEFEFE401"
    );

    /// <summary>⑳-4001 拒绝（payload；会话层与 F003 各自 BuildFrame 后拼接一次写）。价格槽 PriceSlot 等宽。</summary>
    internal static byte[] BuildOrderInsertRspErr(int instance, byte reqId, int errorId, string errorMsg, string userId, string brokerId, string instrumentId, string orderRef, char priceType, char direction, char offsetFlag, double limitPrice, int volume, char timeCondition) =>
        Apply(OrderInsertRspErrTemplate,
            new Edit(9, 2, InstanceField(instance)),
            new Edit(100, 12, F(orderRef, 12)),
            new Edit(120, 3, A($"{priceType}{direction}{offsetFlag}")),
            new Edit(126, 3, PriceSlot(limitPrice)),
            new Edit(130, 2, Cat(MinimalInt(volume), A(timeCondition.ToString()))),
            new Edit(85, 6, F(userId, 6)),
            new Edit(113, 6, F(userId, 6)),
            new Edit(80, 4, F(brokerId, 4)),
            new Edit(92, 6, F(instrumentId, 6)),
            new Edit(148, 6, F(instrumentId, 6)),
            new Edit(17, 1, EscapedValue(reqId)),
            new Edit(20, 53, RspInfo(53, errorId, errorMsg))
        );

    // ===== M2 帧 ⑳-F003（163B payload：OnErrRtnOrderInsert——拒单在 C# 层唯一可见信号 → RspError） =====
    internal static readonly byte[] ErrRtnOrderInsertTemplate = H(
        "0103344CE102E2F003E301E10201E0E5E404E1018839393939E7323437313633E7616732353132EFEA303030303030303030303032E1323437313633EA323030E431E440BEDCE80133E931E30131E830EFEFEFEFEFEFEFEFE8616732353132EFEFEFEFEFEFEFEFE401E355E3944354503ACEDED0A7B5C445786368616E67654944D7D6B6CEA3ACC7E0EBCCE0EEC8E0EBD5FDC8B7B5C445786368616E67654944EFEFE3"
    );

    /// <summary>⑳-F003 报单错误回报（拒单 → RspError 事件）。价格槽 PriceSlot 等宽。</summary>
    internal static byte[] BuildErrRtnOrderInsert(int instance, int errorId, string errorMsg, string userId, string brokerId, string instrumentId, string orderRef, char priceType, char direction, char offsetFlag, double limitPrice, int volume, char timeCondition) =>
        Apply(ErrRtnOrderInsertTemplate,
            new Edit(9, 2, InstanceField(instance)),
            new Edit(41, 12, F(orderRef, 12)),
            new Edit(61, 3, A($"{priceType}{direction}{offsetFlag}")),
            new Edit(67, 3, PriceSlot(limitPrice)),
            new Edit(71, 2, Cat(MinimalInt(volume), A(timeCondition.ToString()))),
            new Edit(26, 6, F(userId, 6)),
            new Edit(54, 6, F(userId, 6)),
            new Edit(21, 4, F(brokerId, 4)),
            new Edit(33, 6, F(instrumentId, 6)),
            new Edit(89, 6, F(instrumentId, 6)),
            new Edit(107, 53, RspInfo(53, errorId, errorMsg))
        );

    // ===== M2 帧 ㉒-4007（112B payload：OnRspOrderAction——OrderActionAsync 阻塞等它） =====
    internal static readonly byte[] OrderActionRspTemplate = H(
        "0103344CE101E24007E303E10201A5E30AE355E3194354503AB3B7B5A5D5D2B2BBB5BDCFE0E0D3A6B1A8B5A5EFEFEFEE0404014839393939E7323437313633EB303030303030303030303034E801C8E0E69C40444345EFEC30EC323437313633EFEFEFEFEFEFEFEFEFEFEFEFEFEFEF04"
    );

    /// <summary>㉒-4007 撤单应答·失败（RspInfo ← 核心错误码/消息，等宽 25B——变宽即失步）。</summary>
    internal static byte[] BuildOrderActionRspErr(int instance, byte reqId, int errorId, string errorMsg, string userId, string brokerId, string orderRef) =>
        Apply(OrderActionRspTemplate,
            new Edit(9, 2, InstanceField(instance)),
            new Edit(64, 12, F(orderRef, 12)),
            new Edit(57, 6, F(userId, 6)),
            new Edit(90, 6, F(userId, 6)),
            new Edit(52, 4, F(brokerId, 4)),
            new Edit(16, 1, EscapedValue(reqId)),
            new Edit(19, 25, RspInfo(25, errorId, errorMsg))
        );

    /// <summary>㉒-4007 撤单应答·成功（RspInfo errorId=0 +「撤单成功」，等宽 25B——成功撤单不发 F004）。</summary>
    internal static byte[] BuildOrderActionRspOk(int instance, byte reqId, string userId, string brokerId, string orderRef) =>
        Apply(OrderActionRspTemplate,
            new Edit(9, 2, InstanceField(instance)),
            new Edit(64, 12, F(orderRef, 12)),
            new Edit(57, 6, F(userId, 6)),
            new Edit(90, 6, F(userId, 6)),
            new Edit(52, 4, F(brokerId, 4)),
            new Edit(16, 1, EscapedValue(reqId)),
            new Edit(19, 25, RspInfo(25, 0, "撤单成功"))
        );

    /// <summary>
    /// RspInfo 槽等宽编码：E3 + MinimalInt(errorId) + EscapedGbk(msg)，空格右补到槽宽、超长按 GBK 截断。
    /// <b>等宽纪律（2026-10-02 E2E 实证）</b>：旧 OK 路径把 25B 槽缩成 8B（E4+"D5FDC8B7" ASCII）——4007
    /// 应答本身 SDK 能解析（OnRspOrderAction 触发、ErrorID=0），但<b>其后的所有推送帧静默失效</b>（撤单 '5'
    /// 回报从不触发 OnRtnOrder——曾被误判为「合成 '5' 形状 SDK 不触发回调」；压掉 4007 即恢复推送）。
    /// 变宽即失步的又一实证，与 ⑳/F003 RspInfo 变长编辑不触发 RspError 同根因。
    /// </summary>
    private static byte[] RspInfo(int width, int errorId, string msg)
    {
        var id = MinimalInt(errorId);
        var budget = width - 1 - id.Length;   // E3 + id 之后的消息字节预算
        var raw = Encoding.GetEncoding("GBK").GetBytes(msg);
        if (raw.Length > budget) msg = TruncateGbk(msg, budget);
        var body = Cat(H("E3"), id, EscapedGbk(msg));
        return body.Length >= width ? body[..width]
            : Cat(body, new byte[width - body.Length].Select(_ => (byte)' ').ToArray());
    }

    // ===== M2 帧 ㉒-F004（125B payload：OnErrRtnOrderAction 撤单错误回报） =====
    internal static readonly byte[] ErrRtnOrderActionTemplate = H(
        "0103344CE102E2F004E30DE1020270E40405021339393939E7323437313633EB303030303030303030303034E801C8E0E69C40444345EFEC30EFEFEFEFEFEFEFEFE5323437313633EFEFEFEFEFEFEFEFEFEFEFEFEFEFEFEFEFEFEFEFEF04E355E3194354503AB3B7B5A5D5D2B2BBB5BDCFE0E0D3A6B1A8B5A5EFEFEFEE"
    );

    /// <summary>㉒-F004 撤单错误回报（payload；会话层与 4007 拼接一次写）。</summary>
    internal static byte[] BuildErrRtnOrderAction(int instance, int errorId, string errorMsg, string userId, string brokerId, string orderRef) =>
        Apply(ErrRtnOrderActionTemplate,
            new Edit(9, 2, InstanceField(instance)),
            new Edit(32, 12, F(orderRef, 12)),
            new Edit(25, 6, F(userId, 6)),
            new Edit(66, 6, F(userId, 6)),
            new Edit(20, 4, F(brokerId, 4)),
            new Edit(96, 25, RspInfo(25, errorId, errorMsg))
        );

    // ===== M3 帧 MdSubscribeRsp（183B payload：订阅应答——捕获 verbatim） =====
    /// <summary>
    /// 订阅应答抓包原文：帧头 + 4 条记录（m2701 / i2701 / ta2701 / IF2612）。
    /// <b>只作为字节形状来源</b>——绝不再整段回放（见 <see cref="BuildMdSubscribeRsp"/>）。
    /// 版式：0..30 = 帧头（含首条记录前的末字节 E9）；此后每 38B 一条记录，起点 31 / 69 / 107 / 145，
    /// 记录内 E6 定宽 6B 合约槽落在 +27（抓包四条记录的槽偏移实测一致）。
    /// </summary>
    internal static readonly byte[] MdSubscribeRspTemplate = H(
        "0103344CE44402E5050229E755E44354503A4E6F204572726F72EFEFEFEFE92441E17088B26E0BE170E270B16E0BE170E2B0B16E0BE170E201E66D32373031EFEFEFEFEFE12441E17088B26E0BE170E270B16E0BE170E2B0B16E0BE170E201E66932373031EFEFEFEFEFE12441E17088B26E0BE170E270B16E0BE170E2B0B16E0BE170E201E6746132373031EFEFEFEFEF2441E17088B26E0BE170E270B16E0BE170E2B0B16E0BE170E201E6494632363132EFEFEFEFEF"
    );

    private const int MdSubRspFirstRecord = 31;   // 帧头宽度 = 首条记录起点
    private const int MdSubRspRecordLen = 38;     // 单条记录宽度
    private const int MdSubRspIdSlot = 27;        // 记录内 6B 合约槽（E6 定宽）

    /// <summary>抓包 4 条记录的合约槽绝对偏移：31/69/107/145 + 27 = 58/96/134/172。</summary>
    private static readonly int[] MdSubRspIdOffsets =
        new[] { 0, 1, 2, 3 }.Select(k => MdSubRspFirstRecord + k * MdSubRspRecordLen + MdSubRspIdSlot).ToArray();

    /// <summary>
    /// 订阅应答：**按实际订阅集回填合约槽**，帧长保持抓包形状（183B），只改 4 个 6B 合约槽。
    /// <para>
    /// 2026-10-29 定案——旧版把 183B 抓包原样回放，等于每次订阅都向客户端宣告
    /// m2701/i2701/ta2701/IF2612 四条「订阅成功」：桌面端日志冒出 4 条从没请求过的合约应答，
    /// 而自己订阅的 ax001 一条应答都没有（E2E 里订阅的 m2701 恰好被抓包命中，才一直没暴露）。
    /// </para>
    /// <para>
    /// <b>为什么不能按请求数裁剪记录</b>（2026-10-29 实测两次）：把记录裁成 N 条后，官方 SDK
    /// 能解析出 N 条、isLast 也按位置判定正确，但**原生层会把同一帧无限重投**（2 条帧跑出 90 万次
    /// OnRspSubMarketData 回调）——与本项目「单帧变形 ⇒ native 无限重投」的既有结论同根
    /// （E4F101 快照 op、变宽 RspInfo 同族）。所以记录不动，只换槽内容：槽宽、长度、标记字节全不变。
    /// </para>
    /// <para>
    /// 槽位按请求序列轮转填充（1 个合约 ⇒ 全部槽同名；2 个 ⇒ a,b,a,b），绝不多出对方没请求的合约。
    /// 实测原生解析器按<b>槽内字符串长度</b>走记录边界：5 字符 ID 填进去时 183B 帧只走出 3 条回调
    /// （末条 isLast=true）。数量无所谓——要紧的是名字全来自本次请求、且不重投。
    /// </para>
    /// </summary>
    internal static byte[] BuildMdSubscribeRsp(IReadOnlyList<string> instruments)
    {
        if (instruments.Count == 0)
            throw new ArgumentException("订阅应答至少要有一个合约（无合约时不回应答）", nameof(instruments));
        var edits = new Edit[MdSubRspIdOffsets.Length];
        for (var i = 0; i < edits.Length; i++)
            edits[i] = new Edit(MdSubRspIdOffsets[i], 6, InstrumentSlot(instruments[i % instruments.Count]));
        return Apply(MdSubscribeRspTemplate, edits);
    }

    /// <summary>6B 合约槽（E6 定宽）——抓包的填充字节是 <c>EF</c>（本编码的空），不是空格；
    /// 超 6 字符按槽宽截断（与 broker/user 同一条等宽纪律）。</summary>
    private static byte[] InstrumentSlot(string instrumentId)
    {
        var slot = Enumerable.Repeat((byte)0xEF, 6).ToArray();
        A(instrumentId.Length > 6 ? instrumentId[..6] : instrumentId).CopyTo(slot, 0);
        return slot;
    }

    // ===== M3 帧 QryInstrumentRsp（421B payload：查合约 tag 应答——单行模板（InstrumentId/ExchangeId 等宽编辑；VolumeMultiple/PriceTick 等保留捕获 verbatim——板子只读 id/交易所） =====
    internal static readonly byte[] QryInstrumentRspTemplate = H(
        "0103344CE104E2802FE303E101025EE307E103025AEFEFE1494E45E173633237E1736332373036E1736332373036E17363E131E132EFEFEFEFE331E207E0EBE306E31EE301E201F4E301E203E0E83FB999999999999A3230323430353036E13230323430363033E13230323730353331E13230323730363031E13230323730363037E131E30132313FC5C28F5C28F5C33FC5C28F5C28F5C331EFEFE17FE0EFFFFFFFFFFFFFE13FF0E630736332373036E1494E45E1736332373036E1736332373036E17363E131E132303237E136E13330E131E1353030E131E131303030E1302E31E13230323430353036E13230323430363033E13230323730E1736332373036E17363E131E132303237E136E13330E131E1353030E131E131303030E1302E31E13230323430353036E13230323430363033E13230323730353331E13230323730363031E13230323730E17363E131E132303237E136E13330E131E1353030E131E131303030E1302E31E13230323430353036E13230323430363033E13230323730353331E13230323730363031E13230323730363037E131E131E17363EFEFEFEFEFE4"
    );

    /// <summary>查合约应答（板子数据源——instance/reqId 回显请求值；多合约=每合约一帧同写）。多行收集在 wrapper
    /// 静默期收尾（2026-10-05 定案：tag 帧恒 isLast=true 无 false 编码——见 CtpTrader.OnQueryItem）。</summary>
    internal static byte[] BuildQryInstrumentRsp(int instance, byte reqId, string instrumentId, string exchangeId) =>
        Apply(QryInstrumentRspTemplate,
            new Edit(9, 2, InstanceField(instance)),
            new Edit(16, 1, EscapedValue(reqId)),
            new Edit(33, 6, F(instrumentId, 6)),
            new Edit(40, 6, F(instrumentId, 6)),
            new Edit(170, 6, F(instrumentId, 6)),
            new Edit(181, 6, F(instrumentId, 6)),
            new Edit(188, 6, F(instrumentId, 6)),
            new Edit(251, 6, F(instrumentId, 6)),
            new Edit(24, 3, F(exchangeId, 3)),
            new Edit(177, 3, F(exchangeId, 3))
        );

    // ===== M3 帧 MdLoginRsp（62B payload：MD 登录应答——交易日编辑） =====
    internal static readonly byte[] MdLoginRspTemplate = H(
        "0103344CE43001E5020181E755E44354503A4E6F204572726F72EFEFEFEFE9100301243230323630393239EFEFEFEFEFEFEFEFEFEFEFEFEFEFEFEFEFEFEE"
    );

    /// <summary>MD 登录应答（凭据不校验——行情订阅无账户语义）。</summary>
    internal static byte[] BuildMdLoginRsp(string tradingDay) =>
        Apply(MdLoginRspTemplate,
            new Edit(35, 8, A(tradingDay))
        );

    // ===== M3 帧 MdTick（109B payload：单合约 E4F103 流式 tick——价格三槽 + 成交量槽写入，其余字段 verbatim） =====
    internal static readonly byte[] MdTickTemplate = H(
        "0103344CE4F103E504E1D2E42439E1866D32373031EFEB30323A32313A3139E53230323630393330E16D32373031EFEFEFEFEFE12433E11C40AA56E60C3DCE42192CC8E0E5E0E8E24142BF9B80E32434E11840AA54E7045140AA56E703282481E10840E0E073C994E0E15FE0E6"
    );

    /// <summary>tick 推送（E4F103 流式 op——E4F101 快照 op 重复推送触发 native 重投，勿改回）；last=最近值、
    /// <b>bid=floor / ask=ceil</b>（对手价回显方向语义：taker 的 IOC 限价恒越过真实价——3B 槽 1/64 ulp &gt;
    /// ax001 0.01 tick 时最近值回显可能低 0.0044 ⇒ noise IOC 够不到真实卖价 ⇒ 双 bot 市场不成交，2026-10-13
    /// 实测；成交按 maker 价，ceil 无经济副作用），成交量/买一卖一档量/持仓量槽 ← 交易所 tick
    /// （三个量槽原为抓包常量：`0C 3D CE` = 802254、`04 51` = 1105、`03 28` = 808、持仓量 2457399）。</summary>
    internal static byte[] BuildMdTick(string instrumentId, string tradingDay, string updateTime,
        double lastPrice, double bidPrice, double askPrice, long volume, int bidVolume, int askVolume,
        double openInterest) =>
        Apply(MdTickTemplate,
            new Edit(32, 8, A(tradingDay)),
            new Edit(16, 5, F(instrumentId, 5)),
            new Edit(41, 5, F(instrumentId, 5)),
            new Edit(23, 8, A(updateTime)),
            new Edit(56, 3, PriceSlot(lastPrice)),
            new Edit(60, 3, FixedInt3(volume)),
            new Edit(72, 5, PriceSlot(openInterest, 5)),   // 持仓量：抓包槽 5B（2457399 逐字节吻合）
            new Edit(82, 3, PriceSlot(bidPrice, 3, roundUp: false)),
            new Edit(86, 2, FixedInt2(bidVolume)),
            new Edit(88, 3, PriceSlot(askPrice, 3, roundUp: true)),
            new Edit(92, 2, FixedInt2(askVolume))
        );

    /// <summary>3B 大端整数（总成交量槽——抓包宽度 3B，先验宽度不变，只改值）。值上限 16777215。</summary>
    internal static byte[] FixedInt3(long v) => EscapedRaw([(byte)(v >> 16), (byte)(v >> 8), (byte)v]);

    /// <summary>2B 大端整数（买一/卖一档量槽——抓包值 1105/808 与客户端解码值逐位吻合，见
    /// <see cref="BuildMdTick"/>）。值上限 65535。</summary>
    internal static byte[] FixedInt2(int v) => EscapedRaw([(byte)(v >> 8), (byte)v]);

    /// <summary>
    /// 数字槽原字节 + <b>E0 转义</b>（值内 {E0..EF,FE} 字节前插 E0，E0 自身亦转义——与
    /// <see cref="EscapedValue"/>/<see cref="EscapedMoney"/> 同一字母表）。
    /// <para>
    /// <b>2026-10-25 事故定案</b>：这三个量槽原先裸插字节。成交量 11749 = <c>00 2D E5</c>，低位 0xE5 落在
    /// 标记字母表里 ⇒ 客户端按标记切字段直接错位（帧长仍对，但字段边界全乱）⇒ native 侧进入**同一笔
    /// tick 的无限重投**：bot 日志 30 秒涨到 160MB、行情假死、盘口时间戳停在同一毫秒。
    /// 价格槽靠 <see cref="PriceSlot"/> 的「避 tag 递减 1 ulp」躲过这一劫，量槽不能改值（会谎报成交量），
    /// 只能按协议转义——帧长因此可变，4B 帧头带真实长度，客户端按长度读帧。
    /// </para>
    /// </summary>
    internal static byte[] EscapedRaw(byte[] raw)
    {
        var extra = raw.Count(b => b is (>= 0xE0 and <= 0xEF) or 0xFE);
        if (extra == 0) return raw;
        var r = new byte[raw.Length + extra];
        var i = 0;
        foreach (var b in raw)
        {
            if (b is (>= 0xE0 and <= 0xEF) or 0xFE) r[i++] = 0xE0;
            r[i++] = b;
        }
        return r;
    }
}
