namespace Ctp.Net.Front;

/// <summary>
/// 「状态不确定」动作账本——<b>前置进程级</b>，跨 CTP 会话存活（2026-10-29）。
/// <para>
/// <b>为什么需要它</b>：撤单/报单是一次 RPC 往返。RPC 在往返途中断开（交易所重启、网络抖动、
/// 交易所应答超时销毁连接）时，前置无法知道单子在交易所侧到底落没落地——旧实现把这种「不确定」
/// 直接当成失败回给客户端，动作就此蒸发。2026-10-29 之前交易所里积下的 54,738 张从未撤掉的 GFD 买单
/// 就是这个缺口（断连瞬间仍在飞的撤单随 socket 一起消失，客户端以为撤成功）。
/// </para>
/// <para>
/// <b>怎么补</b>：把「不确定」的动作记在账本里（按账户），该账户**下一次登录**（SDK 自动重连即触发）
/// 时由会话对账：撤单直接重放（撤单幂等——单子不在/已终态都算完成），不确定的报单先去查，
/// 若已落地且仍活着就当作孤儿单撤掉（客户端已经收到失败，不会有人再管它）。
/// </para>
/// <para>
/// 账本只记**标识**（账户 / OrderRef / 合约），不记凭据、不记价格——对账只需能定位单子。
/// 容量与时效双封顶：单账户每类 {@value MaxPerKindPerAccount} 条、{@value TtlMinutes} 分钟过期
/// （过期的无法对账，只能记日志——宁可漏对账也不能无界吃内存）。
/// </para>
/// </summary>
internal sealed class FrontPendingLedger
{
    /// <summary>单账户每类（撤单 / 报单）最多记多少条——单账户活单不会超过量级，超出说明前置侧出了怪事。</summary>
    internal const int MaxPerKindPerAccount = 512;

    /// <summary>账本条目存活时长。超过即视为无法对账（对方大概率早已重连过并重放过）而丢弃。</summary>
    internal const int TtlMinutes = 10;

    /// <summary>一条待对账动作。<see cref="InstrumentId"/> 只有报单才可能有（撤单按 OrderRef 定位即可）。</summary>
    internal sealed record Pending(string OrderRef, string? InstrumentId, DateTimeOffset At);

    private readonly object _gate = new();
    private readonly Dictionary<string, List<Pending>> _cancels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Pending>> _inserts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<string>? _log;

    internal FrontPendingLedger(Func<DateTimeOffset>? now = null, Action<string>? log = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _log = log;
    }

    /// <summary>记一笔「撤单可能没到交易所」。同账户同 OrderRef 重复记只留一条（幂等）。</summary>
    internal void RecordCancel(string accountId, string orderRef)
    {
        if (string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(orderRef)) return;
        Add(_cancels, accountId, new Pending(orderRef, null, _now()));
    }

    /// <summary>记一笔「报单可能已落地、但客户端拿到的是失败」。对账时若它真的活着就撤掉（孤儿单清理）。</summary>
    internal void RecordInsert(string accountId, string orderRef, string? instrumentId)
    {
        if (string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(orderRef)) return;
        Add(_inserts, accountId, new Pending(orderRef, instrumentId, _now()));
    }

    /// <summary>取走该账户全部待补撤单（取走即删——调用方负责重放，失败的自行 <see cref="RecordCancel"/> 放回）。</summary>
    internal IReadOnlyList<Pending> TakeCancels(string accountId)
        => Take(_cancels, accountId);

    /// <summary>取走该账户全部待确认报单。</summary>
    internal IReadOnlyList<Pending> TakeInserts(string accountId)
        => Take(_inserts, accountId);

    /// <summary>诊断/测试用：该账户当前积压条数（撤单, 报单）。</summary>
    internal (int Cancels, int Inserts) CountFor(string accountId)
    {
        lock (_gate)
        {
            var at = _now();
            return (_cancels.TryGetValue(accountId, out var c) ? Valid(c, at).Count : 0,
                    _inserts.TryGetValue(accountId, out var i) ? Valid(i, at).Count : 0);
        }
    }

    private void Add(Dictionary<string, List<Pending>> map, string accountId, Pending p)
    {
        lock (_gate)
        {
            if (!map.TryGetValue(accountId, out var list))
                map[accountId] = list = new List<Pending>();

            var at = p.At;
            list.RemoveAll(x => x.OrderRef == p.OrderRef || at - x.At > TimeSpan.FromMinutes(TtlMinutes));
            if (list.Count >= MaxPerKindPerAccount)
            {
                // 满了丢最老的——最新的更可能还没被对账过。
                list.RemoveAt(0);
                _log?.Invoke($"[CTP-Front] 待对账账本已满（账户 {accountId}，上限 {MaxPerKindPerAccount}），丢弃最旧一条");
            }
            list.Add(p);
        }
    }

    private IReadOnlyList<Pending> Take(Dictionary<string, List<Pending>> map, string accountId)
    {
        lock (_gate)
        {
            if (!map.TryGetValue(accountId, out var list) || list.Count == 0) return Array.Empty<Pending>();
            map.Remove(accountId);
            var at = _now();
            var valid = Valid(list, at);
            var expired = list.Count - valid.Count;
            if (expired > 0)
                _log?.Invoke($"[CTP-Front] 待对账账本：账户 {accountId} 有 {expired} 条超过 {TtlMinutes} 分钟，无法对账，已丢弃");
            return valid;
        }
    }

    private static List<Pending> Valid(List<Pending> list, DateTimeOffset at)
        => list.Where(x => at - x.At <= TimeSpan.FromMinutes(TtlMinutes)).ToList();
}
