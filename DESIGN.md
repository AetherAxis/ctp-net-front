# Ctp.Net.Front 设计说明

> 本文件写「怎么设计 / 为什么这么设计 / 还能往哪升级」。工作区边界与迁移决策以根 `MIGRATION.md` 为准。

## 1. 定位与边界

CTP 线协议前置：客户端连 tcp :17801/:17802，后端经 `SimRpcClient` 连 Aether.Exchange(:17800)。不撮合、不记账、不持久化、不直连交易所，镜像「交易所 + N 券商前置」拓扑。

边界：撮合/账户归 aether-exchange，`ICtpSession`/DTO 归 contracts，SDK 归 core，抓包生成归 ⑨ capture；本仓只管分帧、会话、模板、映射与 `RemoteCtpSession`。

## 2. 项目组成

| 项目/文件 | 职责 | 关键类型 | 出向依赖 |
|---|---|---|---|
| `src/` + `tests/` | src 库 + E2E/wire/契约 | `FrontLoopback` 等 | Exchange.Rpc、Contracts、Core |
| `CtpFrontServer` / `CtpFrontMdServer` | 交易/行情监听，每连接会话+RPC | 同名类 | Exchange.Rpc |
| `CtpFrontSession` / `CtpFrontMdSession` | 状态机、模板回放、推送、单写者、序号/重建 | 同名类 | Exchange.Rpc、Ctp.Net |
| `CtpFrame` / `CtpFieldCodec` / `CtpOrderWireCodec` | 分帧（标准/握手/心跳/垃圾）、tag 编解码、⑲/㉑ 解析 | `CtpFrame`、`CtpFrameParser`、`CtpFieldCodec`、`ParsedOrderInsert/Action` | BCL |
| `CtpDomainMapper` | Domain ↔ `Ctp*` 唯一入口，枚举对齐 | `CtpDomainMapper` | Exchange、Ctp.Net |
| `CtpFrontTemplates` | 抓包模板 + 偏移替换，价格/量/GBK 编码 | `CtpFrontTemplates`、`Edit` | BCL |
| `RemoteCtpSession` | `ICtpSession` 远端适配、回调泵、tick 缓存 | `RemoteCtpSession` | Ctp.Net、Exchange.Rpc |

## 3. 关键设计决策

### 3.1 同形，SDK 即裁判
- **决策**：抓包帧逐字节复刻，只改标记槽；固定挑战使密文即哈希。
- **理由**：官方 SDK 解析，形状错即不回调。
- **代价**：模板与抓包耦合；未逆向帧只能空表。

### 3.2 映射单入口
- **决策**：Exchange 不引用 CTP DTO，`CtpDomainMapper` 唯一转换，枚举 byte 对齐。
- **理由**：核心可脱离 CTP，契约只有一个审查面。
- **代价**：新字段需同步改映射，码值对齐靠测试。

### 3.3 4B 帧头 + tag payload
- **决策**：`[02 00][2B 大端长度][payload]`，前 4B 路由、其后 tag 字段；握手 `00 06` 特例；心跳循环内剥离并回射。
- **理由**：抓包真实格式；不回射心跳长会话停摆，不剥离会吃掉下一帧。
- **代价**：握手启发式、未知头逐字节跳、心跳跨段未覆盖。

### 3.4 模板由 ⑨ capture gen 生成 + 等宽纪律
- **决策**：ProtoCapture `gen` 生成模板；价格/量/orderRef/消息槽等宽、量化、避 tag 字节。
- **理由**：SDK 按记录宽度遍历，槽位变宽会失步（2026-10-13 断连根因）。
- **代价**：身份字段截断/补空格；价格量化；原抓包清空。

### 3.5 进程分离 + 应答先于推送
- **决策**：每会话一条 `SimRpcClient`；单写者 + 捕获窗保证应答先于本单推送，成功报单让路 10ms。
- **理由**：对齐真实拓扑；省账户键；`CtpOrderTracker` 依赖应答后注册。
- **代价**：多一跳；交易腿断连即关，行情腿重建。

## 4. 运行与验证

```bash
dotnet build Ctp.Net.Front.slnx -c Release
dotnet test  Ctp.Net.Front.slnx   # 需 macOS frameworks（CopyCtpMacFrameworks）
```

- **73 例**：`[Fact]` 45、`[Theory]` 5（28 条 `InlineData`）；属性计数 50、展开 73，与 `MIGRATION.md` 一致。
- E2E 44 覆盖登录/结算/资金/持仓、报单-成交-撤单、F0 240、F003、IOC/PostOnly、双客户端、MD 订阅/tick、MM 阶梯、百轮不停摆（`CtpFrontE2e*` 等）。
- wire 29：`OrderCodecShapeTests` 7（真实报单/撤单帧，tag 漂移/E0/FE）、`RtnOrderWidthShapeTests` 22（帧宽/6B 消息恒定）。
- `SessionAdapterTests` 24：未登录同步抛、拒单 await 抛、应答先于推送、成交 Order→Trade、查询空账本。
- 夹具：`FrontLoopback`、`SessionHarness`/`LoopbackSim`；关闭并行。
- CI 只跑 29 例 wire，E2E 留本地（`.so` 缺口，§9.2）。

## 5. 升级记录（2026-10-24）

**真前置 golden 帧回归**（同日第二轮）：拿到 SimNow 官服凭据后，用 ⑨ 的 `proxy` 模式把应答帧重新抓了一遍并落成断言——新增 `tests/Ctp.Net.Front.Tests/RealFrontGoldenFrameTests.cs`（4 例，期望值来自真前置实测字节）：

- 持仓空表 `0103344CE104E28005E302E706`、查报单空表 `0103344CE104E28001E303E707`：与本地模板**逐字节相同**（查报单一项此前只知「SDK 会超时」，实测真前置确实回这个 13B tag 帧，超时是 SDK 对空结果不派发回调的既有行为）。
- 资金应答 134B payload（6 槽 `412E83E0EF6147AE14`）、结算确认 65B payload：**线上宽度与真前置一致**——宽度漂移是本仓最贵的 bug 类型（帧缩/撑 ⇒ SDK 记录遍历失步 ⇒ 断连风暴），现在改模板会被这 4 例拦下。
- 顺带定案两件**不需要动代码**的事：① 多行走二进制（真前置查合约 17335 行仅末行 `isLast=True`，4096B 分块），tag 协议无 `isLast=false` 编码，**不要为对齐帧级标志去改模板**；② 6 字符账户在 `E1` 槽被真前置自己截成 5 字符，`F()` 的截断是保真而非缺陷。

| 项 | 变更前 | 变更后 | 依据 / 验证 |
|---|---|---|---|
| `Microsoft.NET.Test.Sdk` | 17.14.1 | 18.11.0 | `git status` 仅该文件；`git diff` 见版本行 |
| `xunit.runner.visualstudio` | 3.1.4 | 4.0.1 | 同上；`xunit` 保持 2.9.3 |
| `coverlet.collector` | 6.0.4 | 10.1.0 | 同上；测试 csproj 未改，仍由 CPM 统一 |

验证：Release 构建 **0 错误 0 警告**；**非 E2E 的 53 例全绿**（`--filter` OrderCodecShape + RtnOrderWidthShape + SessionAdapter，即 CI 的 29 例 wire 钉子加 24 例 SessionAdapter 契约）。44 例 E2E 未跑：它们要官方 C++ SDK 的 native 产物，本机与 CI 同样缺（`ci.yml` 注释与 MIGRATION §9.2 已记），挂起原因与本次升级无关。

## 6. 待升级项

| # | 优先级 | 项目 | 现状证据 | 建议 | 代价 / 风险 |
|---|---|---|---|---|---|
| 1 | P0 | E2E 无法进 CI | `ci.yml` 只过滤两 wire 类；§9.2 linux-x64 `.so` 缺口 | Core 交付原生依赖、放开全量；过渡期 macOS runner | 打包/rpath 成本高；44 例只在本地跑 |
| 2 | P1 | 模板不可复现且含历史会话数据 | 头注：含 token/密文/账号，原抓包已清空。**2026-10-24 已用真前置重抓校验 tag 帧**（`RealFrontGoldenFrameTests` 4 例全绿），但模板文件本身仍带旧会话字节 | 用新抓包重生成脱敏模板 | 重生成可能改帧宽；多行/tick 未逆向前不要盲重生成 |
| 3 | P2 | 等宽归一化截断真实身份 | `F(s,width)` 截断/补空格；user 6B、instrument 5/6B。**2026-10-24 实证真前置自己也截**（6 字符账户在 `E1` 槽 → 5 字符），故 6 字符内是保真；只剩 7–8 位用户未验 | 先测 7–8 位用户真帧，再决定是否改生成器 | 偏移与 payload 联动，改动面大 |
| 4 | P2 | 分帧器/字段编解码器无独立单测 | 测试无 `CtpFrameParser`/`CtpFieldCodec` 直接引用；心跳跨段未覆盖 | 加纯内存单测：多帧、半帧、握手、垃圾、心跳 | 成本低；可把限制显式化 |
| 5 | P3 | 错误码 >255 降级为 148 | `WireError` 超 1 字节核心码映射 148，真码只进 GBK 前缀 | 建映射表并补测试；评估结构化槽 | 改协议槽需抓包；消费方丢精度 |
