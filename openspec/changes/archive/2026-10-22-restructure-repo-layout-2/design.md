## Context

仓库已有“命名空间寄生”实践：`MafSample.ConsoleApp` 的批处理辅助类放在 `Microsoft.Extensions.Hosting`，`NatsConsumerAttribute` 放在 `NATS.Net`。但 foundation 类库没有统一执行这一约定：

- `ChannelServerOptions`、`ChannelKeepAliveOptions` 与核心宿主类型混在 `Microsoft.Extensions.Hosting.Net`。
- `DorisOptions` 留在 `Galosys.Foundation.Doris`，与使用它的 `IOptionsSnapshot<DorisOptions>` 不在同一命名空间。
- `NatsOptions` 与 `ServiceProviderExtensions` 放在 `System`。
- `ChannelServerServiceCollectionExtensions` 放在 `Microsoft.Extensions.Hosting.Net`，调用方必须额外引用宿主命名空间才能发现 `AddChannelServer`。

本变更只做命名空间和文件目录调整，不改变 Options 的注册方式或任何运行时行为。Nats 项目当前不可编译，相关改动只能做静态校验。`NetServer.ConsoleApp` 的路径以 `restructure-repo-layout-1` 已应用为前提。

## Goals / Non-Goals

**Goals:**

- Options 类型统一位于 `Microsoft.Extensions.Options`，文件位于 `Microsoft/Extensions/Options/`。
- DI 注册/辅助扩展统一位于 `Microsoft.Extensions.DependencyInjection`，文件位于 `Microsoft/Extensions/DependencyInjection/`。
- 受影响的 Primitives 源码、测试与 NetServer 示例补齐 `using`，保持构建与测试通过。
- 用自动化测试锁定 Primitives 的命名空间约定。
- 在 README/AGENTS 中记录约定，防止后续类型再次放错位置。

**Non-Goals:**

- 不把 `ChannelServerOptions` 改为通过 `IOptions<ChannelServerOptions>` 注册。`AddChannelServer` 仍直接创建、校验并以单例注册 Options，避免改变既有宿主行为。
- 不移动核心类型 `ChannelServerBase`、`ChannelContext`、`IChannelServer`、管线与传输类型。
- 不移动 `EmptyServiceScope`（内部 `IServiceScope` 实现，不是公开 DI 扩展）与 `DorisStreamLoadClient`（服务实现，不是注册扩展）。
- 不修复 `Galosys.Foundation.Nats.Net` / `NatsSample.ConsoleApp` 的编译错误。
- 不调整 `NatsConsumerBootstrapper` 等与 Options/DI 无关的遗留命名空间。

## Decisions

### D1：按类型职责而非按“是否引用 Microsoft.Extensions”划分命名空间

Options 类是配置载体，统一放 `Microsoft.Extensions.Options`；`IServiceCollection`/`IServiceProvider` 扩展是 DI 入口，统一放 `Microsoft.Extensions.DependencyInjection`。这样调用方在写 `services.Configure<DorisOptions>(...)` 或 `services.AddChannelServer(...)` 时，已有的 Options/DI `using` 就能覆盖这些类型。

替代方案：所有 foundation 类型继续留在 `Microsoft.Extensions.Hosting.Net`。无法体现 Options/DI 的独立职责，且 Doris/Nats 无法共用该命名空间；不采用。

### D2：只改命名空间，不切换 Options 注册模式

`AddChannelServer` 当前在注册阶段直接 `new ChannelServerOptions()`、执行 `configure`、调用 `Validate()` 并注册为单例；`ChannelServerBase` 构造函数直接接收该实例。改成 `IOptions<ChannelServerOptions>` 会引入配置系统、验证时机和生命周期语义变化，超出本次“命名空间寄生”的范围。

替代方案：顺带改为标准 Options 模式。会改变 `AddChannelServer` 的注册行为并需要更新 `channel-hosting` 规格；本次不做。

### D3：物理目录跟随命名空间

每个项目建立 `Microsoft/Extensions/Options/` 与 `Microsoft/Extensions/DependencyInjection/` 目录。Primitives 现有 `Hosting/Net/Options/` 目录整体上移为 `Microsoft/Extensions/Options/`；`ChannelServerServiceCollectionExtensions.cs` 从 `Hosting/Net/Hosting/` 移到 `Microsoft/Extensions/DependencyInjection/`。

替代方案：只改 `namespace` 不改文件路径。会造成目录与命名空间长期漂移，与仓库现有的 `Microsoft/Extensions/Hosting/...` 布局不一致；不采用。

### D4：用反射测试锁定 Primitives 的命名空间

在 `tests/Galosys.Foundation.Primitives.Tests/NamespaceConventionTests.cs` 中通过程序集反射检查类型全名：

```csharp
var assembly = typeof(ChannelServerBase).Assembly;
Assert.NotNull(assembly.GetType("Microsoft.Extensions.Options.ChannelServerOptions"));
Assert.NotNull(assembly.GetType("Microsoft.Extensions.Options.ChannelKeepAliveOptions"));
Assert.NotNull(assembly.GetType("Microsoft.Extensions.DependencyInjection.ChannelServerServiceCollectionExtensions"));
Assert.Null(assembly.GetType("Microsoft.Extensions.Hosting.Net.ChannelServerOptions"));
```

这样测试不需要在编译期引用新命名空间，可以先写测试观察失败，再迁移类型使其通过。Doris/Nats 无独立测试项目，用 `rg` 做静态校验。

### D5：Nats 内部类型同样迁移，但只做静态校验

`NatsOptions` 与 `ServiceProviderExtensions` 都是 internal，迁移不影响外部调用方。`Nats.Net` 当前不可编译，无法通过构建验证；按约定同步修改，并在任务中要求用 `rg` 确认 namespace 与目录，不把 Nats 构建纳入通过条件。

## Risks / Trade-offs

- [public Options 类型命名空间变化导致外部调用方编译失败] → 本仓库内全部调用方同步更新；在 proposal 中标记 **BREAKING**，README 记录新命名空间。
- [Primitives 项目未引用 `Microsoft.Extensions.Options` 包，声明该命名空间可能引起误解] → 仅声明命名空间不需要包引用；只有使用 `IOptions<T>` 等类型时才需要。Doris/Nats 已有 Options 包依赖。
- [测试文件遗漏 `using Microsoft.Extensions.Options` 导致构建失败] → 逐一更新 7 个使用 `ChannelServerOptions` 的测试文件，并以 `dotnet test` 全量验证。
- [Nats 改动无法编译验证，可能引入拼写错误] → 用 `rg` 检查 namespace、目录和 `using`；Nats 仍按已知限制排除构建。
- [NetServer 示例路径依赖前一个 change] → 在 proposal/design 中声明前置条件；若单独执行本 change，需要先把示例路径按当前布局映射。
- [误把 `EmptyServiceScope` 等内部实现也搬走，扩大改动面] → Non-Goals 明确排除；只迁移 Options 类与 DI 扩展类。

## Migration Plan

1. 新增失败的命名空间约定测试并运行，确认当前 `Microsoft.Extensions.Options.ChannelServerOptions` 不存在。
2. 移动 Primitives 的 Options 文件与 DI 扩展文件，修改 namespace，补齐源码 `using`。
3. 更新 7 个测试文件的 `using`，运行测试使其通过。
4. 移动 `DorisOptions.cs`、`NatsOptions.cs`、`ServiceProviderExtensions.cs`，修改 namespace。
5. 更新 `NetServer.ConsoleApp` 的 `using`，构建示例。
6. 更新 Primitives README 与 AGENTS 命名空间约定。
7. 执行 Primitives/Doris 构建、测试、NetServer 构建，并用 `rg` 检查旧命名空间与目录已消失。

回滚策略：本次只移动文件与改 namespace/using，可用 `git mv` 将文件移回原目录并恢复 namespace；测试文件与文档用 `git checkout --` 恢复。无数据迁移。
