# Ctp.Net.Front

CTP 线协议前置服务器（券商侧接入层）——官方 CTP SDK 会话复刻为自有线协议：对客户端暴露 tcp :17801（交易）/ :17802（行情），背后连仿真交易所 127.0.0.1:17800。真实前置（SimNow 等）与本地前置对客户端完全同形——本仓即协议适配层。

## 组成

| 文件 | 说明 |
|---|---|
| `CtpFrontServer.cs` / `CtpFrontMdServer.cs` | 交易/行情前置服务器主体（监听、会话管理、交易所转发） |
| `CtpFrontSession.cs` / `CtpFrontMdSession.cs` | 单客户端会话（SDK 流转译为线协议帧） |
| `CtpFrame.cs` / `CtpFieldCodec.cs` / `CtpOrderWireCodec.cs` | 线协议帧结构与字段编解码 |
| `CtpDomainMapper.cs` | SDK 类型 ↔ 契约 DTO 的转换入口 |
| `CtpFrontTemplates.cs` | SDK 模板结构（由 ProtoCapture 生成器从 CTP 头文件生成） |
| `RemoteCtpSession.cs` | 交易所侧会话端口适配（实现 Ctp.Net.ICtpSession 的远端半边） |

## 拓扑

```
客户端（ctp-net-client SDK 会话）
   │  自有线协议 :17801/:17802
   ▼
Ctp.Net.Front（本仓）  ── RPC ──▶ Aether.Exchange（:17800 撮合）
   ▲
   │ 也可直连真实前置（SimNow 同形）
```

前置从不直连撮合、客户端从不直连交易所——真实世界的"一个交易所 + N 券商前置"拓扑镜像。

## 来历

2026-10-19 拆自 Aether monorepo `business/CtpFront/Acme.Ctp.Front/`（单项目、零跨域依赖）。完整决策记录见工作区根 `MIGRATION.md`。
