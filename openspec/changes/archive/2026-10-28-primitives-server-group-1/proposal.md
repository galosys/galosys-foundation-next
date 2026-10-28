## Why

`primitives-server-composition-1/2` 之后，一个宿主可以创建多个 sealed `ChannelServer`，但没有任何类型负责统一启停。调用方必须在每个宿主或示例里手写启动、失败回滚和停机；第二个 server 绑定失败时，第一个已启动的 listener 会残留，停机时也容易漏掉某个 server。P1.5b 需要把多服务器生命周期收敛到一个独立组合类型，同时不改变单个 `IChannelServer` 的语义。

## What Changes

- 新增 public sealed `ChannelServerGroup`，位于 `Microsoft.Extensions.Hosting.Net`，实现 `IAsyncDisposable` / `IDisposable`。
- 构造函数接收 `params IChannelServer[] servers`，复制输入并暴露 `IReadOnlyList<IChannelServer> Servers`；空组合法。
- `StartAsync` 按 `Servers` 顺序串行启动；某个失败时，按相反顺序停止本次已启动的 server，保留并重新抛出原始异常。
- `StopAsync` 并行停止所有 server，等待全部完成；有失败时抛出包含全部异常的 `AggregateException`。
- `DisposeAsync` / `Dispose` 停止并释放所有 server，重复调用幂等。
- 组只做生命周期组合，不替换、不包装 `IChannelServer` 的 options、`LocalEndPoint`、`ShutdownTimeout` 或错误语义。
- 新增 `ChannelServerGroupTests.cs`：双 loopback server、独立 options、第二个绑定失败的 rollback、统一停机、释放幂等、取消、空组。
- 更新 Primitives README，增加多服务器组合用法。
- 本 change 不提供 DI、宿主集成、`AddChannelServer(definition)` 或 sample 迁移。

## Capabilities

### New Capabilities

- `channel-server-group`：多服务器组合生命周期，包括顺序启动、失败回滚、并行停机、异常聚合和幂等释放。

### Modified Capabilities

无。

## Impact

- 代码：新增 `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/ChannelServerGroup.cs`；修改 `src/Galosys.Foundation.Primitives/README.md`。
- 公共 API：新增 `ChannelServerGroup`；`IChannelServer`、`ChannelServer`、`ChannelServerOptions` 不变。
- 测试：新增 `tests/Galosys.Foundation.Primitives.Tests/ChannelServerGroupTests.cs`；现有单 server 测试不改。
- 依赖：必须在 `primitives-server-composition-1`（提供 sealed `ChannelServer`）之后执行；建议在 `primitives-server-composition-2` 之后执行，避免与旧继承入口并存。不新增 NuGet 包，不引入 `Microsoft.AspNetCore.*`。
- 验证：`dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`、`dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`、`openspec validate "primitives-server-group-1" --strict --no-interactive`。
