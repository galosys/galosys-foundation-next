## Why

`primitives-socket-p1-1` 只提供可手工构造的 `ChannelServerBase`：宿主必须自己创建 `IServiceScopeFactory`、自己调用 `StartAsync`/`StopAsync`，示例里还存在“一个 DI scope 覆盖服务器整个生命周期”的错误用法。P1 的验收标准是 `NetServer.ConsoleApp` 用新 API 行为等价，因此需要把服务端接入通用主机生命周期，并让每连接 scope 真正来自容器。

## What Changes

- 新增 `ChannelServerHostedService`：把 `IChannelServer` 的启动/停机映射到 `IHostedService`，启动失败让宿主启动失败，停机遵守 `ShutdownTimeout`。
- 新增 `ServiceCollectionExtensions.AddChannelServer<TChannelServer>`：注册 `ChannelServerOptions`、服务器单例与 hosted service，并把容器的 `IServiceScopeFactory`/`ILoggerFactory` 注入服务器。
- 宿主启动时校验 Options（fail-fast），非法配置不进入监听状态。
- 每连接 scope 由容器创建；连接内可通过 `ChannelContext.Services` 解析 Scoped 服务，连接结束时释放。
- `src/NetServer.ConsoleApp` 迁移：删除 `ListenerBootstrapper` 的 scope 包裹与手工 `RunAsync`，改用 `AddChannelServer<SampleChannelServer>`；`SampleChannelServer` 构造函数改为接收 `ChannelServerOptions`。
- 在 `src/Galosys.Foundation.Primitives/README.md` 增加“宿主接入”章节、新建 `src/NetServer.ConsoleApp/README.md`，并更新根 `AGENTS.md`（测试项目、`dotnet test` 命令、移除“无测试”描述）。

## Capabilities

### New Capabilities

- `channel-hosting`: DI 注册、宿主生命周期映射、启动校验、每连接作用域与容器释放。

### Modified Capabilities

无。`channel-server-transport` 与 `channel-pipeline` 的行为由 p1-1 建立，本变更只增加宿主层，不改变其规范。

## Impact

- 代码：`src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Hosting/**`（新增）、`src/NetServer.ConsoleApp/Program.cs`、`ListenerBootstrapper.cs`（拆分/删除）、新增 `SampleChannelServer.cs`、`src/NetServer.ConsoleApp/README.md`、`AGENTS.md`。
- 依赖：Primitives 增加 `Microsoft.Extensions.Hosting.Abstractions`（10.0.12），复用已有的 `Microsoft.Extensions.DependencyInjection.Abstractions` / `Microsoft.Extensions.Logging.Abstractions`（10.0.13）；不使用 `Microsoft.AspNetCore.App` 共享框架。
- 公共 API：新增 `AddChannelServer<T>`；`ChannelServerHostedService<T>` 为内部实现。`SampleChannelServer` 构造函数签名变化（示例内部，不是框架 API）。
- 行为：`NetServer.ConsoleApp` 由 hosted service 托管，Ctrl+C 时优雅停机；端口占用在宿主启动阶段失败。
- 验证：`dotnet build src/Galosys.Foundation.Primitives/...`、`dotnet build src/NetServer.ConsoleApp/...`、`dotnet test tests/Galosys.Foundation.Primitives.Tests/...`；`MafSample.slnx` 因含不可编译的 `Nats.Net` 不参与验证。
