using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Ctp.Net;
using Aether.Exchange.Rpc;

namespace Ctp.Net.Front;

/// <summary>
/// CTP 前置会话：一条 TCP 连接 = 一个会话状态机。
/// <para>
/// 状态流转（从抓包还原的序列）：
/// <code>
/// Handshake(①) → Challenge(②) → ChallengeResponse(③→④) →
///   Auth(⑤→⑥) → Login(⑦→⑧) → Authenticated(⑨→⑩ …⑪⑫⑬⑭⑮⑯⑰⑱)
/// </code>
/// </para>
/// <para>
/// <b>应答策略 = 抓包 verbatim 复刻</b>（<see cref="CtpFrontTemplates"/>，由 ProtoCapture gen 生成器算偏移：
/// <c>dotnet run --project apps/CtpSim/Acme.Ctp.ProtoCapture -- gen</c>）：
/// 官方 C++ SDK 自己解析帧——我们只把捕获的 S→C 帧原样回放，替换 broker/user/reqId/交易日/资金等
/// 少数标记界定字段。密码策略：发<b>固定挑战</b> ⇒ 同密码+同 nonce ⇒ 同密文 ⇒ 存密文当哈希比对。
/// </para>
/// <para>
/// <b>进程分离（2026-10-05，真实世界对齐）</b>：会话经自持的 <see cref="SimRpcClient"/> 连交易所（每客户端
/// 会话一条连接——登录绑定账户，服务端按连接路由推送，真实券商柜台镜像）。RPC op 无账户参数（绑定账户）、
/// 推送 payload 无 accountId（按连接路由）⇒ 本会话无需账户键与事件过滤。「应答先于推送」跨进程保持：
/// RPC 侧 SimConnection 的 capture-during-dispatch 保证应答帧先写 wire；本会话的捕获窗（<see cref="_dispatching"/>
/// 跨 await 有效）把 RPC 推送事件压到应答帧之后排空。密码域（2026-10-09 双域）：密文 hex 塞进
/// <c>LoginArg.Password</c> 且 <c>FrontDomain</c>=true——核心按域存/验（front 列独立于 JSON-RPC 明文列），
/// 同一账户前置与提币两扇门都通（此前域隔离互斥——闭环断在提币最后一焊）。
/// </para>
/// </summary>
internal sealed class CtpFrontSession : IDisposable
{
    private enum State
    {
        WaitingHandshake,
        WaitingChallengeResponse,
        WaitingAuth,
        WaitingLogin,
        Authenticated,
        Closed,
    }

    private readonly TcpClient _client;
    private readonly SimRpcClient _rpc;
    private readonly Action<string> _log;
    private State _state = State.WaitingHandshake;
    private readonly CtpFrameParser _parser = new();

    // 查报单缺口告警节流（2026-10-24——多行报单应答帧未实现，见 WarnIfOrdersExistAsync）
    private DateTime _lastOrderGapWarnAt = DateTime.MinValue;
    private int _orderGapWarnCount;

    // 会话身份（⑤/⑦ 请求中提取）
    private string _userId = "";
    private string _brokerId = "";

    // 登录后上下文
    private CtpLoginResult? _login;
    private byte _loginReqId;         // ⑦ 的 reqId——⑩ 回显它（SDK 以此归并登录结果）

    // —— 出站管道（M2）：读循环不再内联写——应答入队，单写者循环统一写 TCP；主动推送（RPC 推送事件）经
    //    RoutePush 入队。capture-during-dispatch：本会话分发线程执行 op 期间（含 RPC await——_dispatching
    //    跨 await 保持 true）推送先入捕获缓冲，应答帧入队后同锁排空——「应答先于推送」（消费方在应答后
    //    才注册 OrderRef，先到的回报会被丢）。RPC 侧服务端另有同款捕获保证应答帧先写 wire（F1 顺序契约）。——
    private readonly Channel<byte[]> _out = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly object _writeGate = new();
    private readonly List<byte[]> _captured = new();
    private volatile bool _dispatching;

    public CtpFrontSession(TcpClient client, SimRpcClient rpc, Action<string> log)
    {
        _client = client;
        _rpc = rpc;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var remote = _client.Client.RemoteEndPoint?.ToString() ?? "?";
        _log($"[CTP-Front] 会话启动: {remote}");
        var writer = Task.CompletedTask;
        try
        {
            using var stream = _client.GetStream();
            _rpc.ConnectionLost += OnExchangeLost;   // 交易所死亡 ⇒ 关会话（客户端见断线）
            writer = WriterLoopAsync(stream, ct);
            var buffer = new byte[64 * 1024];
            while (_state != State.Closed && !ct.IsCancellationRequested)
            {
                var n = await stream.ReadAsync(buffer, ct);
                if (n == 0) break;

                var frames = _parser.Feed(buffer.AsSpan(0, n));
                foreach (var frame in frames)
                {
                    // 心跳回射（协议级 keepalive，不走业务分发/捕获缓冲——任意会话状态都须应答）。
                    // 2026-10-02 DirectTest idle 复现：不回射 ⇒ 客户端应答管线 ~30-60s 停摆
                    // （host 照常收答、客户端全超时无断线事件）——bot 长连接必死。
                    if (frame.IsHeartbeat)
                    {
                        _out.Writer.TryWrite(CtpFrame.Heartbeat);
                        continue;
                    }
                    await DispatchFrameAsync(frame, ct);
                }
            }
        }
        catch (Exception ex)
        {
            _log($"[CTP-Front] 会话异常: {ex.Message}");
        }
        finally
        {
            _state = State.Closed;
            _rpc.OrderPushed -= OnRpcOrderPushed;
            _rpc.TradePushed -= OnRpcTradePushed;
            _rpc.ConnectionLost -= OnExchangeLost;
            _rpc.Logout();            // 服务端清绑定/订阅 + flush（镜像进程内版的会话事件退订语义）
            _rpc.Dispose();
            _out.Writer.TryComplete();
            try { await writer; } catch { }   // 排空剩余出站帧（含捕获的推送）
            _log($"[CTP-Front] 会话结束: {remote}");
        }
    }

    /// <summary>单写者出站循环——唯一写 TCP 的地方（应答与推送统一走 <see cref="_out"/>）。</summary>
    private async Task WriterLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _out.Reader.ReadAllAsync(ct))
            {
                // 出站帧也记日志——诊断「SDK 收到坏响应后挂死」时需要看见我们到底发了什么字节
                _log($"[CTP-Front] → [{frame.Length}B] {Convert.ToHexString(frame)}");
                await stream.WriteAsync(frame, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"[CTP-Front] 写循环异常: {ex.Message}"); }
    }

    /// <summary>
    /// 分发一个入站帧（capture-during-dispatch 包装）：处理期间（含 RPC await）推送事件回流进
    /// <see cref="_captured"/>；应答帧入队后同锁排空捕获——应答先于本单推送到达客户端
    /// （RPC 侧服务端另有 capture 保证应答帧先写 wire；本会话的捕获窗跨 await 覆盖
    /// 「应答 continuation 与推送事件在不同线程」的竞态——无论谁先跑，次序都由排空点决定）。
    /// 无应答帧且有捕获推送（成功报单路径）时先让路 10ms：客户端 CtpBrokerage 在
    /// OrderInsertAsync 返回后才把 OrderRef 注册进 tracker，loopback 无 WAN RTT，
    /// SDK 回调线程可能先于注册到达 ⇒ 回报被当手动单静默丢弃 + 看门狗误判（SimRpcServer 同款事故）。
    /// 让路期间 <see cref="_dispatching"/> 保持 true——其它来源的推送仍入捕获，次序无损。
    /// </summary>
    private async Task DispatchFrameAsync(CtpFrame frame, CancellationToken ct)
    {
        byte[]? response = null;
        lock (_writeGate) _dispatching = true;
        try
        {
            response = await ProcessFrameAsync(frame, ct);
        }
        finally
        {
            var hasCaptured = false;
            lock (_writeGate) hasCaptured = _captured.Count > 0;
            if (response is not { Length: > 0 } && hasCaptured)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(10), ct); }
                catch (OperationCanceledException) { }
            }
            lock (_writeGate)
            {
                if (response is { Length: > 0 })
                    _out.Writer.TryWrite(response);
                foreach (var captured in _captured)
                    _out.Writer.TryWrite(captured);
                _captured.Clear();
                _dispatching = false;
            }
        }
    }

    /// <summary>F0 推送序号——自然计数 + 256 回卷（2026-10-15 修正：旧版「跳过字母表 223→F0」制造序列
    /// 跳变，客户端推送序列校验拒收 ⇒ 其后整流停摆（混合流 225 轮实测）。E0..EF 值由 builder 的
    /// EscapedValue 编码为转义对（镜像 SDK 请求侧同款）；FE/FF 裸值是数据（254 轮定案）。</summary>
    private int _rtnInstanceInt;
    /// <summary>F0 族推送序号计数：到 240 标记回收（SDK 可接受域实测 1-251——E3 FC 起、E2 双字节区、
    /// 回卷形态均被拒收（2026-10-19 F0 族 E2E 实证；224-239 转义对/240-251 裸值可过）。序号是 SDK 侧
    /// 底层限制，服务器端无法扩域——临界限即主动断开客户端连接（SDK 自动重连、新会话序号归零，
    /// 客户端零改动）。240 = 251 减余量。相关：被 5c6f9ff 退役的客户端 220 规避重连实质是在兜同一
    /// 限制（应答族 int 化只救了 80 族，救不了 F0 推送族）。</summary>
    private bool _rtnRecycleDue;
    private int NextRtnInstance()
    {
        var n = ++_rtnInstanceInt;
        if (n >= 240) _rtnRecycleDue = true;
        return n;
    }

    /// <summary>应答侧 80 族记录流水（⑯/⑱/查合约/查报单应答的 `E3` 后 1B）——**逐条递增，非请求回显**。
    /// 2026-10-03 SimNow 过滤查报单实证：请求 op `E104E280E4` 的计数在 op 字节里（03/04…），
    /// 而应答 `E3` = 09 = 会话内全部 80 族应答记录的流水（⑯=01、⑱=02、二进制报单 6 行内部消耗
    /// 03-08、空应答=09）。客户端按它丢弃过期帧——回显请求计数会被判过期静默丢弃。多行应答逐行消耗。 </summary>
    private int _qrySeqInt;
    /// <summary>80 族应答流水——<b>int 永不回卷</b>（真前置 2026-10-15 300 笔实抓定案：1-223 裸值、
    /// 224-239 跳段/转义皆过、240-255 裸值含 FE/FF、≥256 转 `E2 &lt;2B 大端&gt;`——编码统一在
    /// <see cref="CtpFrontTemplates.InstanceField"/>）。旧 byte 版 256→0/→1 回卷 = 值骤降被客户端
    /// 拒收 ⇒ 111 轮停摆根因；「请求回显」修复是巧合重合（小会话前置计数 ≈ 请求计数）。</summary>
    private int NextQrySeq() => ++_qrySeqInt;

    private int _cmdInstanceInt;
    private int NextCmdInstance() => ++_cmdInstanceInt;

    /// <summary>
    /// 查报单的诚实告警（2026-10-24）：本前置**没有非空报单的应答帧模板**——真前置的非空报单走 LZ4 列式二进制
    /// （未逆向；已对齐的只有空表那个 13B tag 帧），所以任何查报单都回空表。账户真有活单时，客户端看到的
    /// 「委托表为空」是假象：这里按 30s 节流把它写进日志，不让缺口静默（这是已知功能缺失，不是真前置行为）。
    /// </summary>
    /// <summary>
    /// 查报单缺口告警：本前置没有非空报单应答帧模板，查报单一律回空表。账户真有委托时这不只是「少几行」——
    /// 客户端会据此认为自己没有可撤的活单（对账/追单逻辑直接走错分支），所以必须喊进日志。
    /// <para>
    /// 计数口径：<c>QueryOrders</c> 回的是账户账本里的<b>全部</b>委托（含已成/已撤的历史记录，实测 botmm 单账户
    /// 上万条），所以分开报——活单（'3' 未成交在队列 / '1' 部分成交在队列，即 <c>SimOrderStatus.IsLive</c>）
    /// 是危险量，历史条数只是背景。
    /// </para>
    /// </summary>
    private void WarnIfOrdersExistAsync()
    {
        if ((DateTime.UtcNow - _lastOrderGapWarnAt).TotalSeconds < 30) return;
        _lastOrderGapWarnAt = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            try
            {
                var orders = await _rpc.QueryOrdersAsync(null, CancellationToken.None);
                if (orders.Count == 0) return;
                var live = 0;
                foreach (var o in orders)
                    if ((char)o.OrderStatusRaw is '3' or '1') live++;
                _log($"[CTP-Front] ⚠ 查报单：账本共 {orders.Count} 条委托记录（其中活单 {live} 张），" +
                     $"但非空报单应答帧未实现——本次仍回空表" +
                     (live > 0 ? $"（客户端看到空委托表是假象，实际有 {live} 张可撤活单）" : "（只有历史记录，客户端仍应看到当日委托）") +
                     $"；第 {++_orderGapWarnCount} 次告警");
            }
            catch { /* 告警失败不影响查询本身 */ }
        });
    }

    /// <summary>核心错误码 → wire 错误码（捕获槽 1 字节：>255 的核心码降级为 148 通用码，真码进 GBK 文本前缀）。</summary>
    private static (int Id, string Msg) WireError(CtpBusinessException ex)
        => ex.ErrorId > byte.MaxValue ? (148, $"[{ex.ErrorId}] {ex.ErrorMsg}") : (ex.ErrorId, ex.ErrorMsg);

    /// <summary>RPC 拒单异常归一：分仓后 SimRpcClient（Exchange.Rpc）抛 <c>ExchangeBusinessException</c>，
    /// 本类错误应答路径统一按 <c>CtpBusinessException</c> 消费（2026-10-23 测试迁移逮住的漏接：⑲/㉑ 只接
    /// CtpBusinessException ⇒ post-only 拒单异常逃逸打死会话泵，后续 op 全部超时）。</summary>
    private static CtpBusinessException AsCtp(Exception ex)
        => ex switch
        {
            CtpBusinessException c => c,
            Aether.Exchange.ExchangeBusinessException e => CtpDomainMapper.ToCtpException(e),
            _ => throw ex,
        };

    // —— M2 推送：RPC 推送事件 → OnRtnOrder/OnRtnTrade wire 帧（按连接路由——本连接只收本账户的推送，无需过滤）——

    private void OnRpcOrderPushed(Aether.Exchange.Order order)
    {
        if (_state != State.Authenticated) return;
        _log($"[CTP-Front] → 推送 OnRtnOrder: ref={order.OrderRef} status={(char)order.OrderStatusRaw}");
        RoutePush(BuildFrame((char)order.OrderStatusRaw switch
        {
            '0' => CtpFrontTemplates.BuildRtnOrder0(NextRtnInstance(), _userId, _brokerId, order.InstrumentId, order.OrderRef,
                (char)order.PriceType, (char)order.Direction, (char)order.OffsetFlag, order.LimitPrice, order.VolumeTotalOriginal, '3', _login?.TradingDay ?? ""),
            // 非 '0'/'3' 状态必须带真状态字节——客户端（CtpBrokerage tracker / 期货页）靠它收敛订单生命周期。
            // 掩成 '3' 的后果：'5'/'2'/'4' 终态成「永远在队」僵尸（2026-10-02 双 bot 冻结根因：做市商撤单
            // 后票根永不清 ⇒ 不再重挂）；'1' 部成不可见（做市商无法区分在队/部成）。
            // '3' 模板 + 状态槽(177)/消息槽(219) 等宽编辑，SDK 正常回调（交易所模式双客户端 E2E 钉住）。
            // 边界：部成/终态推送的 VolumeTraded 字段保留捕获 verbatim（成交量回显失真——README 记录）。
            '1' or '2' or '4' or '5' => CtpFrontTemplates.BuildRtnOrderTerminated(NextRtnInstance(), _userId, _brokerId, order.InstrumentId, order.OrderRef,
                (char)order.PriceType, (char)order.Direction, (char)order.OffsetFlag, order.LimitPrice, order.VolumeTotalOriginal, '3', _login?.TradingDay ?? "",
                (char)order.OrderStatusRaw, ShortStatusMsg(order)),
            _ => CtpFrontTemplates.BuildRtnOrder3(NextRtnInstance(), _userId, _brokerId, order.InstrumentId, order.OrderRef,
                (char)order.PriceType, (char)order.Direction, (char)order.OffsetFlag, order.LimitPrice, order.VolumeTotalOriginal, '3', _login?.TradingDay ?? ""),
        }) ?? Array.Empty<byte>());
    }

    /// <summary>非 '0'/'3' 状态推送的状态消息（消息槽 6B = 3 个 GBK 汉字，超长截断——等宽纪律）。</summary>
    private static string ShortStatusMsg(Aether.Exchange.Order order) => (char)order.OrderStatusRaw switch
    {
        '1' => "部成交",
        '2' => "已作废",
        '4' => "已作废",
        '5' => "已撤销",
        _ => order.StatusMsg is { Length: > 0 } ? order.StatusMsg : "终态",
    };

    private void OnRpcTradePushed(Aether.Exchange.Trade trade)
    {
        if (_state != State.Authenticated) return;
        _log($"[CTP-Front] → 推送 OnRtnTrade: ref={trade.OrderRef} {(char)trade.Direction}{(char)trade.OffsetFlag} {trade.Volume}@{trade.Price} id={trade.TradeId}");
        RoutePush(BuildFrame(CtpFrontTemplates.BuildRtnTrade(NextRtnInstance(), _userId, _brokerId, trade.InstrumentId, trade.OrderRef,
            trade.TradeId, (char)trade.Direction, (char)trade.OffsetFlag, trade.Price, trade.Volume, _login?.TradingDay ?? "")));
    }

    /// <summary>交易所侧连接死亡（host 挂/超时销毁）⇒ 关闭本 CTP 会话（客户端见断线——真实券商行为）。</summary>
    private void OnExchangeLost()
    {
        _log("[CTP-Front] 与交易所的 RPC 连接断开——关闭会话");
        _state = State.Closed;
        try { _client.Close(); } catch { }   // 读写循环随之退出
    }

    /// <summary>主动推送入站（core 事件 → wire 帧）。分发期间入捕获缓冲（应答先于推送），否则直写队列。</summary>
    private void RoutePush(byte[] frame)
    {
        lock (_writeGate)
        {
            if (_dispatching)
                _captured.Add(frame);
            else
                _out.Writer.TryWrite(frame);
        }
        // F0 族序号临界限（240）：本帧入队后主动断开——客户端 SDK 自动重连开新会话（序号归零）。
        // 在 lock 外执行（Close 触发读循环退出 → finally 清理链安全）；最后一帧已在出站队列（写循环
        // 先于 Close 生效的概率高，即便丢弃客户端重连后重查自愈）。
        if (_rtnRecycleDue && _state != State.Closed)
        {
            _rtnRecycleDue = false;
            _log("[CTP-Front] F0 推送序号达 240 上限——主动断开客户端（SDK 自动重连、序号归零）");
            try { _client.Close(); } catch { }
        }
    }

    /// <summary>处理一个入站帧，返回应答帧（null = 无应答）。业务分支含 RPC await（交易所跨进程）。</summary>
    private async Task<byte[]?> ProcessFrameAsync(CtpFrame frame, CancellationToken ct)
    {
        _log($"[CTP-Front] ← {frame}");

        if (frame.IsGarbage)
        {
            _log($"[CTP-Front] ✗ 垃圾帧丢弃(64B): {Convert.ToHexString(frame.Raw)}");
            return null;
        }

        return frame.IsHandshake
            ? HandleHandshake(frame)
            : _state switch
            {
                State.WaitingChallengeResponse => HandleChallengeResponse(frame),
                State.WaitingAuth => HandleAuth(frame),
                State.WaitingLogin => await HandleLoginAsync(frame, ct),
                State.Authenticated => await HandleBusinessAsync(frame, ct),
                _ => null,
            };
    }

    // —— ① 初始握手：客户端发版本串，我们回固定挑战 ② ——

    private byte[]? HandleHandshake(CtpFrame frame)
    {
        // 状态守卫：只接受初始握手。会话中途再收到 0006 开头的数据（对端重连半帧/垃圾被误分帧）
        // 若照旧应答 ⇒ 重发挑战 ② + 状态机重置回 WaitingChallengeResponse——已认证会话被腰斩。
        if (_state != State.WaitingHandshake)
        {
            _log($"[CTP-Front] 会话中途收到握手帧——忽略（当前状态={_state}）: {HexPrefix(frame.Payload)}");
            return null;
        }
        _state = State.WaitingChallengeResponse;
        return BuildFrame(ChallengePayload);
    }

    /// <summary>
    /// 固定挑战 payload——从 SimNow 真实抓包帧②逐字节提取（175B，含完整 ~128B 加密 nonce）。
    /// 服务端发固定挑战 ⇒ 同密码+同挑战 ⇒ 同密文 ⇒ 存密文当哈希比对（无需逆向加密算法）。
    /// </summary>
    internal static readonly byte[] ChallengePayload = Convert.FromHexString(
        "0103344ce43026e5020192e755efefefefefea40330135e3802094e0e753222cf1d8ce9a2a3782e0e51bcba2e0ea43a6b6f3759f278ca11ebdf1a3d062cd86d13f38a655c65492307ee0eb99c7b76424bda606bf81207fe0e40d280796f81730fb3731051d0980f1d2ddf3797f356c91e0ec3eae3de0e7e0e022a99ec108f57ab42d3ab0fb7ce0e869c93d8e32fdfe4f64e0e06a5084b72d7ac89141b868ae34f1b40cefefefefefefefefefefefec");

    // —— ③ 挑战响应 → ④ 连接确认（verbatim 复刻）——

    private byte[]? HandleChallengeResponse(CtpFrame frame)
    {
        // 客户端回加密的挑战响应——直接接受，发连接确认（抓包帧④ 19B 原样）
        _state = State.WaitingAuth;
        return BuildFrame(CtpFrontTemplates.ConfirmPayload);
    }

    // —— ⑤ 认证请求 → ⑥ 认证响应（模板回放）——
    // ⑤ 与 ⑦ 共享 op 签名 E1 01 E2 30 10（body[0..5]）+ E3 reqId（body[6]）——按状态机区分。

    private byte[]? HandleAuth(CtpFrame frame)
    {
        var body = frame.Body;
        if (!MatchesSignature(body, "E101E23010"))
        {
            _log($"[CTP-Front] 非预期帧（等认证）: body前缀={HexPrefix(body)}");
            return null;
        }

        var reqId = ReqIdAfterOp(body);
        _brokerId = CtpFieldCodec.ExtractField(body, 0xFE) ?? "";
        _userId = CtpFieldCodec.ExtractField(body, 0xE7) ?? "";
        var appId = CtpFieldCodec.ExtractField(body, 0xE4) ?? "";

        _log($"[CTP-Front] 认证: broker={_brokerId} user={_userId} app={appId} reqId={reqId}");

        // 认证响应——接受（我们的交易所不做 AppId 校验）
        _state = State.WaitingLogin;
        return reqId is { } id
            ? BuildFrame(CtpFrontTemplates.BuildAuthRsp(_brokerId, _userId, appId, id))
            : null;
    }

    // —— ⑦ 登录请求 → ⑧ 登录预响应（模板回放；完整登录结果由 ⑩ 承载）——

    private async Task<byte[]?> HandleLoginAsync(CtpFrame frame, CancellationToken ct)
    {
        var body = frame.Body;
        if (!MatchesSignature(body, "E101E23010"))
        {
            _log($"[CTP-Front] 非预期帧（等登录）: body前缀={HexPrefix(body)}");
            return null;
        }

        _loginReqId = ReqIdAfterOp(body) ?? 0;
        _brokerId = CtpFieldCodec.ExtractField(body, 0xFE) ?? _brokerId;
        var appId = CtpFieldCodec.ExtractField(body, 0xE5) ?? "";
        // **⑦ 的 user 槽是定长 6 字节**（客户端 SDK 内部序列化实测：botmm→"botmm "补空格、
        // botnoise→"botnoi"截断——2026-10-12 服务器实测踩中：绑定被等宽污染的账户名 ⇒ 连到
        // 幽灵户 0 余额）。**绑定账户名以 ⑤ 认证帧的变长完整 user 为准**（状态机保证 ⑤ 先于 ⑦ 必达，
        // HandleAuthAsync 已存完整 _userId）——⑦ 的 user 解析结果丢弃。
        _userId = string.IsNullOrEmpty(_userId)
            ? CtpFieldCodec.ExtractField(body, 0xE7) ?? _userId
            : _userId;

        // 加密密码（E6 标记）——存密文当哈希（同密码+同挑战 ⇒ 同密文）
        var encryptedPwd = CtpFieldCodec.ExtractEncrypted(body);
        var pwdHex = encryptedPwd is { Length: > 0 }
            ? Convert.ToHexString(encryptedPwd)[..Math.Min(16, encryptedPwd.Length * 2)]
            : "";

        _log($"[CTP-Front] 登录: broker={_brokerId} user={_userId} app={appId} reqId={_loginReqId} pwd=({encryptedPwd?.Length ?? 0}B 密文)");

        try
        {
            // 进程分离（2026-10-05）：登录 = 首个交易所触点——连接 RPC + 登录。密码域（2026-10-09 双域）：
            // 前置走 FrontDomain=true（挑战响应密文域）——核心存/验 FrontPasswordHash 列，与 JSON-RPC
            // 明文域（钱包页提币）各自独立设密/补设，同一账户两扇门都通（此前单列互斥，前置开户的账户
            // 提币永远 90017）。登录绑定账户后，服务端把该账户的订单/成交推送路由到本连接——前置无需账户键与事件过滤。
            await _rpc.ConnectAsync(ct);
            _login = CtpDomainMapper.ToCtpLoginResult(await _rpc.LoginAsync(new LoginArg(_brokerId, _userId, pwdHex, FrontDomain: true), ct));
            _log($"[CTP-Front] 交易所登录成功: 交易日={_login.TradingDay} MaxOrderRef={_login.MaxOrderRef}");

            // ⑧ 只是预响应——交易日/FrontID/SessionID/MaxOrderRef 由 ⑩（随 ⑨ 系统信息请求下发）承载
            _state = State.Authenticated;
            _rpc.OrderPushed += OnRpcOrderPushed;       // M2：推送管道（按连接路由——只收本账户）
            _rpc.TradePushed += OnRpcTradePushed;
            return BuildFrame(CtpFrontTemplates.BuildLoginRsp(_brokerId, _userId));
        }
        catch (CtpBusinessException ex)
        {
            _log($"[CTP-Front] 登录失败: [{ex.ErrorId}] {ex.ErrorMsg}——关闭连接");
            _state = State.Closed;
            return null;
        }
        catch (Aether.Exchange.ExchangeBusinessException ex)
        {
            // SimRpcClient 抛 ExchangeBusinessException → 转 CtpBusinessException
            _log($"[CTP-Front] 登录失败: [{ex.ErrorId}] {ex.Message}——关闭连接");
            _state = State.Closed;
            return null;
        }
    }

    // —— ⑨ 之后：业务帧按 op 签名分派（顺序敏感：先长签名后短前缀）——

    private async Task<byte[]?> HandleBusinessAsync(CtpFrame frame, CancellationToken ct)
    {
        var body = frame.Body;
        var day = _login?.TradingDay ?? "";

        // ⑨ 系统信息请求: body[0..2] = E4 30 24（SDK 收到 ⑧ 后自动发）
        // → ⑩ 主帧（登录结果：交易日/FrontID/SessionID/MaxOrderRef）+ 伴随帧，两帧一次写回
        if (body.Length >= 3 && body[0] == 0xE4 && body[1] == 0x30 && body[2] == 0x24)
        {
            _log($"[CTP-Front] 系统信息请求 → 回登录结果(⑩)");
            var main = BuildFrame(CtpFrontTemplates.BuildSysInfoRsp(_brokerId, _userId, _loginReqId, day, _login?.MaxOrderRef ?? "0"));
            var both = new byte[main.Length + CtpFrontTemplates.SysInfoCompanionFrame.Length];
            main.CopyTo(both, 0);
            CtpFrontTemplates.SysInfoCompanionFrame.CopyTo(both, main.Length);
            return both;
        }

        // ⑪⑬ 结算确认请求: E1 01 E2 40 13；reqId 在 E1 01 E1 43 E3 之后（⑪=03 ⑬=04）。
        // 注意 E19CE3 是「响应」帧⑫的 reqId 回显字段——请求里没有；请求侧的 reqId 标签是 E143
        // （与 ⑮ 的 E12EE3 / ⑰ 的 E1A6E3 同构：E1 01 E1 <tag> E3 <reqId>，body[6] 是链路计数≠reqId）。
        if (MatchesSignature(body, "E101E24013"))
        {
            var reqId = ExtractReqIdAfter(body, "E101E143E3");
            var inst = NextCmdInstance();
            _log($"[CTP-Front] 结算确认 #{inst} reqId={reqId}");
            return reqId is { } id
                ? BuildFrame(CtpFrontTemplates.BuildSettleRsp(inst, id, _brokerId, _userId, day))
                : null;
        }

        // ⑮ 资金查询请求: E1 04 E2 80 06；reqId 在 E1 2E E3 之后（=05）。
        // 应答 E3 = 80 族记录流水（NextQrySeq——见字段注释；旧版回显请求计数在查报单缺席时会错位）。
        if (MatchesSignature(body, "E104E28006"))
        {
            var reqId = ExtractReqIdAfter(body, "E12EE3");
            var account = await _rpc.QueryAccountAsync(ct);   // 绑定账户（登录时绑定）——无需账户键
            _log($"[CTP-Front] 资金查询 reqId={reqId} balance={account.Balance}");
            return reqId is { } id
                ? BuildFrame(CtpFrontTemplates.BuildQryAccountRsp(NextQrySeq(), id, _brokerId, account.AccountId, _userId, day, account.Balance))
                : null;
        }

        // ⑰ 持仓查询请求: E1 04 E2 80 04；reqId 在 E1 A6 E3 之后（=06）。
        // 非空持仓：逐仓一行 ⑱（方向三元组/总仓量槽编辑——2026-10-03 SimNow 实抓模板）；
        // 空仓走原 13B 空表。多行边界：isLast 编码未逆向——首行即完成客户端收集。
        if (MatchesSignature(body, "E104E28004"))
        {
            var reqId = ExtractReqIdAfter(body, "E1A6E3");
            var positions = await _rpc.QueryPositionsAsync(null, ct);   // 绑定账户全量
            _log($"[CTP-Front] 持仓查询 reqId={reqId} → {positions.Count} 条");
            if (reqId is not { } id) return null;
            if (positions.Count == 0)
                return BuildFrame(CtpFrontTemplates.BuildQryPositionEmptyRsp(NextQrySeq(), id));
            var frames = positions.Select(p => BuildFrame(CtpFrontTemplates.BuildQryPositionRsp(
                NextQrySeq(), id, _brokerId, _userId, p.InstrumentId, p.ExchangeId, day,
                (char)p.PosiDirection, (char)p.PositionDate, p.TotalPosition))).ToArray();
            var total = frames.Sum(b => b.Length);
            var buf = new byte[total];
            var off = 0;
            foreach (var b in frames) { b.CopyTo(buf, off); off += b.Length; }
            return buf;
        }

        // 查成交请求: E1 04 E2 80 02（真前置 2026-10-24 实抓：请求 op=02、应答 op=03，reqId 在 E1 01 E1 CD E3 后）。
        // 本前置恒回空表（13B tag 帧）：交易所侧成交查询未接入（非空记录帧未逆向），但**必须应答**——
        // 不回会让标准客户端的查询管线白等 30s。空表与真前置同形逐字节一致。
        if (MatchesSignature(body, "E104E28002"))
        {
            var tradeReqId = ExtractReqIdAfter(body, "E101E1CDE3");
            _log($"[CTP-Front] 查成交 reqId={tradeReqId} → 13B payload tag 空表（真前置同款形状）");
            return tradeReqId is { } tid
                ? BuildFrame(CtpFrontTemplates.BuildQryTradeEmptyRsp(NextQrySeq(), tid))
                : null;
        }

        // 查报单请求: E1 04 E2 80 E4 <计数器>（⑤⑦ 族同款——计数在 op 字节不在 E3）；reqId 在 E1 01 E1 CD E3 后。
        // **必须应答**（恒空表——2026-10-03 双 bot 实测：未应答的查报单把客户端查询管线堵死 30s，
        // Lean 引擎 GetCashBalance 排队超时 → cash=0 → 引擎侧 buying power 拒掉全部订单）。
        // 真前置多行报单走 LZ4 二进制（不逆向）；空表（17B tag 帧）为 SimNow 过滤查询实抓模板，
        // E3 = 应答侧 80 族记录流水（NextQrySeq——二进制 6 行内部消耗 6 个号、空应答接续）。
        // <b>这是已知功能缺失</b>：标准 CTP 客户端的报单查询是「逐条回调 + bIsLast 收尾」
        // （官方 ThostFtdcTraderApi.h 的 OnRspQryOrder），有委托就该有行；本前置没有非空报单的帧模板，
        // 只能回空表 —— 账户真有活单时由 WarnIfOrdersExistAsync 喊进日志（2026-10-24）。
        if (MatchesSignature(body, "E104E280E4"))
        {
            var reqId = ExtractReqIdAfter(body, "E101E1CDE3");
            WarnIfOrdersExistAsync();   // 有活单却回空表 ⇒ 必须喊出来（见方法注释）
            _log($"[CTP-Front] 查报单 reqId={reqId} → 13B payload tag 空表（17B 线上；2026-10-24 真前置实抓逐字节一致，SDK 30s 超时后管线恢复——真前置同款行为）");
            return reqId is { } id
                ? BuildFrame(CtpFrontTemplates.BuildQryOrderEmptyRsp(NextQrySeq(), id))
                : null;
        }

        // 终端信息上报: E4 30 E6 05 01 31（SDK 每次登录自动发、真前置不应答——2026-10-03 录制实证）。
        // 识别降噪：不再落「未知业务帧」（fire-and-forget，无应答帧）。
        if (body.Length >= 5 && body[0] == 0xE4 && body[1] == 0x30 && body[2] == 0xE6 && body[3] == 0x05 && body[4] == 0x01)
        {
            _log("[CTP-Front] 终端信息上报（真前置不应答，忽略）");
            return null;
        }

        // 查合约: E1 04 E2 80 2E —— 板子数据源（2026-10-02 过滤查询抓包定案：真前置按应答大小选格式，
        // 大清单走 LZ4 批量二进制（不逆向）；本地上市表小 ⇒ tag 模板逐合约一帧同写）。多行收集在 wrapper 侧
        // 静默期收尾（2026-10-05 探针定案：tag 帧恒 isLast=true、无 false 编码——多行走 wrapper 收集而非帧内
        // 标志；见 CtpTrader.OnQueryItem 注释）。请求侧 reqId 标签族 E1 01 01 5D E3（按查询类型各异），
        // instance 回显请求值（请求 op 后 E3 <n>）。
        if (MatchesSignature(body, "E104E2802E"))
        {
            var reqId = ExtractReqIdAfter(body, "E101015DE3");
            var listed = await _rpc.QueryInstrumentsAsync(ct);   // Domain CtpInstrument（板子只读 id/交易所）
            _log($"查合约 reqId={reqId} → 上市 {listed.Count} 个: {string.Join(",", listed.Select(i => i.InstrumentId))}");
            if (reqId is not { } qryId) return null;
            if (listed.Count == 0) return null;
            var frames = listed.Select(i =>
                BuildFrame(CtpFrontTemplates.BuildQryInstrumentRsp(NextQrySeq(), qryId, i.InstrumentId, i.ExchangeId))).ToArray();
            var total = frames.Sum(b => b.Length);
            var buf = new byte[total];
            var off = 0;
            foreach (var b in frames) { b.CopyTo(buf, off); off += b.Length; }
            return buf;
        }

        // ⑲ 报单: E1 01 E2 40 E4 <计数器> —— 末字节随请求数递增（v2 抓包三单 = 05/06/07），匹配 5 字节前缀。
        // 成功 = 无应答帧（真 CTP 语义，2026-10-01 补抓包实证）——OnRtnOrder 推送经捕获缓冲随应答槽排空；
        // 风控拒单 = ⑳ 两子帧一次写（4001 + F003——F003 是 C# 层拒单唯一可见信号 → RspError）。
        if (MatchesSignature(body, "E101E240E4"))
        {
            var o = CtpOrderWireCodec.ParseOrderInsert(body);
            if (o is null)
            {
                _log($"[CTP-Front] 报单解析失败: body={Convert.ToHexString(body)}");
                return null;
            }
            _log($"[CTP-Front] 报单: ref={o.OrderRef} {o.InstrumentId} {(o.Direction == '0' ? "买" : "卖")}{(o.OffsetFlag == '0' ? "开" : "平")} {o.Volume}@{o.LimitPrice} TC={(char)o.TimeCondition} reqId={o.ReqId} body={Convert.ToHexString(body)}");
            try
            {
                // 绑定账户插入（OrderRef 直通）；应答结果弃（成功 = 无应答帧——真 CTP 语义）。
                // RPC 侧服务端的 capture-during-dispatch 保证应答帧先于本单推送写 wire；本会话捕获窗
                // 跨 await 覆盖推送事件 ⇒ 排空次序保持「应答先于推送」。
                await _rpc.InsertOrderAsync(CtpDomainMapper.ToDomainOrderRequest(new CtpOrderRequest {
                    BrokerId = _brokerId, InvestorId = _userId, InstrumentId = o.InstrumentId,
                    Direction = (CtpDirection)o.Direction, OffsetFlag = (CtpOffsetFlag)o.OffsetFlag,
                    PriceType = o.PriceType == '2' ? CtpOrderPriceType.Limit : CtpOrderPriceType.Market,   // '0'/'1' 双宽容（D7）
                    LimitPrice = o.LimitPrice, Volume = o.Volume, OrderRef = o.OrderRef,
                    // TimeCondition 必须透传（2026-10-02 双 bot 冻结根因之二）：CtpOrderRequestMapper 把
                    // 市价单映射成「对手价限价 + IOC」（吃不挂、残量作废）；漏传 ⇒ 核心按默认 GFD 入簿
                    // ⇒ 噪声账户残量单滞留簿上、后续同价单自成交跳过 ⇒ 市场冻死（mapper 注释里实测过的失败模式）。
                    TimeCondition = (CtpTimeCondition)o.TimeCondition,
                }), ct);
                return null;   // 无应答帧——推送走捕获缓冲（DispatchFrameAsync 的 10ms 让路护客户端 tracker 注册）
            }
            catch (Exception ex) when (ex is CtpBusinessException or Aether.Exchange.ExchangeBusinessException)
            {
                var cex = AsCtp(ex);
                _log($"[CTP-Front] 报单拒绝: [{cex.ErrorId}] {cex.ErrorMsg}");
                var inst = NextCmdInstance();
                var echo = (_userId, _brokerId, o.InstrumentId, o.OrderRef, o.PriceType, o.Direction, o.OffsetFlag, o.LimitPrice, o.Volume, o.TimeCondition);
                var (wireId, wireMsg) = WireError(cex);
                var rsp = BuildFrame(CtpFrontTemplates.BuildOrderInsertRspErr(inst, o.ReqId, wireId, wireMsg,
                    echo.Item1, echo.Item2, echo.Item3, echo.Item4, echo.Item5, echo.Item6, echo.Item7, echo.Item8, echo.Item9, echo.Item10));
                var rtn = BuildFrame(CtpFrontTemplates.BuildErrRtnOrderInsert(NextRtnInstance(), wireId, wireMsg,
                    echo.Item1, echo.Item2, echo.Item3, echo.Item4, echo.Item5, echo.Item6, echo.Item7, echo.Item8, echo.Item9, echo.Item10));
                var both = new byte[rsp.Length + rtn.Length];
                rsp.CopyTo(both, 0); rtn.CopyTo(both, rsp.Length);
                return both;
            }
        }

        // ㉑ 撤单: E1 01 E2 40 06 —— core.CancelOrder（按 OrderRef 字符串匹配）。
        // 成功 = 4007 单帧（成功 RspInfo）+ OnRtnOrder('5') 推送；失败 = 4007+F004 两帧一次写。
        if (MatchesSignature(body, "E101E240") && body.Length > 4 && body[4] == 0x06)
        {
            var a = CtpOrderWireCodec.ParseOrderAction(body);
            if (a is null)
            {
                _log($"[CTP-Front] 撤单解析失败: body={Convert.ToHexString(body)}");
                return null;
            }
            _log($"[CTP-Front] 撤单: ref={a.OrderRef} reqId={a.ReqId} body={Convert.ToHexString(body)}");
            var inst = NextCmdInstance();   // 40 族严格序列——一次自增，OK/ERR 共用（空跳一格会被客户端拒收）
            try
            {
                await _rpc.CancelOrderAsync(CtpDomainMapper.ToDomainCancelKey(new CtpOrderRef(_brokerId, _userId, 0, 0, a.OrderRef, "")), ct);
                return BuildFrame(CtpFrontTemplates.BuildOrderActionRspOk(inst, a.ReqId, _userId, _brokerId, a.OrderRef));
            }
            catch (Exception ex) when (ex is CtpBusinessException or Aether.Exchange.ExchangeBusinessException)
            {
                var cex = AsCtp(ex);
                _log($"[CTP-Front] 撤单拒绝: [{cex.ErrorId}] {cex.ErrorMsg}");
                var rsp = BuildFrame(CtpFrontTemplates.BuildOrderActionRspErr(inst, a.ReqId, WireError(cex).Id, WireError(cex).Msg, _userId, _brokerId, a.OrderRef));
                var rtn = BuildFrame(CtpFrontTemplates.BuildErrRtnOrderAction(NextRtnInstance(), WireError(cex).Id, WireError(cex).Msg, _userId, _brokerId, a.OrderRef));
                var both = new byte[rsp.Length + rtn.Length];
                rsp.CopyTo(both, 0); rtn.CopyTo(both, rsp.Length);
                return both;
            }
        }

        _log($"[CTP-Front] 未知业务帧: body前缀={HexPrefix(body)} len={body.Length}");
        return null;
    }

    // —— 提取工具 ——

    /// <summary>op 签名前缀匹配（hex → bytes → SequenceEqual）。</summary>
    private static bool MatchesSignature(ReadOnlySpan<byte> body, string sigHex)
    {
        var sig = Convert.FromHexString(sigHex);
        return body.Length >= sig.Length && body[..sig.Length].SequenceEqual(sig);
    }

    /// <summary>⑤/⑦ 共享 op 签名的 reqId：body[5]==E3 时 body[6] 即 reqId。</summary>
    private static byte? ReqIdAfterOp(ReadOnlySpan<byte> body)
        => body.Length > 6 && body[5] == 0xE3 ? body[6] : null;

    /// <summary>定位 needle 后紧跟的 1B reqId（⑪⑬ E19CE3 / ⑮ E12EE3 / ⑰ E1A6E3）。</summary>
    private byte? ExtractReqIdAfter(ReadOnlySpan<byte> body, string needleHex)
    {
        var needle = Convert.FromHexString(needleHex);
        var hit = body.IndexOf(needle);
        if (hit < 0 || hit + needle.Length >= body.Length)
        {
            _log($"[CTP-Front] reqId needle {needleHex} 未命中");
            return null;
        }
        return body[hit + needle.Length];
    }

    private static string HexPrefix(ReadOnlySpan<byte> body)
        => Convert.ToHexString(body[..Math.Min(body.Length, 16)]);

    // —— 帧构造 ——

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
