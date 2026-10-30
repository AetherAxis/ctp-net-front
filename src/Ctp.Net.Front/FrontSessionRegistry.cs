namespace Ctp.Net.Front;

/// <summary>
/// 同账户活动会话登记（2026-10-29）——<b>同账户重复登录踢掉旧会话</b>，与真柜台行为一致。
/// <para>
/// <b>为什么必须做</b>：交易所侧的 <c>SessionID</c> 恒为 1、`OrderRef` 只在账户内唯一，而撤单只按
/// <c>(账户, OrderRef)</c> 定位（交易所账本 `_byOrderRef[orderRef]` 是覆盖写）。两条同账户会话并存时：
/// ① 新会话分配的 OrderRef 序号可能回卷并**静默顶掉**旧会话仍活着的挂单（旧单从查询里消失、也撤不掉）；
/// ② 旧会话的撤单可能撤掉新会话的单。真 CTP 的答案是「同一投资者重复登录 ⇒ 踢掉旧会话」，
/// 前置是唯一能做这件事的地方（交易所被设计成不认识会话）。
/// </para>
/// </summary>
internal sealed class FrontSessionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CtpFrontSession> _active = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 登记本账户的当前会话，返回被顶掉的旧会话（没有则 null——调用方负责踢它，别在锁里做 I/O）。
    /// </summary>
    internal CtpFrontSession? Register(string accountId, CtpFrontSession session)
    {
        if (string.IsNullOrEmpty(accountId)) return null;
        lock (_gate)
        {
            _active.TryGetValue(accountId, out var previous);
            _active[accountId] = session;
            return ReferenceEquals(previous, session) ? null : previous;
        }
    }

    /// <summary>会话结束时注销（只注销「登记的还是我自己」的情况——避免把刚接手的新会话误摘）。</summary>
    internal void Unregister(string accountId, CtpFrontSession session)
    {
        if (string.IsNullOrEmpty(accountId)) return;
        lock (_gate)
        {
            if (_active.TryGetValue(accountId, out var current) && ReferenceEquals(current, session))
                _active.Remove(accountId);
        }
    }

    /// <summary>诊断/测试用：某账户当前登记的会话数（0 或 1）。</summary>
    internal int CountFor(string accountId)
    {
        lock (_gate) return _active.ContainsKey(accountId) ? 1 : 0;
    }
}
