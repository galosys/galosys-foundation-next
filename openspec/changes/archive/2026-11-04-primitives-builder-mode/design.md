# Design

## Context

见 `proposal.md` 的 Why。本设计需要解决的现状约束：

- `ChannelServerCore` 在 `StartAsync` 时调用 `Action<IChannelPipeline>` 回调来物化管线，`ChannelPipeline` 是 internal 类型，`Run` 只能调用一次且返回 `void`。
- `ChannelServerOptions.Clone()` / `ChannelClientOptions.Clone()` / `ChannelClientPoolOptions.Clone()` 都是 `internal`，只有同程序集内能调用。
- `ChannelServerOptions.Validate()` / `ChannelClientOptions.Validate()` / `ChannelClientPoolOptions.Validate()` 都是 `internal`，且分别在 `ChannelServerCore.StartAsync`、`ChannelClient` 构造函数、`ChannelClientPool` 构造函数中调用 —— 校验时机目前分散在三处。
- `ChannelTlsOptions.ValidateServer()` 已经是 `internal`，`TlsPipelineExtensions.UseTls` 在**注册时**（而非启动时）调用它做校验。
- `IChannelPipeline.Use(ChannelMiddleware)` 与 `Run(ChannelHandlerDelegate)` 是构建器需要复用的两个动词，但 `Run` 返回 `void`，与 fluent builder 的链式返回 `this` 冲突。
- `ChannelServerGroup` 的构造函数接受 `params IChannelServer[]`，`ChannelServer` 实现 `IChannelServer`。
- `InternalsVisibleTo` 已配置 `Galosys.Foundation.Channel.Tests`，测试程序集可以看到 internal 成员。

## Goals / Non-Goals

**Goals:**

- builder 成为 server / client / client pool 的唯一公开构造入口；三个类型的构造函数全部非公开。
- 保留每连接作用域能力（`IServiceScopeFactory` / `IServiceScope`），因此保留 `microsoft.extensions.dependencyinjection.abstractions`。
- 移除对 `Microsoft.Extensions.Hosting` 的全部依赖。
- 校验时机统一：配置类参数在 builder 方法调用时立即校验，跨对象一致性在 `Build()` 时校验。
- 中间件注册顺序与 `Run` 唯一性的语义与现有 `ChannelPipeline` 完全一致，不引入第二套管线语义。

**Non-Goals:**

- 不改变 `IChannelServer` / `IChannelClient` / `ChannelContext` / `IChannelSession` / `IMessageFramer` / `IMessageCodec` 等公共契约。
- 不改变每连接作用域、优雅停机、自动重连、连接池租借/归还的既有运行时语义。
- 不实现客户端 TLS（`ChannelClientOptions.Tls` 仍只做校验，`ConnectAsync` 仍抛 `NotSupportedException`）。
- 不为 `ChannelServerGroup` 引入 builder —— 它是纯运行时组合类型，没有配置面。
- 不保留被删除 API 的兼容垫片（无 `[Obsolete]` 过渡期）。

## Decisions

### 决策 1：builder 不实现 `IChannelPipeline`

**选择**：builder 各自持有中间件列表与终端处理器，`Build()` 时物化成 `Action<IChannelPipeline>` 交给 `ChannelServer`。

**理由**：

- `IChannelPipeline.Run` 返回 `void`，与 fluent builder 的 `Run(handler) → this` 直接冲突。要么让 builder 实现接口并放弃链式返回（那 `Use` 就无法链式），要么让 builder 实现接口但用另一个方法名表示"设置终端处理器"（例如 `RunHandler`），与现有 `pipeline.Run(...)` 的词汇不一致。
- `IChannelPipeline` 的语义是"一次性的管线实例"：`Run` 只能调一次、`Build()` 后不能加中间件。builder 的语义是"可累积的配置描述"，同一 builder 可以构建多个实例。让一个类型同时扮演两种角色会让"重复 Run 该抛错还是该覆盖"变得含糊。
- builder 物化出的 `Action<IChannelPipeline>` 仍然交给现有 `ChannelPipeline` 执行，管线语义零改动。

**备选方案**：

- *builder 实现 `IChannelPipeline`*：需要把 `Run` 改名为其他名字，或接受 `Run` 返回 `void` 导致链式断裂。放弃。
- *builder 持有一个 internal `ChannelPipeline` 实例直接传给 `ChannelServer`*：`ChannelPipeline.Build()` 是 internal 单例语义（`_built` 缓存），一个 `ChannelPipeline` 只能构建一次，无法支持"一个 builder 构建多个实例"。可以在 `Build()` 时每次 new 一个新的 `ChannelPipeline` 绕开，但那样中间件列表的复制逻辑要自己写，与方案一重复。放弃。

### 决策 2：中间件在 `Build()` 时物化，`UseTls` 在注册时校验

**选择**：`ChannelServerBuilder` 的 `Use` / `Run` 只把委托和中间件记进列表；`Build()` 时构造 `Action<IChannelPipeline>`，闭包捕获中间件数组与终端处理器。`UseTls` 单独在**调用时**调用 `ChannelTlsOptions.ValidateServer()`。

**理由**：

- 物化放在 `Build()` 而不是 `Use()`，是为了让"注册顺序 = 执行顺序"这条规则由一次遍历保证，而不是靠每次调用都往一个共享的 `ChannelPipeline` 上追加。
- `UseTls` 必须在调用时校验，是因为现有 `TlsPipelineExtensions.UseTls` 就是这么做的 —— 缺少服务端证书时立刻失败，而不是等到第一次连接进来才暴露。builder 若把这个校验推迟到 `Build()`，会让"配好 TLS 却到 build 才报错"成为可能，比现状差。
- `ValidateServer()` 是 `internal`，builder 在同程序集内可直接调用，无需放宽可见性。

**备选方案**：

- *把 `UseTls` 也推迟到 `Build()` 校验*：错误暴露更晚，与现有 `UseTls` 行为不一致。放弃。
- *builder 直接复用 `TlsPipelineExtensions.UseTls`*：`UseTls` 是 `IChannelPipeline` 的扩展方法，需要一个 `IChannelPipeline` 实例；builder 没有。可以在 `Build()` 物化时调用它 —— 但那样校验就推迟了。折中方案：builder 在 `UseTls` 时先调 `ValidateServer()` 做即时校验，`Build()` 物化时再调 `TlsPipelineExtensions.UseTls`（它内部会再校验一次，幂等）。**采用此方案**，复用既有扩展方法而不是重新实现 TLS 中间件。

### 决策 3：`ChannelServerBuilder` 直接构造 `ChannelServer`，不经中间工厂

**选择**：`Build()` 直接 `new ChannelServer(options, configure, loggerFactory, scopeFactory)`。

**理由**：`ChannelServer` 的构造函数就在同程序集内，改成 `internal` 后 builder 完全可以直接调用。引入额外工厂只会多一层间接，没有收益。

### 决策 4：连接池通过 `AsPool()` 进入，作为 client builder 的一个选项

**选择**：`ChannelClientBuilder.AsPool()` 返回一个 `ChannelClientPoolBuilder`，后者持有 client builder 已配置的 options，并提供 `WithMaxSize` / `WithRentTimeout` / `WithFactory`。

**理由**：

- 用户要求"server+client pool 作为 client 构建的一个选项"。`AsPool()` 把"构建目标"表达成 client 配置上的一个切换，而不是要求调用方在两个平行的 builder 之间做选择。
- 池配置的根是 `ChannelClientOptions`（`ChannelClientPoolOptions.ClientOptions`），两者共享同一份客户端配置是自然的；`AsPool()` 天然完成这个传递。
- `ChannelClientPoolBuilder` 作为独立 public 类型存在，是因为它有自己的三个配置项和不同的 `Build()` 返回类型。

**备选方案**：

- *只提供 `ChannelClientBuilder.WithPool(maxSize, rentTimeout)` 一个方法*：会把池的两个配置项硬编码进方法签名，扩展性差（无法注入工厂）。放弃。
- *独立的 `ChannelClientPoolBuilder.Create(options)` 静态入口，与 client builder 并列*：丢失了"池继承 client 配置"的语义，且用户明确要求池是 client 的一个选项。放弃。

### 决策 5：保留 `Microsoft.Extensions.DependencyInjection.Abstractions`，删除 `Microsoft.Extensions.Hosting.Abstractions`

**选择**：只删 hosting 包引用。

**理由**：

- `ChannelServerCore` 用 `IServiceScopeFactory` / `IServiceScope` 实现"每连接独立作用域"，`EmptyServiceScope` 实现 `IServiceScope`，`ChannelContext.Services` 依赖 `IServiceProvider`。删除这个包会直接丢失 `ScopedHostingTests` 覆盖的能力，也会让 `ChannelContext.Services` 退化为 `null`。
- 这个包提供的是**作用域抽象**，与「把服务注册进容器」是两件事。库依然不提供任何 `IServiceCollection` 扩展。
- hosting 包在删除 `ChannelServerGroupHostedService` 后已无任何使用者，保留它会是无意义的依赖。

**备选方案**：

- *连 DI.Abstractions 一起删，改用自定义 `IChannelScopeFactory` 抽象*：会引入新概念、新接口和新测试面，属于超出本次范围的抽象重构。放弃。

### 决策 6：删除 `ChannelServerDefinition`

**选择**：删除该类型。

**理由**：它的唯一用途是承载「名称 + options + pipeline 委托」以便在容器里收集、稍后重组。builder 模式下这三块信息天然在同一个 builder 实例里，该类型变成纯冗余中间层。其 `Name` 字段（诊断名称）在 builder 模式下没有承载者 —— 接受这一损失，因为 `ChannelServer` 本身不暴露名称，当前也没有任何按名称查找 server 的能力。

### 决策 7：保留 `ChannelServerGroup` 与 `IChannelClientFactory`

**选择**：两者都保持 public 且行为不变。

**理由**：

- `ChannelServerGroup` 是纯运行时组合类型（不构造配置、不依赖容器），它的顺序启动 / 失败回滚 / 并行停机 / 幂等释放语义是独立于 IoC 的能力。删除它会让多服务器编排无处安放。
- `IChannelClientFactory` 是 `ChannelClientPool` 的扩展点（自定义客户端创建方式），与 IoC 无关 —— 它是普通接口，调用方可以直接 `new` 实现并传给池的构建路径。

### 决策 8：`ChannelClientPool` 的 `MaxSize` / `RentTimeout` 在 setter 方法调用时校验

**选择**：`WithMaxSize(int)` 与 `WithRentTimeout(TimeSpan)` 立即校验参数合法性，非法值抛 `ArgumentException`。

**理由**：`ChannelClientPoolOptions.Validate()` 是 internal 且在构造函数中一次性校验全部字段。如果 builder 的 setter 允许写入非法值，错误会延迟到 `Build()` 才暴露，调用方在链式调用中拿不到即时反馈。逐参数即时校验 + `Build()` 时再整体校验，两者不冲突（`Validate()` 是幂等的全量校验）。

### 决策 9：测试也统一走 builder

**选择**：`InternalsVisibleTo` 允许测试直接用 internal 构造函数，但迁移后的测试仍统一走 builder。

**理由**：测试是公开 API 的主要消费者。让测试走 builder，可以在每次重构中持续验证"builder 是唯一可用路径"这条不变量；直接用 internal 构造函数会绕过这条验证。

**例外**：`NamespaceConventionTests` 需要直接反射检查构造函数可见性，这是它的职责所在。

## Risks / Trade-offs

- **风险：`ChannelServerDefinition` 的 `Name` 字段丢失** → 缓解：当前没有任何按名称查找 server 的公开能力（`ChannelServerGroup.Servers` 只有 `IChannelServer` 列表），因此无实际损失。若将来需要，可在 `ChannelServerGroup` 上另加诊断名映射。

- **风险：构造函数改 internal 是 source + binary breaking** → 缓解：本库为本地类库，未发布到 nuget.org；全量 grep 已确认仓库内仅 `tests/Galosys.Foundation.Channel.Tests` 与 `samples/NetServer.ConsoleApp` 使用被移除的 API，两处都在本 change 的迁移范围内。

- **风险：`Build()` 物化 `Action<IChannelPipeline>` 时闭包捕获中间件数组** → 缓解：`Build()` 时用 `ToArray()` 做一次快照，之后修改 builder 的列表不影响已构建实例。闭包捕获的是快照数组而非 builder 本身，因此不存在 builder 被复用时的状态串扰。

- **风险：每个 server 启动都会重新物化管线** → 缓解：这是既有行为（`ChannelServerCore.StartAsync` 每次启动尝试都调用 `configurePipeline`）。builder 物化出的 `Action` 每次遍历中间件数组并调用 `pipeline.Use(...)`，与直接写 `pipeline => pipeline.Use(a).Use(b).Run(h)` 的行为一致。

- **风险：`ChannelServerOptions.Clone()` 是 internal，builder 需在同程序集内调用** → 缓解：builder 就在 `Galosys.Foundation.Channel` 程序集内，无可见性问题。

- **风险：删除 `Microsoft/Extensions/` 目录树后 `Microsoft.Extensions.Options` 目录仍需保留** → 缓解：只删 `Microsoft/Extensions/DependencyInjection/` 与 `Microsoft/Extensions/Hosting/` 两个子目录，`Microsoft/Extensions/Options/` 保留（Options 类型仍寄生在该命名空间下，这是 `foundation-namespace-conventions` 的硬性约定）。

## Migration Plan

1. **新增 builder 类型**（纯新增，不破坏现有代码）：`ChannelServerBuilder`、`ChannelClientBuilder`、`ChannelClientPoolBuilder`。此时构造函数仍是 public，新旧路径并存。
2. **构造函数改 internal**：删除 `Microsoft/Extensions/` 下的两个扩展文件、`ChannelServerDefinition`，csproj 移除 hosting 包引用。**此步之后库不再编译**（测试与示例仍用旧构造函数）。
3. **迁移测试**：删除 `ServerGroupHostingTests.cs`；改造 `HostingTests` / `ScopedHostingTests` / `ChannelClientTests` / `ChannelClientPoolTests`；更新 `NamespaceConventionTests`；新增两个 builder 测试文件。
4. **迁移示例**：`samples/NetServer.ConsoleApp`。
5. **更新文档**：README、`AGENTS.md`、openspec 主 spec。

回滚策略：步骤 1 之后任意步骤失败，可通过恢复构造函数可见性 + 恢复被删文件回退；步骤 1 本身是纯新增，无回滚需要。

## Open Questions

无。所有影响 spec、方案与任务拆分的决策均已在本文件确定。
