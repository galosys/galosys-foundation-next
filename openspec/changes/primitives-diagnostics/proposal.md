## Why

P1/P2 的服务端和消息层只有 `ILogger` 日志，没有 trace 和 metrics：连接建立/关闭、accept 失败、消息读写量、framing 解析耗时都无法从 OpenTelemetry 或 `dotnet-counters` 观察，也无法把一次慢请求关联到具体连接和协议。P5 需要在不改变协议行为的前提下补上可观测性，并且在没有监听器时不能给热路径增加可测量开销。

本 change 用稳定的 `ActivitySource` / `Meter` 名称 `Galosys.Foundation.Primitives` 为服务端连接生命周期、消息会话与 framing 增加活动、计数器、直方图和低基数标签；客户端重连指标因仓库当前没有 P4 客户端类型而明确不实现，避免留下未接线的死代码。

## What Changes

- 新增 public `ChannelDiagnostics` 常量类：`SourceName` / `MeterName` 为 `Galosys.Foundation.Primitives`，并公开 activity 名、instrument 名和 tag 名。
- 新增 public `ChannelDiagnosticsOptions`：默认不采集 `connection.id`、`remote.endpoint`、`message.size` 标签；`EnableSessionActivities` 默认开启，高吞吐场景可显式关闭每条消息的 activity，计数器和直方图仍保留。
- `ChannelServerOptions` 增加 `Diagnostics`，并在 `Clone()` 中复制；`MessageChannelOptions` 增加 `Diagnostics` 与 `ProtocolName`，供 session 标注 `protocol` 标签。
- 新增 internal `ActivitySource` / `Meter` 与 instrument：服务端连接活动、连接接受/拒绝/活动连接数/连接时长/accept 错误；session 读写计数器、消息大小直方图、framing 解析时长、session 错误。
- 在 `ChannelServerCore` 的 accept/连接生命周期、`ChannelSession<T>` 的读写与 `IMessageFramer.Parse` 周围埋点；不改变读取/写入、异常、取消和停机语义。
- 所有活动使用 `ActivitySource.HasListeners()` 守卫；所有 instrument 使用 `Instrument.Enabled` 守卫，未附加监听器时不构造 tag 集合。
- 新增 `ActivityListener` / `MeterListener` 测试：连接活动、错误活动、session 计数器/直方图、无监听器时的零 tag 分配；README 增加监听器配置与标签说明。
- **明确不在本 change 内**：不新增 `ChannelClient` / `IChannelClient`，不实现客户端重连计数器；等 P4 客户端落地后由独立 change 接入同一组 `ChannelDiagnostics` 常量。
- 不改变 `IChannelServer`、`ChannelContext`、`IChannelSession<T>`、framer/codec 的公共契约；不引入 OpenTelemetry SDK 依赖。

## Capabilities

### New Capabilities

- `channel-diagnostics`: 为服务端连接生命周期、消息会话与 framing 提供稳定的 ActivitySource/Meter、低基数标签、可选高基数标签和无监听器零开销保证。

### Modified Capabilities

无。现有 `channel-server-transport`、`channel-pipeline` 与消息层要求保持有效；诊断只观察，不改变行为。

## Impact

- 代码：新增 `Microsoft/Extensions/Hosting/Net/Diagnostics/ChannelDiagnostics.cs`、`ChannelDiagnosticsOptions.cs`、`Diagnostics/DiagnosticsGuard.cs`（可选辅助）；修改 `ChannelServerOptions.cs`、`ChannelServerCore.cs`、`MessageChannelOptions.cs`、`ChannelSession.cs`、`ChannelContextSessionExtensions.cs`（若工厂需要传递协议名）。
- 公共 API：新增 `ChannelDiagnostics` 常量与 `ChannelDiagnosticsOptions`；`ChannelServerOptions.Diagnostics`、`MessageChannelOptions.Diagnostics`/`ProtocolName` 为增量属性；无破坏性变更。
- 测试：`tests/Galosys.Foundation.Primitives.Tests/` 增加 `DiagnosticsTestSupport.cs`、`ServerDiagnosticsTests.cs`、`SessionDiagnosticsTests.cs`、`DiagnosticsOverheadTests.cs`。
- 依赖：仅使用 .NET 10 内置的 `System.Diagnostics.DiagnosticSource`、`System.Diagnostics.Metrics`；不新增 NuGet 包，不引入 OpenTelemetry SDK。
- 前置：需要 P1（服务端内核）、P1.5a（`ChannelServerCore`/`ChannelServer`）与 P2（`ChannelSession<T>`、framer/codec）已落地；P4 客户端不在依赖内。
- 验证：`dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`、`dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~Diagnostics`、`openspec validate "primitives-diagnostics" --strict --no-interactive`。
