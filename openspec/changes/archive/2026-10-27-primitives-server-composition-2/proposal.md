## Why

`primitives-server-composition-1` 已经提供 sealed `ChannelServer`，但 `ChannelServerBase` 仍作为兼容外观存在，`AddChannelServer<T>` 也仍然把全局唯一的 `ChannelServerOptions` 注册进容器。只要这两个入口还在，仓库就有两套服务器写法，而且多服务器调用 `AddChannelServer` 仍会串配置。本 change 完成 P1.5a 的破坏性收尾：删除继承入口和泛型 DI 入口，把 sample/tests 全部迁到组合式 `ChannelServer`。

## What Changes

- **BREAKING** 删除 public `ChannelServerBase`；`ChannelServer` 成为唯一公开服务器实现，`ChannelServerCore` 保持 internal。
- **BREAKING** 删除 `AddChannelServer<TChannelServer>`、`ChannelServerHostedService<TChannelServer>` 和 `Microsoft/Extensions/DependencyInjection/ChannelServerServiceCollectionExtensions.cs`；本 change 不恢复 IoC 注册。
- **BREAKING** `NetServer.ConsoleApp` 迁移：`SampleChannelServer` 改为 handler；`Program.cs` 注册 `ChannelServer` 单例与示例内 `ChannelServerRunner : BackgroundService`；保留 `0.0.0.0:8000`、打印文本、每秒 `hello`、Ctrl+C 优雅停机。
- 迁移测试：删除 `AddChannelServer_RegistersSameServerInstance`；`HostingTests` 改为直接构造 `ChannelServer` 或用测试 runner 托管；`ScopedHostingTests` 通过构造函数传入 `IServiceScopeFactory`；`TestSupport` 删除基类子类。
- 更新 Primitives README 与 `NetServer.ConsoleApp/README.md`，删除继承和 `AddChannelServer` 文档，改为 `ChannelServer` + pipeline 回调。
- 更新受影响的 specs：`channel-server-transport`、`channel-hosting`、`channel-pipeline`、`foundation-namespace-conventions`。
- `ChannelServerGroup`、多服务器托管、IoC/`AddChannelServer(definition)` 留给 P1.5b/后续 change。

## Capabilities

### New Capabilities

无。

### Modified Capabilities

- `channel-server-transport`: 删除 `ChannelServerBase` 兼容入口；组合式 `ChannelServer` 成为唯一公开实现；生命周期要求保持不变。
- `channel-hosting`: 删除 `AddChannelServer<T>` / `ChannelServerHostedService<T>` 的注册与宿主生命周期要求；改为调用方用 `ChannelServer` + 示例 runner 接入宿主；每连接 scope 仍由传入的 `IServiceScopeFactory` 提供。
- `channel-pipeline`: pipeline 配置入口从 `ChannelServerBase.Configure` 改为 `ChannelServer` 构造函数的 `Action<IChannelPipeline>`；中间件顺序与 `Run` 约束不变。
- `foundation-namespace-conventions`: 删除 `AddChannelServer` 专属的命名空间/注册行为场景；保留 Options 与其余 DI 辅助类型的命名空间约定。

## Impact

- 代码：删除 `ChannelServerBase.cs`、`ChannelServerServiceCollectionExtensions.cs`、`Hosting/ChannelServerHostedService.cs`；修改 `Program.cs`、`SampleChannelServer.cs`、新增 `ChannelServerRunner.cs`；修改 `TestSupport.cs`、`HostingTests.cs`、`ScopedHostingTests.cs`、`ShutdownTests.cs`、`DependencyBoundaryTests.cs`、`NamespaceConventionTests.cs`。
- 公共 API：`ChannelServerBase`、`AddChannelServer<T>` 删除；`ChannelServer` 保持 change 1 的签名。仓库内无外部消费方。
- 依赖：不新增 NuGet 包；仍不引入 `Microsoft.AspNetCore.*`。
- 测试：现有 67 个测试中 1 个删除，其余迁移；新增宿主 runner 连接测试与“无 DI 直接构造”测试。
- 验证：`dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`、`dotnet build samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`、`dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`。
- 迁移：必须在 `primitives-server-composition-1` 合入后执行；回滚本 change 需要同时恢复基类、DI 扩展、hosted service 与 sample/tests 文档。
