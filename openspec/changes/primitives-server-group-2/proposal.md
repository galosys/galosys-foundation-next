## Why

`primitives-server-group-1` 提供了 `ChannelServerGroup`，但组仍需要调用方手工创建、注册到宿主并映射启停。多个 server 的 options 如果继续走全局 `ChannelServerOptions` 注册，会再次出现端口串配置；如果每个定义都用工厂手写，宿主启动顺序、失败回滚和作用域传播又会散落在示例里。本 change 把“定义 → 组 → 宿主”固定为一条注册路径，同时保持每个定义独立配置。

## What Changes

- 新增 public sealed `ChannelServerDefinition`：`Name`、`ChannelServerOptions` 和 `Action<IChannelPipeline>` 管线配置委托；构造时校验并快照 options。
- 新增 public sealed `ChannelServerGroupHostedService`，实现 `IHostedService` / `IAsyncDisposable`，把 `IHost.StartAsync` / `StopAsync` 映射到 `ChannelServerGroup`。
- 新增 `IServiceCollection` 扩展 `AddChannelServer(ChannelServerDefinition)` 与 `AddChannelServerGroup()`，命名空间为 `Microsoft.Extensions.DependencyInjection`。
- `AddChannelServerGroup()` 从所有已注册定义构建一个 `ChannelServerGroup`，使用容器的 `ILoggerFactory` 与 `IServiceScopeFactory`；每个定义保留自己的 `ChannelServerOptions`，不注册全局 `ChannelServerOptions`。
- 明确定义注册顺序即启动顺序；重复调用 `AddChannelServerGroup()` 幂等；只注册定义但不调用 `AddChannelServerGroup()` 时定义保持惰性。
- 新增 `ServerGroupHostingTests.cs`：两个定义、独立 options、宿主启停、缺少/重复组注册、scope factory 传播、启动失败清理。
- 更新 Primitives README 的多服务器宿主注册章节。
- 本 change 不迁移 sample，不新增 sample 专用 runner；调用方仍可继续使用自定义 `IHostedService`。

## Capabilities

### New Capabilities

无。

### Modified Capabilities

- `channel-hosting`：新增 `多服务器注册` 要求，并扩展 `宿主生命周期` 以覆盖 `ChannelServerGroupHostedService` 对组的启停、失败传播和释放。
- `channel-server-group`：增加宿主集成要求，规定组可以被 `ChannelServerGroupHostedService` 托管，但不改变组自身的启动、停机和释放语义。

## Impact

- 代码：新增 `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/ChannelServerDefinition.cs`、`src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Hosting/ChannelServerGroupHostedService.cs`、`src/Galosys.Foundation.Primitives/Microsoft/Extensions/DependencyInjection/ChannelServerGroupServiceCollectionExtensions.cs`；修改 `src/Galosys.Foundation.Primitives/README.md`。
- 公共 API：新增 `ChannelServerDefinition`、`ChannelServerGroupHostedService`、`AddChannelServer(ChannelServerDefinition)`、`AddChannelServerGroup()`；`ChannelServerGroup` 与 `IChannelServer` 不变。
- 测试：新增 `tests/Galosys.Foundation.Primitives.Tests/ServerGroupHostingTests.cs`；现有单服务器宿主测试不改。
- 依赖：必须在 `primitives-server-group-1` 之后执行；建议在 `primitives-server-composition-2` 之后执行，避免与已删除的 `AddChannelServer<T>` 混淆。不新增 NuGet 包，不引入 `Microsoft.AspNetCore.*`。
- 验证：`dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`、`dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`、`openspec validate "primitives-server-group-2" --strict --no-interactive`。
