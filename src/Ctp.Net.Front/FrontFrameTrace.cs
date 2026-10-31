namespace Ctp.Net.Front;

/// <summary>
/// 原始帧十六进制落盘开关（诊断用，<b>默认关</b>）。
/// <para>
/// <b>为什么必须默认关（2026-10-31 定案）</b>：前置把每一帧出站/入站字节都 <c>Convert.ToHexString</c>
/// 后写日志，而写日志的地方就在**出站写循环里**、且宿主用 <c>Console.WriteLine</c> 重定向到文件
/// （每行 flush）。做市商每秒几十帧 ⇒ 每秒几十次「格式化 + 同步写盘」压在唯一写 TCP 的线程上。
/// 实测后果不是日志变大这么简单：磁盘一有抖动，写循环就跟着卡，所有应答（报单回报、查询应答、
/// 心跳应答）排队发不出去 ⇒ 客户端那边表现成「会话半死」——下单 10s 无回报、查持仓 5s 无应答、
/// 查报单 8s 超时，再撑十几秒 CTP API 自己判死并拆链路（`前置断开 reason=4097`）。
/// 一天 443MB 的 front.log 就是这条路径的副产品。
/// </para>
/// <para>
/// 要看字节时把环境变量 <c>AETHER_CTP_FRAME_TRACE=1</c> 打开即可（排障现场够用）；语义日志
/// （会话/登录/报单/撤单/拒单）**不受这个开关影响**，始终记录。
/// </para>
/// </summary>
internal static class FrontFrameTrace
{
    internal static readonly bool Enabled =
        Environment.GetEnvironmentVariable("AETHER_CTP_FRAME_TRACE") is "1" or "true";
}
