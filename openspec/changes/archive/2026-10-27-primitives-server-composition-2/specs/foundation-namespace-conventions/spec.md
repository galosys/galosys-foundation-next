## ADDED Requirements

### Requirement: DI 辅助类型位于 Microsoft.Extensions.DependencyInjection

foundation 类库中用于 DI 服务解析的辅助类型 MUST 声明在 `Microsoft.Extensions.DependencyInjection` 命名空间。

#### Scenario: Nats 服务解析辅助的命名空间

- **WHEN** 检查 `ServiceProviderExtensions.cs` 的 `namespace` 声明
- **THEN** 其命名空间为 `Microsoft.Extensions.DependencyInjection`
- **AND** 文件仍保留 `IServiceProvider` 扩展方法

## MODIFIED Requirements

### Requirement: 物理目录与命名空间一致

被迁移的类型 MUST 放在与其命名空间对应的目录中，便于按命名空间浏览源码。

#### Scenario: Options 文件目录

- **WHEN** 检查 `ChannelServerOptions.cs`、`ChannelKeepAliveOptions.cs`、`DorisOptions.cs`、`NatsOptions.cs`
- **THEN** 它们分别位于所属项目的 `Microsoft/Extensions/Options/` 目录下

#### Scenario: DI 文件目录

- **WHEN** 检查 `ServiceProviderExtensions.cs`
- **THEN** 它位于所属项目的 `Microsoft/Extensions/DependencyInjection/` 目录下

## REMOVED Requirements

### Requirement: 命名空间迁移不改变注册行为

**Reason**: `AddChannelServer<TChannelServer>` 被删除，该要求描述的注册行为不再存在。

**Migration**: 组合式 `ChannelServer` 由调用方直接构造；宿主集成由调用方 runner 负责，不再依赖 `AddChannelServer`。

### Requirement: DI 注册与辅助类型位于 Microsoft.Extensions.DependencyInjection

**Reason**: `ChannelServerServiceCollectionExtensions` 与 `AddChannelServer<TChannelServer>` 被删除，原要求中的注册扩展场景不再存在。

**Migration**: DI 辅助类型的命名空间约定由新增的“DI 辅助类型位于 Microsoft.Extensions.DependencyInjection”要求覆盖。
