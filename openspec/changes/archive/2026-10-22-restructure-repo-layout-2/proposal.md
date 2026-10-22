## Why

foundation 类库中的 Options 与 DI 辅助类型目前分散在多个命名空间：`ChannelServerOptions` 留在 `Microsoft.Extensions.Hosting.Net`，`DorisOptions` 留在业务命名空间，`NatsOptions` 和 `ServiceProviderExtensions` 甚至放在 `System`；而 `AddChannelServer` 又和宿主类型混在一起。调用方需要额外的 `using` 才能发现这些类型，也与仓库已有的“命名空间寄生”约定不一致。把它们归入 `Microsoft.Extensions.Options` / `Microsoft.Extensions.DependencyInjection` 后，使用方在引用 Options 或注册 DI 时可以自然发现这些类型。

## What Changes

- 将以下 Options 类型改为 `Microsoft.Extensions.Options` 命名空间，并把文件移动到对应的 `Microsoft/Extensions/Options/` 目录：
  - `ChannelServerOptions`、`ChannelKeepAliveOptions`（Primitives）
  - `DorisOptions`（Doris）
  - `NatsOptions`（Nats，internal）
- 将以下 DI 相关类型改为 `Microsoft.Extensions.DependencyInjection` 命名空间，并移动到对应的 `Microsoft/Extensions/DependencyInjection/` 目录：
  - `ChannelServerServiceCollectionExtensions`（Primitives）
  - `ServiceProviderExtensions`（Nats，internal）
- 在受影响的 foundation 源码、Primitives 测试和 `NetServer.ConsoleApp` 示例中补齐 `using`。
- 新增命名空间约定测试，锁定 Primitives 程序集中的 Options/DI 类型命名空间。
- 更新 `Galosys.Foundation.Primitives/README.md` 与 `AGENTS.md`，记录命名空间约定。
- **BREAKING**：`ChannelServerOptions`、`ChannelKeepAliveOptions`、`DorisOptions` 的命名空间变化；外部调用方需要改用 `Microsoft.Extensions.Options`。本仓库内的调用方会同步更新。

## Capabilities

### New Capabilities

- `foundation-namespace-conventions`：规定 foundation 类库中 Options 类型与 DI 注册/辅助类型必须分别位于 `Microsoft.Extensions.Options` 与 `Microsoft.Extensions.DependencyInjection` 命名空间，并与物理目录保持一致。

### Modified Capabilities

无。`channel-hosting` 等既有规格描述的是注册行为与运行时语义，本次只调整命名空间和目录，不改变 `AddChannelServer` 的注册结果。

## Impact

- 修改 `src/Galosys.Foundation.Primitives`、`src/Galosys.Foundation.Doris`、`src/Galosys.Foundation.Nats.Net` 中的类型命名空间与文件路径。
- 修改 `tests/Galosys.Foundation.Primitives.Tests` 中 7 个使用 `ChannelServerOptions` 的文件，并新增命名空间约定测试。
- 修改 `samples/NetServer.ConsoleApp` 的 `using`；该路径以 `restructure-repo-layout-1` 已应用为前提。
- 不改变任何运行时行为；`Galosys.Foundation.Nats.Net` 仍不可编译，Nats 侧改动只能做静态检查。
