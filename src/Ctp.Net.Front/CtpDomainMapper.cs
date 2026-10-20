using System;
using System.Collections.Generic;
using System.Linq;
using Ctp.Net;

namespace Ctp.Net.Front;

/// <summary>
/// Exchange Domain ↔ Ctp.Net 类型转换器。
/// <para>
/// 设计原则：Exchange 核心零 CTP 协议依赖，Domain 枚举 byte 值精确镜像 CTP char 码
/// （<c>(CtpDirection)(byte)</c> 强转零成本）；本类作为 Front 适配层的唯一转换入口。
/// </para>
/// </summary>
public static class CtpDomainMapper
{
    // —— Domain → Ctp ——

    public static CtpLoginResult ToCtpLoginResult(Aether.Exchange.ExchangeLoginResult d)
        => new(d.TradingDay, d.LoginTime, d.FrontId, d.SessionId, d.MaxOrderRef);

    public static CtpAccount ToCtpAccount(Aether.Exchange.Account d) => new()
    {
        AccountId = d.AccountId, Balance = d.Balance, Available = d.Available,
        WithdrawQuota = d.WithdrawQuota, FrozenMargin = d.FrozenMargin,
        FrozenCash = d.FrozenCash, Commission = d.Commission,
        CurrMargin = d.CurrMargin, PositionProfit = d.PositionProfit,
        CloseProfit = d.CloseProfit,
    };

    public static CtpPosition ToCtpPosition(Aether.Exchange.Position d) => new()
    {
        InstrumentId = d.InstrumentId, ExchangeId = d.ExchangeId,
        PosiDirection = (CtpPosiDirection)(byte)d.PosiDirection,
        HedgeFlag = (CtpHedgeFlag)(byte)d.HedgeFlag,
        PositionDate = (CtpPositionDate)(byte)d.PositionDate,
        YdPosition = d.YdPosition, TotalPosition = d.TotalPosition,
        TodayPosition = d.TodayPosition, UseMargin = d.UseMargin,
        PositionProfit = d.PositionProfit, CloseProfit = d.CloseProfit,
    };

    public static CtpOrder ToCtpOrder(Aether.Exchange.Order d) => new()
    {
        InstrumentId = d.InstrumentId, ExchangeId = d.ExchangeId,
        OrderRef = d.OrderRef, FrontId = d.FrontId, SessionId = d.SessionId,
        OrderSysId = d.OrderSysId,
        Direction = (CtpDirection)(byte)d.Direction,
        OffsetFlag = (CtpOffsetFlag)(byte)d.OffsetFlag,
        PriceType = (CtpOrderPriceType)(byte)d.PriceType,
        LimitPrice = d.LimitPrice, VolumeTotalOriginal = d.VolumeTotalOriginal,
        VolumeTraded = d.VolumeTraded, VolumeTotal = d.VolumeTotal,
        OrderStatusRaw = d.OrderStatusRaw, InsertTime = d.InsertTime,
        UpdateTime = d.UpdateTime, StatusMsg = d.StatusMsg,
    };

    public static CtpTrade ToCtpTrade(Aether.Exchange.Trade d) => new()
    {
        InstrumentId = d.InstrumentId, OrderRef = d.OrderRef, TradeId = d.TradeId,
        OrderSysId = d.OrderSysId,
        Direction = (CtpDirection)(byte)d.Direction,
        OffsetFlag = (CtpOffsetFlag)(byte)d.OffsetFlag,
        Price = d.Price, Volume = d.Volume, TradeTime = d.TradeTime,
    };

    public static CtpTick ToCtpTick(Aether.Exchange.Tick d) => new()
    {
        InstrumentId = d.InstrumentId, ExchangeId = d.ExchangeId,
        TradingDay = d.TradingDay, ActionDay = d.ActionDay,
        UpdateTime = d.UpdateTime, UpdateMillisec = d.UpdateMillisec,
        LastPrice = d.LastPrice, Volume = d.Volume, OpenInterest = d.OpenInterest,
        Turnover = d.Turnover, BidPrice = d.BidPrice, BidVolume = d.BidVolume,
        AskPrice = d.AskPrice, AskVolume = d.AskVolume,
        PreSettlement = d.PreSettlement, PreClose = d.PreClose,
        OpenPrice = d.OpenPrice, Highest = d.Highest, Lowest = d.Lowest,
    };

    public static CtpInstrument ToCtpInstrument(Aether.Exchange.Instrument d) => new()
    {
        InstrumentId = d.InstrumentId, ExchangeId = d.ExchangeId,
        VolumeMultiple = d.VolumeMultiple, PriceTick = d.PriceTick,
        PreSettlement = d.PreSettlement, BasePrice = d.BasePrice,
    };

    // —— Ctp → Domain ——

    public static Aether.Exchange.OrderRequest ToDomainOrderRequest(CtpOrderRequest c)
        => new()
        {
            BrokerId = c.BrokerId,
            InvestorId = c.InvestorId,
            InstrumentId = c.InstrumentId,
            Direction = (Aether.Exchange.Direction)(byte)c.Direction,
            OffsetFlag = (Aether.Exchange.OffsetFlag)(byte)c.OffsetFlag,
            PriceType = (Aether.Exchange.OrderPriceType)(byte)c.PriceType,
            LimitPrice = c.LimitPrice,
            Volume = c.Volume,
            TimeCondition = c.TimeCondition switch
            {
                CtpTimeCondition.GFD => Aether.Exchange.TimeCondition.GFD,
                CtpTimeCondition.IOC => Aether.Exchange.TimeCondition.IOC,
                CtpTimeCondition.FOK => Aether.Exchange.TimeCondition.FOK,
                _ => Aether.Exchange.TimeCondition.GFD,
            },
            ClientOrderRef = c.OrderRef,
        };

    public static Aether.Exchange.CancelKey ToDomainCancelKey(CtpOrderRef c)
        => new(c.BrokerId, c.InvestorId, c.FrontId, c.SessionId, c.OrderRef, c.ExchangeId);

    public static Aether.Exchange.ExchangeBusinessException ToDomainException(CtpBusinessException ex)
        => new(ex.ErrorId, ex.Message);

    public static CtpBusinessException ToCtpException(Aether.Exchange.ExchangeBusinessException ex)
        => new(ex.ErrorId, ex.Message);
}
