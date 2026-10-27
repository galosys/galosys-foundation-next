## ADDED Requirements

### Requirement: 稳定的诊断名称与低基数标签

The implementation SHALL expose public constants for the `ActivitySource` name, `Meter` name, activity names, instrument names and tag names. The `ActivitySource` and `Meter` names MUST both be `Galosys.Foundation.Primitives`. Instrument and activity names MUST remain stable across releases; renaming an existing name is a breaking change. By default, activities and metrics MUST NOT include high-cardinality tags such as `connection.id`, `remote.endpoint` or `message.size`. `connection.id`, `remote.endpoint` and `message.size` MAY be enabled on activities through `ChannelDiagnosticsOptions`; metrics MUST NOT use these tags.

#### Scenario: 稳定名称可被监听

- **WHEN** 使用 `ActivityListener` 的 `ShouldListenTo` 判断 `source.Name == ChannelDiagnostics.SourceName`
- **THEN** 可以接收到 Primitives 产生的 activity
- **AND** 使用 `MeterListener` 通过 `instrument.Meter.Name == ChannelDiagnostics.MeterName` 可以启用全部 instrument

#### Scenario: 默认不采集高基数标签

- **WHEN** 使用默认 `ChannelDiagnosticsOptions` 建立连接并读写消息
- **THEN** `ChannelServer.Connection` activity 包含 `transport`、`protocol`、`outcome`
- **AND** 不包含 `connection.id`、`remote.endpoint`、`message.size` 标签
- **AND** metric 不包含 `connection.id`、`remote.endpoint`、`message.size` 标签

#### Scenario: 显式开启连接 ID

- **WHEN** 设置 `ChannelServerOptions.Diagnostics.CaptureConnectionId = true` 并处理一个连接
- **THEN** `ChannelServer.Connection` activity 包含 `connection.id` 标签
- **AND** metric 仍然不包含 `connection.id`

### Requirement: 服务端连接生命周期活动与指标

`ChannelServerCore` MUST emit a `ChannelServer.Connection` activity spanning the per-connection handler lifecycle, with `ActivityKind.Server`, `transport` and `protocol=stream` tags. The activity MUST set `outcome=ok` on normal completion, `outcome=error` and `error.type` on handler exceptions, and `outcome=cancelled` when the handler is cancelled by connection shutdown. The server MUST expose counters for accepted connections, rejected connections, accept errors, an UpDownCounter for active connections, and a histogram for connection duration. Rejected connections MUST use the fixed reason values `max_connections` or `initialization_error`.

#### Scenario: 正常连接产生活动与计数

- **WHEN** 客户端连接到一个默认 TCP 服务器并完成回显
- **THEN** 产生一个 `ChannelServer.Connection` activity，`outcome=ok`、`protocol=stream`、`transport=tcp`
- **AND** `channel.server.connections.accepted` 至少增加 1
- **AND** `channel.server.connections.active` 在连接结束后回到 0
- **AND** `channel.server.connection.duration` 至少记录一次测量

#### Scenario: 处理器异常标记错误

- **WHEN** 连接处理器抛出 `InvalidOperationException`
- **THEN** `ChannelServer.Connection` activity 的 `outcome=error`
- **AND** `error.type` 为 `System.InvalidOperationException`
- **AND** `channel.server.connection.duration` 仍记录一次测量
- **AND** 服务器继续接受其他连接

#### Scenario: 超过最大连接数

- **WHEN** `MaxConnections` 已满，服务器拒绝新连接
- **THEN** `channel.server.connections.rejected` 增加 1，`reason=max_connections`
- **AND** 不产生该被拒绝连接的 `ChannelServer.Connection` activity

#### Scenario: accept 失败

- **WHEN** accept loop 遇到非停机取消的异常
- **THEN** `channel.server.accept.errors` 增加 1
- **AND** 现有连续失败阈值与日志行为保持不变

### Requirement: 消息会话与 framing 指标

`ChannelSession<T>` MUST emit session read/write activities when `EnableSessionActivities` is true, and MUST always emit message counters and message-size histograms when the corresponding instruments are enabled. Read and write activities MUST use the configured `ProtocolName` and `direction=read` or `direction=write`. Framing parse duration MUST be recorded around `IMessageFramer.Parse` with the same protocol tag. Message size MUST be recorded as a histogram value, not as a metric tag. Cancellation MUST be recorded as `outcome=cancelled` and MUST NOT be converted into a successful outcome.

#### Scenario: 读取一条消息

- **WHEN** session 通过 `LengthPrefixedFramer` 与 JSON codec 读取一条完整消息
- **THEN** 产生 `ChannelSession.Read` activity，`protocol=json`、`direction=read`、`outcome=ok`
- **AND** `channel.session.messages` 增加 1
- **AND** `channel.session.message.size` 记录该帧 payload 的长度
- **AND** `channel.session.framing.duration` 至少记录一次测量

#### Scenario: 写入一条消息

- **WHEN** session 写入一条 JSON 消息
- **THEN** 产生 `ChannelSession.Write` activity，`protocol=json`、`direction=write`、`outcome=ok`
- **AND** `channel.session.messages` 的 `direction=write` 计数增加
- **AND** `channel.session.message.size` 记录编码后 payload 的长度

#### Scenario: 读取取消

- **WHEN** `ReadAsync` 因取消令牌取消
- **THEN** `ChannelSession.Read` activity 的 `outcome=cancelled`
- **AND** `channel.session.errors` 记录一次取消
- **AND** 下一次读取仍可继续，协议行为不变

#### Scenario: framing 解析失败

- **WHEN** framer 因非法长度抛出 `InvalidDataException`
- **THEN** `ChannelSession.Read` activity 的 `outcome=error`，`error.type=System.IO.InvalidDataException`
- **AND** `channel.session.errors` 增加 1
- **AND** 异常原样抛给调用方，不被诊断层吞掉

### Requirement: 无监听器时的开销与协议中立

All diagnostic entry points MUST check `ActivitySource.HasListeners()` before creating an activity or tags, and MUST check `Instrument.Enabled` before recording measurements. When no listener is attached, the implementation MUST NOT allocate `ActivityTagsCollection`, tag arrays or `KeyValuePair` arrays for diagnostic purposes. Instrumentation MUST NOT change message bytes, ordering, framing, codec results, exceptions, cancellation or shutdown behavior. The implementation MUST NOT require an OpenTelemetry SDK or any new NuGet package.

#### Scenario: 未附加监听器

- **WHEN** 没有 `ActivityListener` 或 `MeterListener` 监听 Primitives
- **THEN** 连接和消息处理继续正常工作
- **AND** 诊断入口返回 null 或直接返回，不构造 tag 集合
- **AND** 连续调用诊断入口不产生可测量的 tag 分配

#### Scenario: 只监听 metrics

- **WHEN** 只附加 `MeterListener`，不附加 `ActivityListener`
- **THEN** 计数器与直方图正常记录
- **AND** 不创建 activity，也不因 activity 路径分配 tag

#### Scenario: 协议行为不变

- **WHEN** 在启用全部诊断的情况下运行既有回显、framing、codec 和 session 测试
- **THEN** 消息内容、顺序、异常类型和取消行为与未启用诊断时一致
- **AND** 现有测试无需修改断言

### Requirement: 客户端指标显式后置

This change MUST NOT add `ChannelClient`, `IChannelClient` or any placeholder client instrumentation. If the repository does not yet contain a P4 client type, client reconnect and connection-failure metrics MUST be omitted rather than implemented as dead code. When a P4 client is added later, client metrics MUST use the same `Galosys.Foundation.Primitives` meter name and a separate change.

#### Scenario: 仓库没有客户端类型

- **WHEN** 检查本 change 的程序集和公共 API
- **THEN** 不存在新增的 `ChannelClient` 或 `IChannelClient`
- **AND** 不存在未接线的 `channel.client.reconnects` 或 `channel.client.connect.failures` instrument

#### Scenario: 后续接入客户端指标

- **WHEN** P4 客户端在后续 change 中落地
- **THEN** 客户端重连计数使用同一个 `ChannelDiagnostics.MeterName`
- **AND** 本 change 的 README 已记录建议的指标名称与低基数标签
