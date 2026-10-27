## Context

P1/P2 已有 `ILogger` 日志、连接 ID 和完整的生命周期，但没有 ActivitySource/Meter：

- 无法从 OpenTelemetry 看到一次连接从 accept 到关闭的跨度；
- 无法统计 accept 失败、连接拒绝、活动连接数、连接时长；
- 无法统计 session 读写消息数、消息大小分布和 framing 解析耗时；
- 无法把慢消息关联到连接、协议和方向。

P5 的 diagnostics 必须满足两个约束：一是不能改变协议行为（读取/写入、异常、取消、停机语义保持原样）；二是没有监听器时不能给热路径增加可测量开销。仓库当前也没有 P4 的 `ChannelClient` / `IChannelClient`，因此客户端重连指标不能凭空实现。

## Goals / Non-Goals

**Goals:**

- 用稳定名称 `Galosys.Foundation.Primitives` 提供 `ActivitySource` 与 `Meter`。
- 为服务端连接生命周期、session 读写、framing 解析提供活动、计数器、直方图和 UpDownCounter。
- 定义低基数标签集合；`connection.id`、`remote.endpoint`、`message.size` 作为可选标签，默认关闭。
- 用 `ActivitySource.HasListeners()` 与 `Instrument.Enabled` 守卫，未附加监听器时不创建 tag 集合、不记录测量。
- 用 `ActivityListener` / `MeterListener` 测试活动、错误、计数器、直方图和禁用路径。
- 在 README 中给出监听器配置、指标/标签表和“默认不采集高基数标签”的说明。

**Non-Goals:**

- 不新增 `ChannelClient` / `IChannelClient`，不实现客户端重连计数器；P4 落地后由独立 change 接入。
- 不引入 OpenTelemetry SDK、Exporter 或 `Microsoft.Extensions.Diagnostics` 包；只使用 .NET 内置 `System.Diagnostics.DiagnosticSource` / `System.Diagnostics.Metrics`。
- 不改变 `IChannelServer`、`ChannelContext`、`IChannelSession<T>`、`IMessageFramer`、`IMessageCodec<T>` 的公共契约。
- 不把诊断作为 pipeline 中间件；服务端连接活动由 core 产生，消息活动由 session 产生，避免中间件抢读字节流。
- 不做采样策略、动态开关或运行时重配置；采样由监听器/OpenTelemetry 侧决定。
- 不改变日志文本、异常类型、超时值或任何协议行为。

## Decisions

### 1. 稳定名称与公共常量

`ChannelDiagnostics` 是 public static 常量类，放在 `Microsoft.Extensions.Hosting.Net`：

```csharp
public static class ChannelDiagnostics
{
    public const string SourceName = "Galosys.Foundation.Primitives";
    public const string MeterName = "Galosys.Foundation.Primitives";

    public const string ServerConnectionActivity = "ChannelServer.Connection";
    public const string SessionReadActivity = "ChannelSession.Read";
    public const string SessionWriteActivity = "ChannelSession.Write";

    public const string ConnectionsAcceptedMetric = "channel.server.connections.accepted";
    public const string ConnectionsRejectedMetric = "channel.server.connections.rejected";
    public const string ConnectionsActiveMetric = "channel.server.connections.active";
    public const string ConnectionDurationMetric = "channel.server.connection.duration";
    public const string AcceptErrorsMetric = "channel.server.accept.errors";
    public const string SessionMessagesMetric = "channel.session.messages";
    public const string SessionMessageSizeMetric = "channel.session.message.size";
    public const string SessionFramingDurationMetric = "channel.session.framing.duration";
    public const string SessionErrorsMetric = "channel.session.errors";

    public const string ConnectionIdTag = "connection.id";
    public const string RemoteEndPointTag = "remote.endpoint";
    public const string TransportTag = "transport";
    public const string ProtocolTag = "protocol";
    public const string DirectionTag = "direction";
    public const string OutcomeTag = "outcome";
    public const string ReasonTag = "reason";
    public const string MessageSizeTag = "message.size";
    public const string ErrorTypeTag = "error.type";
}
```

`ActivitySource` 与 `Meter` 本身保持 internal static readonly，版本 `"1.0.0"`。使用方只需按名称监听，不依赖库内对象生命周期；这样避免公开可释放的 `ActivitySource`/`Meter` 被使用方误 Dispose。

### 2. `ChannelDiagnosticsOptions` 控制高基数标签

```csharp
namespace Microsoft.Extensions.Options
{
    public sealed class ChannelDiagnosticsOptions
    {
        /// <summary>是否在 activity 上附加 connection.id，默认 false。</summary>
        public bool CaptureConnectionId { get; set; }

        /// <summary>是否附加 remote.endpoint，默认 false。</summary>
        public bool CaptureRemoteEndPoint { get; set; }

        /// <summary>是否在 activity 上附加 message.size 标签，默认 false。</summary>
        public bool CaptureMessageSize { get; set; }

        /// <summary>是否为每条消息创建 session activity，默认 true。</summary>
        public bool EnableSessionActivities { get; set; } = true;

        public ChannelDiagnosticsOptions Clone() => (ChannelDiagnosticsOptions)MemberwiseClone();
    }
}
```

`ChannelServerOptions` 增加 `Diagnostics`，默认 `new()`，`Clone()` 调用 `Diagnostics.Clone()`。`MessageChannelOptions` 增加 `Diagnostics` 与 `ProtocolName`：

```csharp
public string ProtocolName { get; set; } = "custom";
public ChannelDiagnosticsOptions Diagnostics { get; set; } = new();
```

`AsJsonSession<T>()` 把 `ProtocolName` 设为 `"json"`，`AsLineTextSession()` 设为 `"line"`，`AsSession()` 使用调用方传入的 `MessageChannelOptions` 或默认 `"custom"`。`ProtocolName` 是低基数标签；自定义协议应设置固定名称，不要使用消息内容或连接 ID。

**为什么默认关闭高基数标签：** `connection.id`、`remote.endpoint`、`message.size` 的取值范围大，作为 metric tag 会造成基数爆炸。默认只在 activity 上按需开启；`message.size` 的分布用 histogram 值表达，不进入 tag。

### 3. 服务端活动与指标由 `ChannelServerCore` 产生

服务端不增加 per-accept 的 activity（accept 次数可能非常高），而是用计数器记录 accept 结果，用一条 `ChannelServer.Connection` activity 覆盖连接处理生命周期：

| 名称 | 类型 | 触发点 | 标签 |
|---|---|---|---|
| `ChannelServer.Connection` | Activity (Server) | `ProcessConnectionAsync` 开始到结束 | `transport`、`protocol=stream`、可选 `connection.id`、`remote.endpoint`、`outcome`、`error.type` |
| `channel.server.connections.accepted` | Counter&lt;long&gt; | accept 且未被 `MaxConnections` 拒绝 | `transport` |
| `channel.server.connections.rejected` | Counter&lt;long&gt; | `MaxConnections` 拒绝或初始化失败 | `transport`、`reason` |
| `channel.server.connections.active` | UpDownCounter&lt;long&gt; | 连接处理开始 +1，结束 -1 | `transport` |
| `channel.server.connection.duration` | Histogram&lt;double&gt; (ms) | 连接处理结束 | `transport`、`outcome` |
| `channel.server.accept.errors` | Counter&lt;long&gt; | accept 异常且非停机取消 | `transport` |

`transport` 的值是 `tcp` 或 `namedpipe`，由端点类型推导。为避免 diagnostics 与 `primitives-namedpipe` 产生编译顺序依赖，helper 使用：

```csharp
internal static string GetTransportName(EndPoint? endPoint)
    => endPoint switch
    {
        IPEndPoint => "tcp",
        null => "unknown",
        { } value when value.GetType().FullName
            == "Microsoft.Extensions.Hosting.Net.NamedPipeEndPoint" => "namedpipe",
        _ => "other",
    };
```

这不是公共契约；若后续新增 transport，扩展该 helper 即可。NamedPipe change 不需要反向依赖 diagnostics。

`outcome` 取值固定为 `ok`、`error`、`cancelled`。`error.type` 只写异常类型全名，不写异常消息/堆栈。`reason` 取值固定为 `max_connections`、`initialization_error`。所有 metric 不附加 `connection.id`、`remote.endpoint` 或 `message.size`。

### 4. Session 与 framing 指标由 `ChannelSession<T>` 产生

`ChannelSession<T>` 在 `ReadAsync` / `WriteAsync` 周围埋点：

| 名称 | 类型 | 触发点 | 标签 |
|---|---|---|---|
| `ChannelSession.Read` | Activity (Internal) | 每次 `ReadAsync`，受 `EnableSessionActivities` 控制 | `protocol`、`direction=read`、可选 `message.size`、`outcome`、`error.type` |
| `ChannelSession.Write` | Activity (Internal) | 每次 `WriteAsync`，受 `EnableSessionActivities` 控制 | `protocol`、`direction=write`、可选 `message.size`、`outcome`、`error.type` |
| `channel.session.messages` | Counter&lt;long&gt; | 成功读取/写入一条消息 | `protocol`、`direction`、`outcome` |
| `channel.session.message.size` | Histogram&lt;long&gt; (By) | 一帧 payload 长度 | `protocol`、`direction` |
| `channel.session.framing.duration` | Histogram&lt;double&gt; (ms) | `IMessageFramer.Parse` 前后 | `protocol` |
| `channel.session.errors` | Counter&lt;long&gt; | 解码、framing 或读写异常 | `protocol`、`direction`、`outcome` |

`message.size` 只在 histogram 值中出现，默认不做 tag；只有 `CaptureMessageSize = true` 时才写到 activity tag。读取消记录 `outcome=cancelled`；写取消仍按 P2 的“写取消不安全”约定由 session 中止连接，但诊断不改变该行为。

`EnableSessionActivities = true` 时，每条消息会创建 activity；高吞吐场景可在 `MessageChannelOptions.Diagnostics` 中关闭，计数器/直方图仍然保留。

### 5. 未附加监听器时零 tag 分配

所有入口先检查：

```csharp
if (!ActivitySource.HasListeners())
    return null;

if (!SessionMessages.Enabled)
    return;
```

只有 `StartActivity` 返回非 null 后才 `SetTag`，因此未监听时不会构造 `ActivityTagsCollection` 或 `KeyValuePair` 数组。`Stopwatch.GetTimestamp()` 只在 `ShouldMeasureConnection()`（`ActivitySource.HasListeners() || ConnectionDuration.Enabled`）为 true 时调用，避免纯计数场景的计时开销。

`ActivitySource`/`Meter`/instrument 是 static readonly，每个进程只创建一次；不在每连接或每消息创建新对象。

### 6. 客户端指标明确后置

仓库当前没有 `ChannelClient` / `IChannelClient`（P4 尚未落地）。本 change：

- 不新增客户端类型、客户端连接状态机或占位方法；
- 不定义 `channel.client.reconnects` / `channel.client.connect.failures` instrument；
- 在 README 的“后续扩展”中记录这两个名称，供 P4 change 接入同一 `MeterName`；
- 不写“客户端重连”测试，避免测试一个不存在的生产路径。

若应用本 change 时 P4 已经落地，仍按同一原则由独立 change 在 `ChannelClient` 内调用 `ChannelDiagnostics` helper，不在本 change 追加未评审的客户端代码。

## Risks / Trade-offs

- **每消息 activity 的量** → 默认开启可能在高吞吐下产生大量 span；`EnableSessionActivities=false` 可关闭，计数器/直方图仍保留。README 明确说明。
- **tag 基数** → `connection.id`、`remote.endpoint`、`message.size` 默认关闭；`protocol` 由调用方设置固定值，不自动使用类型名或消息内容。
- **`Instrument.Enabled` 的语义** → 只表示是否有监听器启用该 instrument；没有监听器时跳过测量。测试用 `MeterListener` 验证。
- **诊断改变异常路径** → 所有 helper 只 SetTag/Record，不 catch 业务异常；`try/catch` 仅用于记录后原样 `throw`。
- **与 NamedPipe 的编译顺序** → transport 名称通过端点类型全名推导，不引用 `NamedPipeEndPoint` 类型；两个 change 可以独立应用。
- **与 P2 的耦合** → session/framing 指标需要 `ChannelSession<T>` 与 `MessageChannelOptions`；若 P2 尚未落地，本 change 不能完整编译，依赖顺序在任务中明确。
- **测试计时抖动** → 只断言“有测量值/标签正确”，不断言具体耗时阈值；无监听器分配测试使用宽松上限并先做 warmup。
- **无 OpenTelemetry SDK** → 只提供 `ActivitySource`/`Meter`，导出由宿主决定；README 给出 `ActivityListener`/`MeterListener` 示例，不绑定具体 exporter。

## Migration Plan

1. 新增 `ChannelDiagnosticsOptions` 与 `ChannelDiagnostics` 常量/内部 source/meter/instrument。
2. 修改 `ChannelServerOptions` 增加 `Diagnostics` 并在 `Clone()` 中复制；修改 `MessageChannelOptions` 增加 `Diagnostics`/`ProtocolName`。
3. 在 `ChannelServerCore` 的 accept 结果、连接开始/结束、异常路径加入计数器和 activity。
4. 在 `ChannelSession<T>` 的 `ReadAsync`/`WriteAsync`、`IMessageFramer.Parse` 周围加入 activity、counter、histogram。
5. 让 `AsJsonSession`/`AsLineTextSession`/`AsSession` 传递 `ProtocolName`；默认 `"custom"`。
6. 增加 `DiagnosticsTestSupport` 与四组测试；先跑服务端，再跑 session，最后跑 overhead。
7. 更新 Primitives README 的可观测性章节，加入监听器配置、指标/标签表、客户端指标后置说明。
8. 构建 Primitives、跑 `Diagnostics` 过滤测试、跑全量测试与 `openspec validate`。

回滚策略：删除 diagnostics 文件与 options 属性，移除 core/session 中的 helper 调用；协议代码本身没有被修改，回滚不影响连接行为。若只想关闭 session activity，可保留代码并设 `EnableSessionActivities=false`，无需回滚。

## Open Questions

无。客户端重连指标明确留给 P4 后续 change；本 change 不创建占位类型或未接线 instrument。
