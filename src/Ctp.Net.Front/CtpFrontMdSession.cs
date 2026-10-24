using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Ctp.Net;
using Aether.Exchange.Rpc;

namespace Ctp.Net.Front;

/// <summary>
/// CTP 行情腿会话（M3）：握手/挑战/确认与交易腿同款线协议信封 → MD 登录（op <c>E4 30 E6 03</c>，
/// 凭据不校验——行情订阅无账户语义）→ 订阅（op <c>E4 44 01</c>，解析合约集 → RPC `quotes` 快照 +
/// `subscribe` 订阅，应答与快照**同一次 TCP 写**）→ tick 推送（<see cref="SimRpcClient.TickPushed"/>
/// → E4F103 单合约帧，按订阅集过滤）。
/// <para>
/// <b>进程分离（2026-10-05）</b>：经自持 <see cref="SimRpcClient"/> 连交易所（行情无账户语义——
/// 本会话不登录，连接只做 Hello + quotes/subscribe op；服务端按本连接的订阅集路由 tick）。
/// 快照与时钟来自 `quotes` op **应答**（非推送）——「订阅应答+快照单次写」的载荷形状与进程内版
/// 逐字节一致；tick 的 day/time 空值兜底用登录时缓存的交易所时钟。
/// </para>
/// 出站管道与交易腿同款（Channel 单写者 + RoutePush）；tick 是纯推送，无应答排序需求（无 capture 缓冲）。
/// </summary>
internal sealed class CtpFrontMdSession : IDisposable
{
    private enum State { WaitingHandshake, WaitingChallengeResponse, WaitingMdLogin, Ready, Closed }

    private readonly TcpClient _client;
    private readonly Func<SimRpcClient> _rpcFactory;
    private readonly Action<string> _log;
    private State _state = State.WaitingHandshake;
    private readonly CtpFrameParser _parser = new();
    private readonly HashSet<string> _subscribed = new(StringComparer.OrdinalIgnoreCase);

    // 交易所时钟缓存（MD 登录时经 quotes op 取——tick 的 day/time 空值兜底，替代进程内直调核心时钟）
    private string _tradingDay = "";
    private string _timeOfDay = "";

    private readonly Channel<byte[]> _out = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly object _writeGate = new();

    // RPC 可替换（断连重建）——读侧快照访问
    private SimRpcClient _rpc;
    private readonly SemaphoreSlim _rebuildLock = new(1, 1);

    public CtpFrontMdSession(TcpClient client, Func<SimRpcClient> rpcFactory, Action<string> log)
    {
        _client = client;
        _rpcFactory = rpcFactory;
        _rpc = rpcFactory();
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var remote = _client.Client.RemoteEndPoint?.ToString() ?? "?";
        _log($"MD 会话启动: {remote}");
        var writer = Task.CompletedTask;
        try
        {
            using var stream = _client.GetStream();
            writer = WriterLoopAsync(stream, ct);
            var buffer = new byte[64 * 1024];
            while (_state != State.Closed && !ct.IsCancellationRequested)
            {
                var n = await stream.ReadAsync(buffer, ct);
                if (n == 0) break;
                foreach (var frame in _parser.Feed(buffer.AsSpan(0, n)))
                {
                    // 心跳回射（协议级 keepalive，不走业务分发——任意会话状态都须应答；与交易腿同款，
                    // 2026-10-02 长会话实测：不回射 ⇒ 客户端应答管线停摆）。tick 是纯推送无应答排序
                    // 需求，心跳回射与业务帧同一出站通道保序即可。
                    if (frame.IsHeartbeat)
                    {
                        _out.Writer.TryWrite(CtpFrame.Heartbeat);
                        continue;
                    }
                    var response = await ProcessFrameAsync(frame, ct);
                    if (response is { Length: > 0 })
                        _out.Writer.TryWrite(response);
                }
            }
        }
        catch (Exception ex)
        {
            _log($"MD 会话异常: {ex.Message}");
        }
        finally
        {
            _state = State.Closed;
            _rpc.TickPushed -= OnTick;
            _rpc.Unsubscribe();     // 服务端清本连接订阅集（fire-and-forget）
            _rpc.Dispose();
            _out.Writer.TryComplete();
            try { await writer; } catch { }
            _log($"MD 会话结束: {remote}（订阅 {string.Join(",", _subscribed)}）");
        }
    }

    private async Task WriterLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _out.Reader.ReadAllAsync(ct))
            {
                _log($"→ [{frame.Length}B] {Convert.ToHexString(frame)}");
                await stream.WriteAsync(frame, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"MD 写循环异常: {ex.Message}"); }
    }

    private void RoutePush(byte[] frame)
    {
        lock (_writeGate) _out.Writer.TryWrite(frame);
    }

    private async Task<byte[]?> ProcessFrameAsync(CtpFrame frame, CancellationToken ct)
    {
        _log($"← {frame}");
        if (frame.IsGarbage) return null;

        if (frame.IsHandshake)
        {
            if (_state != State.WaitingHandshake) return null;
            _state = State.WaitingChallengeResponse;
            return BuildFrame(CtpFrontSession.ChallengePayload);
        }

        return _state switch
        {
            State.WaitingChallengeResponse => HandleChallengeResponse(),
            State.WaitingMdLogin => await HandleMdLoginAsync(frame, ct),
            State.Ready => await HandleSubscribeAsync(frame, ct),
            _ => null,
        };
    }

    private byte[]? HandleChallengeResponse()
    {
        _state = State.WaitingMdLogin;
        return BuildFrame(CtpFrontTemplates.ConfirmPayload);
    }

    private async Task<byte[]?> HandleMdLoginAsync(CtpFrame frame, CancellationToken ct)
    {
        var body = frame.Body;
        if (body.Length < 4 || body[0] != 0xE4 || body[1] != 0x30 || body[2] != 0xE6 || body[3] != 0x03)
        {
            _log($"非预期帧（等 MD 登录）: body前缀={HexPrefix(body)}");
            return null;
        }
        var user = ExtractInstrumentLike(body);
        _log($"MD 登录: user={user ?? "?"}（凭据不校验）");
        try
        {
            // 进程分离：行情无账户语义——连接交易所（Hello）取时钟（交易日/时间，tick 兜底用），不登录。
            // 交易所不可达（90012）⇒ 诚实失败：关会话（客户端见断线）。
            await ConnectRpcAsync(ct);
        }
        catch (CtpBusinessException ex)
        {
            _log($"MD 登录失败（交易所不可达）: [{ex.ErrorId}] {ex.ErrorMsg}——关闭连接");
            _state = State.Closed;
            return null;
        }
        _state = State.Ready;
        return BuildFrame(CtpFrontTemplates.BuildMdLoginRsp(_tradingDay));
    }

    /// <summary>连接交易所 RPC + 取时钟 + 挂 tick/断连事件（登录与重建共用）。</summary>
    private async Task ConnectRpcAsync(CancellationToken ct)
    {
        _rpc.ConnectionLost += OnRpcConnectionLost;
        await _rpc.ConnectAsync(ct);
        var clock = await _rpc.GetQuotesAsync(null, ct);
        _tradingDay = clock.TradingDay;
        _timeOfDay = clock.TimeOfDay;
        _rpc.TickPushed += OnTick;   // tick 按本连接订阅集路由（服务端 MatchesAccount/SubscribedTo 路由）
    }

    /// <summary>交易所 RPC 死亡（host 重启/超时销毁）——不重建 ⇒ 本连接 tick 静默停、客户端无感知地看死行情
    ///（2026-10-18 定案：MD 会话存活期间 tick 推送停更——桌面端/bot 行情假死，重连才恢复）。</summary>
    private void OnRpcConnectionLost() => _ = RebuildRpcAsync();

    /// <summary>重建交易所 RPC + 重订阅存量合约集。指数退避由固定 5s 间隔替代（交易所同机回环，恢复快）。</summary>
    private async Task RebuildRpcAsync()
    {
        if (_state != State.Ready) return;
        if (!await _rebuildLock.WaitAsync(0)) return;   // 已有重建在进行
        try
        {
            for (var attempt = 1; _state == State.Ready; attempt++)
            {
                try
                {
                    _log($"MD RPC 断开——重建交易所连接（第 {attempt} 次）…");
                    try { _rpc.TickPushed -= OnTick; _rpc.ConnectionLost -= OnRpcConnectionLost; _rpc.Dispose(); } catch { }
                    _rpc = _rpcFactory();
                    await ConnectRpcAsync(CancellationToken.None);
                    if (_subscribed.Count > 0)
                        await _rpc.SubscribeAsync(_subscribed.ToList(), CancellationToken.None);
                    _log($"MD RPC 重建成功——重订阅 {string.Join(",", _subscribed)}");
                    return;
                }
                catch (Exception ex)
                {
                    _log($"MD RPC 重建失败（第 {attempt} 次）: {ex.Message}——5s 后重试");
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }
        }
        finally { _rebuildLock.Release(); }
    }

    private async Task<byte[]?> HandleSubscribeAsync(CtpFrame frame, CancellationToken ct)
    {
        var body = frame.Body;
        if (body.Length < 3 || body[0] != 0xE4 || body[1] != 0x44 || body[2] != 0x01)
        {
            _log($"非预期帧（等订阅）: body前缀={HexPrefix(body)}");
            return null;
        }
        // 解析合约集（严格合约形状 run——与交易腿 ⑲ 同款规则）并增量登记
        var added = new List<string>();
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
            if (j > i + 1 && IsInstrumentRun(body[(i + 1)..j]))
            {
                var inst = Encoding.ASCII.GetString(body[(i + 1)..j]);
                if (_subscribed.Add(inst)) added.Add(inst);
            }
            i = j - 1;
        }
        _log($"订阅: +[{string.Join(",", added)}] → 共 {_subscribed.Count} 个");
        if (added.Count == 0)
            return BuildFrame(CtpFrontTemplates.MdSubscribeRspTemplate);

        // 快照经 `quotes` op 应答取（进程内版 = Market.GetQuote 直调）；订阅经 `subscribe` op（服务端
        // 激活行情——订阅即激活）。先取快照再订阅：快照走应答通道，与订阅 op 的激活快照推送（如有）不冲突
        // （本会话 tick 路由在订阅登记后生效——两个来源的 tick 都按订阅集过滤，幂等）。
        var snap = await _rpc.GetQuotesAsync(added, ct);
        await _rpc.SubscribeAsync(added, ct);

        // 订阅应答 + 当前快照 tick **同一次 TCP 写**（真实前置行为——v2 抓包 664B = 195B rsp + 461B tick
        // 拼一个写；M0 = rsp + 12B 迷你帧同写）。单独推送 E4F101 会触发 native 无限重投（E2E 7 种
        // 帧形状实证）——「紧跟 rsp 同写」是唯一未测且与抓包完全一致的形状。快照来自 op 应答 ⇒ 形状不变。
        var parts = new List<byte[]> { BuildFrame(CtpFrontTemplates.MdSubscribeRspTemplate) };
        foreach (var q in snap.Quotes ?? new List<MarketQuote>())
        {
            // 快照通道的 MarketQuote 无成交量字段（⑥ RPC 契约）——订阅首帧先给 0，随后的流式 tick 带真实量。
            parts.Add(BuildFrame(CtpFrontTemplates.BuildMdTick(q.InstrumentId, snap.TradingDay,
                snap.TimeOfDay, q.LastPrice, q.BidPrice, q.AskPrice, volume: 0, bidVolume: 0, askVolume: 0)));
            _log($"→ 快照: {q.InstrumentId} last={q.LastPrice} bid={q.BidPrice} ask={q.AskPrice}");
        }
        var total = parts.Sum(b => b.Length);
        var buf = new byte[total];
        var off = 0;
        foreach (var b in parts) { b.CopyTo(buf, off); off += b.Length; }
        return buf;
    }

    private void OnTick(Aether.Exchange.Tick tick)
    {
        if (_state != State.Ready || !_subscribed.Contains(tick.InstrumentId)) return;
        var day = string.IsNullOrEmpty(tick.TradingDay) ? _tradingDay : tick.TradingDay;
        _log($"→ 推送 tick: {tick.InstrumentId} last={tick.LastPrice} bid={tick.BidPrice} ask={tick.AskPrice}");
        // tick 推送（E4F103 流式 op——2026-10-02 长会话抓包定案：旧模板误用 E4F101 一次性快照 op，
        // 重复推送快照帧触发 native 无限重投；E4F103 是真实 tick 流 op，481 帧/次会话 SDK 零洪水）
        RoutePush(BuildFrame(CtpFrontTemplates.BuildMdTick(tick.InstrumentId, day,
            string.IsNullOrEmpty(tick.UpdateTime) ? _timeOfDay : tick.UpdateTime,
            tick.LastPrice, tick.BidPrice, tick.AskPrice, tick.Volume, tick.BidVolume, tick.AskVolume)));
    }

    // —— 工具（与交易腿同款规则）——

    private static bool IsTag(byte b) => b is >= 0xE1 and <= 0xEF or 0xFE;

    private static bool IsInstrumentRun(ReadOnlySpan<byte> r)
    {
        var letters = 0;
        while (letters < r.Length && char.IsAsciiLetter((char)r[letters])) letters++;
        return letters is >= 1 and <= 2 && r.Length - letters is >= 3 and <= 4   // 1-2 字母（官方命名；本地品种 zx 对齐）
            && r[letters..].ToArray().All(b => b is >= (byte)'0' and <= (byte)'9');
    }

    private static string? ExtractInstrumentLike(ReadOnlySpan<byte> body)
    {
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != 0xE7) continue;
            var j = i + 1;
            while (j < body.Length && !IsTag(body[j])) j++;
            if (j > i + 1 && IsInstrumentRun(body[(i + 1)..j]))
                return Encoding.ASCII.GetString(body[(i + 1)..j]);
        }
        return null;
    }

    private static string HexPrefix(ReadOnlySpan<byte> body) => Convert.ToHexString(body[..Math.Min(body.Length, 16)]);

    private static byte[] BuildFrame(byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        frame[0] = 0x02;
        frame[1] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), (ushort)payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    public void Dispose()
    {
        _state = State.Closed;
        try { _client.Close(); } catch { }
    }
}
