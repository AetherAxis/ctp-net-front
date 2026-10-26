using Aether.Exchange;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// 测试夹具品种注册（<see cref="InstrumentCatalog.RegisterVariety"/>）——生产品种表 2026-10-26 起
/// 只带 ax，本测试仓的 ag2610 夹具（SessionHarness/FrontLoopback 全部数值断言按此校准：
/// ×15 / 12% / tick 1 / SHFE / 基价 7000 / 昨结 6990）在程序集加载时注册。值 = 被移除前的品种表原值。
/// </summary>
internal static class TestVarietyInit
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register()
    {
        InstrumentCatalog.RegisterVariety(new SimInstrument
        {
            InstrumentId = "ag", ExchangeId = "SHFE",
            VolumeMultiple = 15, MarginRate = 0.12, PriceTick = 1,
            BasePrice = 7000, PreSettlement = 6990,
        });
    }
}
