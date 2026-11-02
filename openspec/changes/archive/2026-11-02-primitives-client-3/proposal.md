## Why

`primitives-client-1` 提供一个 `ChannelClient` 对应一个连接，调用方若要在并发请求间复用连接，必须自己维护创建、上限、等待、健康检查和释放；`primitives-client-2` 只解决单连接断线重连。设计文档把连接池与 batch 明确列为 P4 的独立能力：连接池限制并发连接数，batch 让高频小消息只 flush 一次。

本 change 在不改变 `IChannelClient` 与 `IChannelSession<T>` 公开契约的前提下，增加有界连接池和批量写入扩展。连接池不做负载均衡、空闲淘汰或消息重放；batch 不改变单条消息的帧格式，也不引入第二个会话抽象。

## What Changes

- 新增 public `IChannelClientFactory` 与默认实现 `ChannelClientFactory`：`Create(ChannelClientOptions)` 统一创建客户端，便于连接池替换与测试注入。
- 新增 public `ChannelClientPoolOptions`（`Microsoft.Extensions.Options`）：`ClientOptions`、`MaxSize`（默认 8）、`RentTimeout`（默认 30 秒）。
- 新增 public sealed `ChannelClientPool`：`RentAsync` / `ReturnAsync` / `DisposeAsync`，有界复用、健康检查、断线替换、等待超时与所有权校验。
- 新增 public `WriteBatchAsync<T>` 扩展（`Microsoft.Extensions.Hosting.Net`）与 internal `IChannelSessionBatchWriter<T>`；P2 的 internal `ChannelSession<T>` 实现批量路径，在同一个写锁内编码并写出多帧，只调用一次 `FlushAsync`。
- 批量写入失败或取消发生在已写出部分帧之后时，中止连接，避免半批数据被后续写入误认为完整消息；空批次不触碰连接。
- 新增 `ChannelClientPoolTests.cs` 与 `ChannelSessionBatchTests.cs`，覆盖复用、上限、超时、替换、归还校验、批量顺序与单次 flush。
- 文档补充连接池与 batch 的边界；明确不做空闲淘汰、负载均衡、多 EndPoint、事务性批量或消息重放。

## Capabilities

### New Capabilities

- `channel-client-pool`: 客户端工厂、连接池配置、有界租借/归还、健康替换与释放语义。

### Modified Capabilities

- `channel-session`: 新增批量写入要求；单条 `WriteAsync` 的帧格式、锁与取消语义保持不变。

## Impact

- 代码：新增 `Microsoft/Extensions/Hosting/Net/Client/{IChannelClientFactory,ChannelClientFactory,ChannelClientPool}.cs`、`Microsoft/Extensions/Options/ChannelClientPoolOptions.cs`、`Session/{IChannelSessionBatchWriter,ChannelSessionBatchExtensions}.cs`；修改 `Session/ChannelSession.cs` 实现批量接口。
- 公共 API：新增 `IChannelClientFactory`、`ChannelClientFactory`、`ChannelClientPoolOptions`、`ChannelClientPool`、`WriteBatchAsync<T>`；`IChannelClient` 与 `IChannelSession<T>` 签名不变。
- 依赖：依赖 `primitives-client-1`、`primitives-client-2`（池内客户端可重连）与 `primitives-message-session`（`ChannelSession<T>`）。不新增生产 NuGet 包。
- 测试：新增 `ChannelClientPoolTests.cs`、`ChannelSessionBatchTests.cs`；现有测试不改。
- 验证：`dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`、`dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`、`openspec validate "primitives-client-3" --strict --no-interactive`。
