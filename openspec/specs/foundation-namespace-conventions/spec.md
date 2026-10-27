# foundation-namespace-conventions Specification

## Purpose
定义 foundation 类库中 Options 类型、DI 辅助类型的命名空间与物理目录约定，确保类型归属可预测。
## Requirements
### Requirement: Options 类型位于 Microsoft.Extensions.Options

foundation 类库中所有以 `Options` 结尾的配置类型 MUST 声明在 `Microsoft.Extensions.Options` 命名空间。

#### Scenario: Primitives 的 Options 命名空间

- **WHEN** 通过反射读取 `Galosys.Foundation.Primitives` 程序集中的 `ChannelServerOptions` 与 `ChannelKeepAliveOptions`
- **THEN** 两个类型的 `Namespace` 均为 `Microsoft.Extensions.Options`
- **AND** 程序集中不存在 `Microsoft.Extensions.Hosting.Net.ChannelServerOptions` 或 `Microsoft.Extensions.Hosting.Net.ChannelKeepAliveOptions`

#### Scenario: Doris 与 Nats 的 Options 命名空间

- **WHEN** 检查 `DorisOptions.cs` 与 `NatsOptions.cs` 的 `namespace` 声明
- **THEN** 两者均为 `Microsoft.Extensions.Options`
- **AND** 不再出现 `namespace Galosys.Foundation.Doris` 的 `DorisOptions` 或 `namespace System` 的 `NatsOptions`

### Requirement: DI 辅助类型位于 Microsoft.Extensions.DependencyInjection

foundation 类库中用于 DI 服务解析的辅助类型 MUST 声明在 `Microsoft.Extensions.DependencyInjection` 命名空间。

#### Scenario: Nats 服务解析辅助的命名空间

- **WHEN** 检查 `ServiceProviderExtensions.cs` 的 `namespace` 声明
- **THEN** 其命名空间为 `Microsoft.Extensions.DependencyInjection`
- **AND** 文件仍保留 `IServiceProvider` 扩展方法

### Requirement: 物理目录与命名空间一致

被迁移的类型 MUST 放在与其命名空间对应的目录中，便于按命名空间浏览源码。

#### Scenario: Options 文件目录

- **WHEN** 检查 `ChannelServerOptions.cs`、`ChannelKeepAliveOptions.cs`、`DorisOptions.cs`、`NatsOptions.cs`
- **THEN** 它们分别位于所属项目的 `Microsoft/Extensions/Options/` 目录下

#### Scenario: DI 文件目录

- **WHEN** 检查 `ServiceProviderExtensions.cs`
- **THEN** 它位于所属项目的 `Microsoft/Extensions/DependencyInjection/` 目录下
