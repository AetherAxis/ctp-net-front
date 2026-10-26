using Aether.Exchange;

namespace Ctp.Net.Front.Tests;

/// <summary>
/// 测试夹具品种注册（<see cref="InstrumentCatalog.RegisterVariety"/>）——生产品种表 2026-10-26 起
/// 只带 ax，本仓测试用的 ag/m/rb/cu 夹具元数据（SessionHarness/FrontLoopback 及 E2E 全部数值断言
/// 按此校准）在程序集加载时注册。值 = 被移除前的品种表原值，一个字符没动。
/// </summary>
internal static class TestVarietyInit
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register()
    {
        // ag：SessionHarness.Instrument = ag2610（×15 / 12% / tick 1 / SHFE / 基价 7000 / 昨结 6990）
        InstrumentCatalog.RegisterVariety(new SimInstrument
        {
            InstrumentId = "ag", ExchangeId = "SHFE",
            VolumeMultiple = 15, MarginRate = 0.12, PriceTick = 1,
            BasePrice = 7000, PreSettlement = 6990,
        });
        // m：E2E M2/M3 与 codec/width 测试的 m2701（×10 / 8% / tick 1 / DCE / 基价 3000 / 昨结 2998）
        InstrumentCatalog.RegisterVariety(new SimInstrument
        {
            InstrumentId = "m", ExchangeId = "DCE",
            VolumeMultiple = 10, MarginRate = 0.08, PriceTick = 1,
            BasePrice = 3000, PreSettlement = 2998,
        });
        // rb/cu：SessionAdapterTests 多合约用例（rb2610 / cu2610）
        InstrumentCatalog.RegisterVariety(new SimInstrument
        {
            InstrumentId = "rb", ExchangeId = "SHFE",
            VolumeMultiple = 10, MarginRate = 0.10, PriceTick = 1,
            BasePrice = 3200, PreSettlement = 3198,
        });
        InstrumentCatalog.RegisterVariety(new SimInstrument
        {
            InstrumentId = "cu", ExchangeId = "SHFE",
            VolumeMultiple = 5, MarginRate = 0.12, PriceTick = 10,
            BasePrice = 78000, PreSettlement = 77900,
        });
    }
}
