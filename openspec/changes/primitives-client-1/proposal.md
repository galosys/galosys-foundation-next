## Why

P1 只有服务器端传输，`ChannelClientBase` 是一个继承 `IChannelServer` 的 internal 空壳，没有任何连接、状态或关闭语义。P2 的 session 与 P4 的重连、连接池、batch 都需要一个稳定的客户端连接视图：谁创建 socket、谁拥有 `ChannelContext`、断开后旧连接是否还能用，必须先定义清楚。

本 change 建立单连接客户端：`IChannelClient` 契约、`ChannelClient` 实现、`ChannelClientOptions` 与连接状态事件。重连、连接池和批量写入分别在后续 change 叠加，本 change 不实现。

## What Changes

- 新增 public `IChannelClient`（`Microsoft.Extensions.Hosting.Net`）：`State`、`Connection`、`StateChanged`、`ConnectAsync`、`CloseAsync`、`DisposeAsync`。
- 新增 public `ChannelClient`：创建 `Socket`、按 `ConnectTimeout` 连接、复用 internal `SocketChannelConnection`、创建带空服务的 `ChannelContext`，暴露字节传输。
- 新增 public `ChannelClientOptions`（`Microsoft.Extensions.Options`）：`EndPoint`、`ConnectTimeout`（默认 10 秒）、`KeepAlive`、`Tls`、`TimeProvider`，并提供校验与克隆。
- 新增 public `ChannelConnectionState` 与 `ChannelStateChangedEventArgs`；状态为 `Disconnected`、`Connecting`、`Connected`、`Reconnecting`（本 change 预留不使用）、`Closed`。
- `ConnectAsync` 幂等：并发调用共享同一次连接尝试；`CloseAsync` 是终态，之后不能再连接；`DisposeAsync` 幂等并到达 `Closed`。
- 客户端不提供 `SendAsync` / `ReceiveAsync`；消息收发通过 `Connection.AsSession<T>()`（P2 可用后）完成，本 change 只暴露字节 `Reader` / `Writer`。
- `ChannelClientOptions.Tls` 存在时，在暴露 `Connection` 之前用 `TlsDuplexPipe` 完成客户端握手；未配置时使用明文传输。
- 删除 internal `ChannelClientBase` 占位（它继承 `IChannelServer`，是范畴错误且无引用）。
- 新增 `ChannelClientTests.cs`：loopback 回显、状态迁移、幂等连接、关闭终态、连接失败分类、释放后行为、旧连接失效、TLS 连接。
- 明确不实现重连、连接池、batch；不做多 EndPoint 故障转移。

## Capabilities

### New Capabilities

- `channel-client`: 客户端契约与状态、单连接建立、关闭/释放终态、客户端配置与 TLS 选项。

### Modified Capabilities

无。

## Impact

- 代码：新增 `Microsoft/Extensions/Hosting/Net/Client/{IChannelClient,ChannelClient,ChannelConnectionState,ChannelStateChangedEventArgs,ChannelConnectionFailure}.cs`、`Microsoft/Extensions/Options/ChannelClientOptions.cs`；删除 `Microsoft/Extensions/Hosting/Net/ChannelClientBase.cs`；给 internal `IChannelConnection` / `SocketChannelConnection` 增加接收方向完成信号；修改 `src/Galosys.Foundation.Primitives/README.md`。
- 公共 API：新增 `IChannelClient`、`ChannelClient`、`ChannelClientOptions`、`ChannelConnectionState`、`ChannelStateChangedEventArgs`；内部占位 `ChannelClientBase` 删除，仓库内无消费方。
- 依赖：依赖 `primitives-tls-transport-1`（客户端 TLS）与 `primitives-server-composition-2`（测试使用 sealed `ChannelServer`）；P2 session 可用后消息层通过 `Connection.AsSession<T>()` 接入，本 change 不引用 session 类型。
- 测试：`tests/Galosys.Foundation.Primitives.Tests/ChannelClientTests.cs`；现有测试不改。
- 验证：`dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`、`dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`、`openspec validate "primitives-client-1" --strict --no-interactive`。
