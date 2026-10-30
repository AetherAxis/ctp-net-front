using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Ctp.Net.Front;

/// <summary>
/// 一个完整的 CTP 协议帧（从 TCP 流中分出）。
/// 标准帧: [0x02][0x00][payload_len: 2B 大端][payload]
/// 握手帧: 以 [0x00][0x06] 开头（非标准头，含内嵌 0x0200 帧 + "API_YYYYMMDD_VN" 版本串）。
/// </summary>
public sealed record CtpFrame
{
    /// <summary>原始字节（含帧头）。</summary>
    public required byte[] Raw { get; init; }

    /// <summary>payload（不含 4B 帧头）。握手帧 = Raw 原样。</summary>
    public required byte[] Payload { get; init; }

    /// <summary>true = 初始握手帧（非 0x0200 开头）。</summary>
    public bool IsHandshake { get; init; }

    /// <summary>true = 心跳帧（6B [00 02 00 00 05 00]）——会话层须回射同款心跳。</summary>
    public bool IsHeartbeat { get; init; }

    /// <summary>true = 无法解析的垃圾数据（超长兜底丢弃）——非协议帧，会话层只记日志。</summary>
    public bool IsGarbage { get; init; }

    /// <summary>消息路由 4B（payload 前 4 字节，如 01 03 33 4C）。</summary>
    public ReadOnlySpan<byte> Route => Payload.Length >= 4 ? Payload.AsSpan(0, 4) : ReadOnlySpan<byte>.Empty;

    /// <summary>消息体（payload 去掉前 4B 路由后的字段数据）。</summary>
    public ReadOnlySpan<byte> Body => Payload.Length > 4 ? Payload.AsSpan(4) : ReadOnlySpan<byte>.Empty;

    /// <summary>心跳帧字节（客户端→服务端，服务端回射同款——2026-10-02 实测：不回射 ⇒ 长会话
    /// ~30-60s 后客户端应答管线停摆（idle 查询全超时、无断线事件）——bot 长连接必死）。</summary>
    public static readonly byte[] Heartbeat = [0x00, 0x02, 0x00, 0x00, 0x05, 0x00];

    public override string ToString()
        => IsGarbage ? $"Garbage[{Raw.Length}B]"
         : IsHeartbeat ? "Heartbeat[6B]"
         : IsHandshake ? $"Handshake[{Raw.Length}B]"
         : $"Frame[{Raw.Length}B] route={Convert.ToHexString(Route)}";
}

/// <summary>
/// 流式 CTP 帧分帧器：喂 TCP 字节流，产出完整帧。
/// 处理：标准帧（0200 开头）、初始握手（0006 开头）、复合帧（一段 TCP 含多帧）、半帧（跨 TCP 段）。
/// </summary>
public sealed class CtpFrameParser
{
    private readonly List<byte> _buffer = new();

    /// <summary>喂入 TCP 字节，返回完整帧（可能 0-N 个）。</summary>
    public IReadOnlyList<CtpFrame> Feed(ReadOnlySpan<byte> data)
    {
        _buffer.AddRange(data.ToArray());
        var frames = new List<CtpFrame>();

        // 解析循环：每轮先剥头部心跳再解帧——心跳可能出现在任意两帧之间（见下）
        while (true)
        {
            // 客户端心跳: 精确 6B [00 02 00 00 05 00]——剥出为 IsHeartbeat 帧交会话层回射
            // （抓包 20260929-053552 实证: 客户端等待服务端响应期间 ~15s 一次。
            //   2026-10-02 长会话实测（DirectTest idle 复现）：不回射 ⇒ 客户端应答管线 ~30-60s 停摆——
            //   host 侧查询照常收到+应答，客户端全部超时且无断线事件 ⇒ bot 长连接必死。
            //   不剥的话它会落进「未知帧头逐字节跳过」分支: 跳过 00 后
            //   残余 [02 00 00 05 00] 幻影解析成 9B 假标准帧 → 吃掉下一真实帧的前几字节 → 流静默错位。
            //   必须在解析循环内剥而非只入口剥一次：[帧A][心跳][帧B] 同批到达时，
            //   帧A 消费后心跳在头部——只入口剥会漏掉它。无前缀撞车：握手帧 00 06 开头、
            //   标准帧 02 00 开头，均不可能是本序列。跨 TCP 段分割的心跳也能认出——剥离循环在解析循环
            //   开头每轮重跑，凑齐 6B 的那次 Feed 就会命中（`FrameParserTests.心跳跨TCP段…` 钉住）。
            while (_buffer.Count >= 6 && _buffer[0] == 0x00 && _buffer[1] == 0x02 && _buffer[2] == 0x00
                   && _buffer[3] == 0x00 && _buffer[4] == 0x05 && _buffer[5] == 0x00)
            {
                _buffer.RemoveRange(0, 6);
                frames.Add(new CtpFrame { Raw = CtpFrame.Heartbeat, Payload = CtpFrame.Heartbeat, IsHeartbeat = true });
            }

            if (!TryParseOne(out var frame) || frame is null) break;
            frames.Add(frame);
            // 移除已消费的字节
            _buffer.RemoveRange(0, frame.Raw.Length);
        }

        return frames;
    }

    private bool TryParseOne(out CtpFrame? frame)
    {
        frame = null;
        if (_buffer.Count < 4) return false;

        var b0 = _buffer[0];
        var b1 = _buffer[1];

        if (b0 == 0x02 && b1 == 0x00)
        {
            // 标准帧：[02][00][len_hi][len_lo][payload]
            var payloadLen = BinaryPrimitives.ReadUInt16BigEndian(
                CollectionsMarshal.AsSpan(_buffer)[2..4]);
            var totalLen = 4 + payloadLen;
            if (_buffer.Count < totalLen) return false;   // 半帧——等更多字节

            var raw = _buffer.Take(totalLen).ToArray();
            frame = new CtpFrame
            {
                Raw = raw,
                Payload = raw[4..],
                IsHandshake = false,
            };
            return true;
        }

        if (b0 == 0x00 && b1 == 0x06)
        {
            // 初始握手帧：格式不同——找内嵌的 0200 帧来确定边界
            // 策略：从偏移 2 开始找 02 00，用内嵌帧长度 + 偏移作为整帧长度
            for (var i = 2; i < Math.Min(_buffer.Count - 4, 32); i++)
            {
                if (_buffer[i] == 0x02 && _buffer[i + 1] == 0x00)
                {
                    var innerLen = BinaryPrimitives.ReadUInt16BigEndian(
                        CollectionsMarshal.AsSpan(_buffer)[(i + 2)..(i + 4)]);
                    var totalLen = i + 4 + innerLen;
                    if (_buffer.Count < totalLen) return false;   // 半帧

                    var raw = _buffer.Take(totalLen).ToArray();
                    frame = new CtpFrame
                    {
                        Raw = raw,
                        Payload = raw,   // 握手帧整个作为 payload
                        IsHandshake = true,
                    };
                    return true;
                }
            }

            // 没找到内嵌 0200 帧——可能是纯握手头（如只有 00 06 00 00 07 04 00 00 00 27）
            // 尝试按已知模式截取：00 06 开头，到下一个 02 00 之前
            for (var i = 2; i < Math.Min(_buffer.Count - 1, 16); i++)
            {
                if (_buffer[i] == 0x02 && _buffer[i + 1] == 0x00)
                {
                    var raw = _buffer.Take(i).ToArray();
                    frame = new CtpFrame
                    {
                        Raw = raw,
                        Payload = raw,
                        IsHandshake = true,
                    };
                    return true;
                }
            }

            // 都不匹配——超长仍无边界，按垃圾数据兜底丢弃 64B。
            // 注意：这里不动 _buffer——Feed 按 Raw.Length 统一消费（旧版内部又 RemoveRange 一次
            // ⇒ 每帧垃圾消费 128B、错位放大一倍）；且不得标 IsHandshake（旧版会把垃圾冒充握手帧
            // 送进 HandleHandshake ⇒ 会话中途重发挑战、状态机重置）——用独立的 IsGarbage 标记。
            if (_buffer.Count > 64)
            {
                var raw = _buffer.Take(64).ToArray();
                frame = new CtpFrame { Raw = raw, Payload = raw, IsGarbage = true };
                return true;
            }
            return false;
        }

        // 未知帧头——跳过一个字节重新尝试
        _buffer.RemoveAt(0);
        return TryParseOne(out frame);
    }
}
