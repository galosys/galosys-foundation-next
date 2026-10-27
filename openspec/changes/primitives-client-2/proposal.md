## Why

`primitives-client-1` 只做单次连接：连接失败立即回到 `Disconnected`，已建立的连接被远端关闭后也不会恢复。真实网络会断线、服务端会滚动重启，调用方不应为每次断线重新构造客户端、重新注册事件。设计文档 §5.7 已经预留 `Reconnecting` 状态和 `ChannelReconnectPolicy`，本 change 把这条路径落地。

本 change 只做重连与退避，不改变连接级语义：重连产生新的 `ChannelContext` 和新 `ConnectionId`，断开期间不缓冲、不重放消息。连接池与 batch 留在 `primitives-client-3`。

## What Changes

- 新增 public `ChannelReconnectPolicy`（`Microsoft.Extensions.Options`）：`MaxAttempts`（0 表示无限）、`InitialDelay`、`MaxDelay`、`BackoffMultiplier`、`JitterFactor`、`ShouldRetry`。
- `ChannelClientOptions` 新增 `ChannelReconnectPolicy? Reconnect`；校验与克隆覆盖该属性。
- `ChannelClient` 在可重试失败时进入 `Reconnecting`，按指数退避 + 抖动重试；成功后回到 `Connected` 并暴露新连接，非可重试失败或尝试耗尽后回到 `Disconnected`。
- 重连延迟只使用 `Task.Delay(TimeSpan, TimeProvider, CancellationToken)`，禁止裸 `Task.Delay`；测试用 `FakeTimeProvider` 推进假时钟。
- `CloseAsync` / `DisposeAsync` 取消退避与重连，进入 `Closed` 后不再重连、不再触发状态事件。
- 新增 `ChannelReconnectTests.cs`：首次连接重试、断线自动重连、TLS 证书失败不重试、`ShouldRetry` 覆盖、尝试耗尽、关闭打断退避、调用方取消。
- 明确不实现连接池、batch、多 EndPoint 故障转移和消息重放。

## Capabilities

### New Capabilities

无。

### Modified Capabilities

- `channel-client`: 扩展客户端状态机与配置，新增 `ChannelReconnectPolicy`、自动重连、退避、重试分类与关闭取消要求。

## Impact

- 代码：新增 `Microsoft/Extensions/Options/ChannelReconnectPolicy.cs`；修改 `ChannelClientOptions.cs`、`ChannelClient.cs`；修改 `Galosys.Foundation.Primitives.Tests.csproj` 增加 `Microsoft.Extensions.TimeProvider.Testing`；新增 `ChannelReconnectTests.cs`。
- 公共 API：新增 `ChannelReconnectPolicy` 与 `ChannelClientOptions.Reconnect`；`IChannelClient` 签名不变。
- 依赖：依赖 `primitives-client-1`；测试使用 `FakeTimeProvider`。不新增生产依赖。
- 测试：`tests/Galosys.Foundation.Primitives.Tests/ChannelReconnectTests.cs`；现有 `ChannelClientTests` 保持通过。
- 验证：`dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`、`dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`、`openspec validate "primitives-client-2" --strict --no-interactive`。
