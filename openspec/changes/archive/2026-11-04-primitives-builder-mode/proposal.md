## Why

`Galosys.Foundation.Channel` 目前通过 `AddChannelServer(definition)` + `AddChannelServerGroup()` 把多台服务器注册进 DI 容器，再由 `ChannelServerGroupHostedService` 映射到宿主生命周期。这套 IoC/Hosting 集成有三个问题：

1. **把传输框架绑死在通用宿主上**。库的唯一用途是构建 `ChannelServer` / `ChannelClient` / `ChannelClientPool`，但为了支持一条宿主注册路径，它引入了 `Microsoft.Extensions.Hosting.Abstractions` 包依赖，以及 `ChannelServerDefinition` 这个仅用于承载「名称 + options + pipeline 委托」的中间类型。传输框架本身并不需要知道宿主的存在。
2. **配置入口重复且不一致**。服务端当前有两条构造路径（`new ChannelServer(...)` 与 DI 注册），客户端和连接池各只有构造函数。每条路径的校验时机不同：构造函数的校验在 `new` 时发生，而 DI 注册路径的校验要等到容器解析时。
3. **`ChannelServerDefinition` 是纯冗余层**。它把 builder 模式下天然属于同一个实例的三块信息（名称、options、pipeline 委托）拆散成「注册进容器、再在解析时重组」的间接层。

本 change 删掉 IoC/Hosting 集成，改用 Builder 模式作为唯一公开构造入口，让配置、中间件与处理器在一个对象里一次性声明完。

## What Changes

- **BREAKING** 删除 `Microsoft.Extensions.DependencyInjection` 命名空间下的 `ChannelServerGroupServiceCollectionExtensions`（`AddChannelServer` / `AddChannelServerGroup`）。
- **BREAKING** 删除 `Microsoft.Extensions.Hosting` 命名空间下的 `ChannelServerGroupHostedService`。
- **BREAKING** 删除 `Galosys.Foundation.Channel.ChannelServerDefinition`。
- **BREAKING** 删除 `src/Galosys.Foundation.Channel/Microsoft/Extensions/` 目录树（含空的 `DependencyInjection/` 与 `Hosting/` 子目录）。
- **BREAKING** `ChannelServer`、`ChannelClient`、`ChannelClientPool` 的构造函数改为 `internal`；公开构造入口收敛到 builder。
- **BREAKING** csproj 移除 `microsoft.extensions.hosting.abstractions` 包引用。保留 `microsoft.extensions.dependencyinjection.abstractions`（`IServiceScopeFactory` / `IServiceScope` 是每连接作用域能力的必要依赖，与「宿主集成」无关）。
- 新增 public sealed `ChannelServerBuilder`：`Create(options | configure)` → `Use(middleware)` / `UseTls(tls)` / `Run(handler)` / `WithLoggerFactory(...)` / `WithServiceScopeFactory(...)` → `Build()` 产出 `ChannelServer`。
- 新增 public sealed `ChannelClientBuilder`：`Create(options | configure)` → `WithReconnect(...)` / `WithTimeProvider(...)` / `WithKeepAlive(...)` → `Build()` 产出 `IChannelClient`。
- 新增 public sealed `ChannelClientPoolBuilder`：连接池作为 client builder 的一个选项，经 `ChannelClientBuilder.AsPool()` 进入，配置 `WithMaxSize` / `WithRentTimeout` / `WithFactory` 后 `Build()` 产出 `ChannelClientPool`。
- 保留 `ChannelServerGroup`（纯运行时组合类型，与 IoC 无关）；调用方用多个 builder 产出的 server 组成组。
- 保留 `IChannelClientFactory` 与 `ChannelClientFactory` 为 public（连接池的扩展点）。
- 测试迁移：删除 `ServerGroupHostingTests.cs`；`HostingTests.cs` / `ScopedHostingTests.cs` 改为不经宿主直接构造；`NamespaceConventionTests.cs` 增加「已删除类型不再存在」的回归断言；新增 `ChannelServerBuilderTests.cs` 与 `ChannelClientBuilderTests.cs`。
- 示例迁移：`samples/NetServer.ConsoleApp` 改用 builder 构造 server 与 pool。
- 文档迁移：更新 Channel README、`AGENTS.md`，以及 `openspec/specs/` 下 `channel-hosting`、`channel-server-group`、`foundation-namespace-conventions` 三个能力的 spec。
- **不修改** `IChannelServer` / `IChannelClient` / `ChannelContext` / `IChannelSession` 等公共契约；**不改变** 每连接作用域、优雅停机、优雅重连、连接池租借/归还的既有语义。

## Capabilities

### New Capabilities

- `channel-builder`: builder 作为 server / client / client pool 的唯一公开构造入口，覆盖配置快照、中间件注册顺序、终端处理器唯一性、TLS 配置即时校验、参数即时校验与多次构建的独立性。

### Modified Capabilities

- `channel-hosting`: 删除「宿主生命周期」与「多服务器注册」两条 requirement 中对 `ChannelServerGroupHostedService` 和 `AddChannelServer*` 的依赖；改为「调用方自行提供 `IHostedService` 包装」，并明确库不再提供 IoC 集成与每连接作用域的容器绑定。
- `channel-server-group`: 删除「宿主集成」requirement（`ChannelServerGroupHostedService` 的映射语义）；组本身的顺序启动、失败回滚、并行停机、幂等释放语义保持不变。
- `foundation-namespace-conventions`: 删除「宿主适配器与 DI 扩展的命名空间」场景，改为断言这两个类型不再存在于程序集中；核心类型的命名空间约定保持不变。
- `channel-client`: 「客户端配置」requirement 中构造函数校验的表述改为 builder 构建时校验；其余状态机、重连、关闭语义不变。
- `channel-client-pool`: 池的构造入口改为 builder；租借/归还、槽位上限、超时语义不变。

## Impact

- 代码新增：`src/Galosys.Foundation.Channel/Galosys/Foundation/Channel/Builders/ChannelServerBuilder.cs`、`.../Builders/ChannelClientBuilder.cs`、`.../Builders/ChannelClientPoolBuilder.cs`。
- 代码删除：`src/Galosys.Foundation.Channel/Microsoft/Extensions/DependencyInjection/ChannelServerGroupServiceCollectionExtensions.cs`、`src/Galosys.Foundation.Channel/Microsoft/Extensions/Hosting/ChannelServerGroupHostedService.cs`、`src/Galosys.Foundation.Channel/Galosys/Foundation/Channel/ChannelServerDefinition.cs`；整个 `src/Galosys.Foundation.Channel/Microsoft/Extensions/DependencyInjection/` 与 `.../Hosting/` 目录。
- 代码修改：`ChannelServer.cs`、`ChannelClient.cs`、`ChannelClientPool.cs`（构造函数改 internal）、`Galosys.Foundation.Channel.csproj`（移除 hosting 包引用）、`TlsPipelineExtensions.cs`（`UseTls` 校验逻辑复用到 builder）。
- 公共 API：新增 `ChannelServerBuilder`、`ChannelClientBuilder`、`ChannelClientPoolBuilder`；`ChannelServer` / `ChannelClient` / `ChannelClientPool` 的构造函数由 public 变 internal（source breaking）；`ChannelServerDefinition`、`ChannelServerGroupHostedService`、`ChannelServerGroupServiceCollectionExtensions` 被移除。
- 测试：删除 `tests/Galosys.Foundation.Channel.Tests/ServerGroupHostingTests.cs`；修改 `HostingTests.cs`、`ScopedHostingTests.cs`、`NamespaceConventionTests.cs`、`ChannelClientTests.cs`、`ChannelClientPoolTests.cs`；新增 `ChannelServerBuilderTests.cs`、`ChannelClientBuilderTests.cs`。
- 示例：`samples/NetServer.ConsoleApp/Program.cs`、`samples/NetServer.ConsoleApp/SenderBootstrapper.cs`。
- 依赖：移除 `microsoft.extensions.hosting.abstractions`；保留 `microsoft.extensions.dependencyinjection.abstractions` 与 `microsoft.extensions.logging.abstractions`；不新增任何包。
- 文档：`src/Galosys.Foundation.Channel/README.md`、`AGENTS.md`、`openspec/specs/foundation-namespace-conventions/spec.md`、`openspec/specs/channel-hosting/spec.md`、`openspec/specs/channel-server-group/spec.md`、`openspec/specs/channel-client/spec.md`、`openspec/specs/channel-client-pool/spec.md`。
- 破坏性范围：仓库内仅 `tests/Galosys.Foundation.Channel.Tests` 与 `samples/NetServer.ConsoleApp` 使用被移除的 API（全量 grep 已确认）；本库为本地类库，未发布到 nuget.org，无外部消费者。
- 验证：`dotnet build src/Galosys.Foundation.Channel/Galosys.Foundation.Channel.csproj`、`dotnet test tests/Galosys.Foundation.Channel.Tests/Galosys.Foundation.Channel.Tests.csproj`、`dotnet build samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`、`openspec validate "primitives-builder-mode" --strict --no-interactive`。
