# 客户端功能矩阵：标准 CTP 客户端 → 真前置 → 本前置

> 口径：**以官方 SDK 的请求方法为准**（`ThostFtdcTraderApi.h` 交易 111 个 `Req*` + `ThostFtdcMdApi.h` 行情 7 个），
> 再对齐「本前置实现了哪些」「② 客户端导出面能发哪些」「真前置抓包实证过哪些」。
> 数据来源：SDK 头文件枚举、`CtpFrontSession`/`CtpFrontMdSession` 的签名分支、`CtpTrader` 的公开方法、
> `~/aether-bots/simnow/capture/`（真前置实抓，本地留存、永不入 git）。
> 最后更新：2026-10-24。

## 0. 总账

| 面 | 总量 | 已实现 | 说明 |
|---|---|---|---|
| 交易请求（`Req*`） | 111 | **12** | 登录/认证/结算确认/报单/撤单/资金/持仓/报单查询/成交查询/合约查询 + 系统信息/终端信息/心跳 |
| 行情请求 | 7 | **2** | 登录 + 订阅（退订未实现） |
| 正常客户端日常会用到的 | ~30 | **12** | 约 40% |
| 服务端推送类型 | ~12 | **5** | RtnOrder / RtnTrade / RtnDepthMarketData / ErrRtnOrderInsert / ErrRtnOrderAction（+ RspError） |

**双向卡点**：② `CtpTrader` 自己只导出了 8 个交易方法（登录、结算确认、查资金、查持仓、查合约、报单、撤单、查报单）
＋ 本次新增 `QueryTradeAsync`（查成交）。所以「前置没实现」的项里，有相当一部分**我们的客户端根本发不出来**——
要抓真帧必须先扩 ②。

## 1. 已实现（真前置实抓验证过）

| # | 官方请求 | 请求 op（抓包） | 前置处理 | 验证 |
|---|---|---|---|---|
| 1 | `ReqAuthenticate` | `E101E23010` | ⑤认证 → ⑥应答（固定挑战，密文当哈希） | 真前置 x38，ErrorID=0 |
| 2 | `ReqUserLogin` | `E101E23010` | ⑦登录 → ⑩主帧 + 伴随帧 | 解出 FrontID/SessionID/MaxOrderRef |
| 3 | （SDK 自动）系统信息 | `E43024E501` / `E43027E501` | ⑨ 应答 | 真前置同款 |
| 4 | （SDK 自动）终端信息采集 | `E430E60501` | 识别并**不应答**（与真前置一致） | 抓包实证 |
| 5 | `ReqSettlementInfoConfirm` | `E101E24013` | ⑪⑬ → 65B payload | 真前置逐字节一致（golden 测试） |
| 6 | `ReqQryTradingAccount` | `E104E28006` | ⑮ 资金 → 134B payload | golden 测试 + 实值 999927.69 |
| 7 | `ReqQryInvestorPosition` | `E104E28004` | ⑰ 持仓 → 逐仓一帧（多行已验证） | 真前置两合约两仓 ⇒ 两行 |
| 8 | `ReqQryInstrument` | `E104E2802E` | 查合约 → 板子表 | 真前置 17335 行走二进制，客户端兼容 tag 逐行 |
| 9 | `ReqOrderInsert` | `E101E240E4` | ⑲ 报单（成功无应答 + RtnOrder 推送；风控拒单 ⑳ 两帧） | 真前置实下单 |
| 10 | `ReqOrderAction` | `E101E24006` | ㉑ 撤单（**必须带 InstrumentID**，见 core DESIGN §5.1） | 真前置实撤成功 |
| 11 | `ReqQryOrder` | `E104E280E4` | ⑯ 空表 13B tag 帧；非空**逐行 tag 记录帧**（442B 模板，2026-10-24 逆向完成） | 空表逐字节一致 + 单行/多行 E2E |
| 12 | `ReqQryTrade` | `E104E28002` | 查成交 → **回 13B 空表**（2026-10-24 新增，真前置同形逐字节） | 真前置实抓空帧 + E2E |
| 13 | 心跳 | 6B / 5B | 原样回射 | 不回射 ⇒ 30–60s 停摆（实证） |
| 14 | `Md ReqUserLogin` | — | 行情登录 | 实连成功 |
| 15 | `SubscribeMarketData` | `E44401` | 订阅 → tick 推送 | 实订成功 |
| 16 | （推送）`RtnOrder`/`RtnTrade`/`RtnDepthMarketData` | — | 已实现 | bot 实跑 |

## 2. 已实现但有缺口

| 项 | 缺口 | 备注 |
|---|---|---|
| `ReqQryOrder` 非空 | **已实现**（2026-10-24）；剩余边界见下 | 真前置 1 行回 tag 记录帧、≥2 行走列式二进制；本前置恒用 tag 记录帧逐行。边界：① `VolumeTraded`/`VolumeTotal` 槽位未定位（沿用模板 0/1，部分成交的成交数靠 `OnRtnTrade` 推送收敛）② 状态消息槽定宽 38B（改宽整帧被 SDK 丢弃）③ 账本无日期维度 ⇒ 活单优先 + 封顶 200 行 |
| `ReqQryTrade` 非空 | 只回空表 | 同上（非空记录帧需要一笔真实成交才能抓） |
| `ReqQryInstrument` 空结果 | 过滤到不存在的合约 ⇒ 客户端 30s 超时 | 真前置其实会派发「空记录 + isLast」，但**本前置这条路不敢转发**（见 §4 的崩溃证据） |
| 行情退订 `UnSubscribeMarketData` | 未实现 | 客户端与前置都没有 |
| tick 字段 | 开高低/昨结昨收缺 | 成交量/档量/持仓量已真实化 |

## 3. 正常客户端会用、但没实现（按优先级）

| # | 官方请求 | 用途 | 卡点 |
|---|---|---|---|
| 1 | `ReqQrySettlementInfo` 查结算单 | 结算单/出入金核查 | ② 未导出 |
| 2 | `ReqQrySettlementInfoConfirm` | 今日是否已确认 | ② 未导出 |
| 3 | `ReqQryInvestor` 查投资者 | 客户信息/权限 | ② 未导出 |
| 4 | `ReqQryExchange` 查交易所 | 交易所列表 | ② 未导出 |
| 5 | `ReqQryTradingCode` 查交易编码 | 多编码账户 | ② 未导出 |
| 6 | `ReqQryInstrumentMarginRate` 保证金率 | 开仓前算占用 | ② 未导出 |
| 7 | `ReqQryInstrumentCommissionRate` 手续费率 | 成本预估 | ② 未导出 |
| 8 | `ReqQryInvestorPositionDetail` 持仓明细 | 平今/平昨判定 | ② 未导出 |
| 9 | `ReqQryProduct` 查产品 | 品种分组 | ② 未导出 |
| 10 | `ReqQryMaxOrderVolume` 最大下单量 | 下单前校验 | ② 未导出 |
| 11 | `ReqQryNotice` / `ReqQryTradingNotice` | 首次登录弹公告 | ② 未导出 |
| 12 | `ReqUserPasswordUpdate` / `ReqTradingAccountPasswordUpdate` | 改密 | ② 未导出 |
| 13 | `ReqUserLogout` | 正常退出握手 | 前置未识别（落「未知业务帧」） |
| 14 | `ReqQryClassifiedInstrument` | 新版客户端筛期权 | ② 未导出 |
| 15 | 银期/转账族（`QryTransferBank`/`TransferSerial`/`ContractBank`/`QueryBankAccountMoneyByFuture`/`FromBankToFutureByFuture`/`FromFutureToBankByFuture`） | 出入金 | 本仓不做资金 |

## 4. 关键教训：不能盲目「补齐空回调」

真前置在**空结果**时会回调 `OnRspQryXxx(pField=nullptr, bIsLast=true)`。本仓 wrapper 原先对
`QryOrder` / `QryInstrument` 用 `if (!cb || !pField) return;` **吞掉**该回调 ⇒ 查询永不完成 ⇒ 客户端 30s 超时。

2026-10-24 尝试改为「转发空记录」后发现：

- `QryTrade` / `QryAccount` / `QryPosition`：转发正常，客户端收到 0 行并**立即完成**（查成交就是靠这条跑通的）。
- `QryOrder` / `QryInstrument`：转发后 **SDK 交出的记录指针无效**，C# `Marshal.PtrToStructure` 直接段错误
  （测试宿主产生 6.6GB core dump；同款 13B 帧换到 op=03 就正常 ⇒ 是 op 级记录解析差异）。

⇒ 现在的策略：`QryTrade/QryAccount/QryPosition` 转发空记录；`QryOrder/QryInstrument` 继续吞掉，
用 30s 超时兜底（宁可慢，不能崩）。**根治办法仍是拿到真前置「同会话请求+非空应答」配对帧**，
把 op=01 的应答形状做对——那时再打开转发。

## 5. 专业/冷门（不建议做）

正常客户端（含快期/文华类）日常不会发的约 60+ 个请求，按族列：

| 族 | 请求 |
|---|---|
| 报价·做市 | `ReqQuoteInsert`/`ReqQuoteAction`/`ReqQryQuote`/`ReqForQuoteInsert`/`ReqQryForQuote` |
| 预埋单 | `ReqParkedOrderInsert`/`ReqQryParkedOrder`/`ReqRemoveParkedOrder`/`ReqParkedOrderAction`/相关查询 |
| 组合腿 | `ReqCombActionInsert`/`ReqQryCombAction`/`ReqQryCombInstrumentGuard`/`ReqQryCombPromotionParam` |
| 执行单 | `ReqExecOrderInsert`/`ReqExecOrderAction`/`ReqQryExecOrder` |
| 期权自对冲 | `ReqOptionSelfCloseInsert`/`ReqOptionSelfCloseAction`/`ReqQryOptionSelfClose` |
| 批量撤单 | `ReqBatchOrderAction` |
| 保证金参数（交易所级） | `ReqQrySPBM*`(9)、`ReqQryRCAMS*`(7)、`ReqQryRULE*`(3)、`ReqQryInvestorProd*Margin`(4)、`ReqQryMM*Rate`(2)、`ReqQryOptionInstrCommRate`/`TradeCost`、`ReqQryInstrumentOrderCommRate` |
| 境外/次级代理 | `ReqQrySecAgent*`(4)、`ReqQryCFMMCTradingAccountKey`/`Token`、`ReqQryInvestUnit` |
| 杂项查询 | `ReqQryDepthMarketData`、`ReqQryTraderOffer`、`ReqQryBrokerTradingAlgos`/`Params`、`ReqQryExchangeRate`/`ProductExchRate`、`ReqQryEWarrantOffset`、`ReqQryRiskSettle*`、`ReqQryProductGroup`、`ReqQryInvestorProductGroupMargin`、`ReqQryInvestorPositionCombineDetail` |
| 短信/验证码登录 | `ReqUserAuthMethod`、`ReqGenUserCaptcha`、`ReqGenUserText`、`ReqUserLoginWithCaptcha`/`OTP`/`Text` |

## 6. 服务端主动推送（S→C）

| 已实现 | 未实现 |
|---|---|
| `RtnOrder`、`RtnTrade`、`RtnDepthMarketData`、`ErrRtnOrderInsert`（风控拒单）、`ErrRtnOrderAction`（撤单失败）、`RspError` | `RtnInstrumentStatus`（合约状态变更——建议补）、`RtnTradingNotice`、`RtnPrivateSeqNo`、询价/报价/执行单/组合腿回报 |
