# Spec Delta

## MODIFIED Requirements

### Requirement: Options 类型位于 Microsoft.Extensions.Options

foundation 类库中所有以 `Options` 结尾的配置类型 MUST 声明在 `Microsoft.Extensions.Options` 命名空间。配置类型 MUST 为公共可见，否则外部宿主无法绑定配置节。

#### Scenario: Primitives 的 Options 命名空间

- **WHEN** 通过反射读取 `Galosys.Foundation.Channel` 程序集中的 `ChannelServerOptions` 与 `ChannelKeepAliveOptions`
- **THEN** 两个类型的 `Namespace` 均为 `Microsoft.Extensions.Options`
- **AND** 程序集中不存在 `Galosys.Foundation.Channel.ChannelServerOptions` 或 `Galosys.Foundation.Channel.ChannelKeepAliveOptions`

#### Scenario: Doris 与 Nats 的 Options 命名空间

- **WHEN** 检查 `DorisOptions.cs` 与 `NatsOptions.cs` 的 `namespace` 声明
- **THEN** 两者均为 `Microsoft.Extensions.Options`
- **AND** 不再出现 `namespace Galosys.Foundation.Doris` 的 `DorisOptions` 或 `namespace System` 的 `NatsOptions`

#### Scenario: Nats 的 Options 为公共可见

- **WHEN** 通过反射读取 `Galosys.Foundation.Nats.Net` 程序集中的 `NatsOptions`
- **THEN** `NatsOptions` 为公共可见类型
- **AND** 其 `Namespace` 为 `Microsoft.Extensions.Options`

### Requirement: DI 辅助类型位于 Microsoft.Extensions.DependencyInjection

foundation 类库中用于 DI 服务解析的辅助类型 MUST 声明在 `Microsoft.Extensions.DependencyInjection` 命名空间。

#### Scenario: Nats 服务解析辅助的命名空间

- **WHEN** 检查 `ServiceProviderExtensions.cs` 的 `namespace` 声明
- **THEN** 其命名空间为 `Microsoft.Extensions.DependencyInjection`
- **AND** 文件仍保留 `IServiceProvider` 扩展方法

#### Scenario: Nats 注册扩展的命名空间

- **WHEN** 通过反射读取 `Galosys.Foundation.Nats.Net` 程序集中的服务集合扩展类型
- **THEN** 其 `Namespace` 为 `Microsoft.Extensions.DependencyInjection`

### Requirement: 物理目录与命名空间一致

被迁移的类型 MUST 放在与其命名空间对应的目录中，便于按命名空间浏览源码。

#### Scenario: Options 文件目录

- **WHEN** 检查 `ChannelServerOptions.cs`、`ChannelKeepAliveOptions.cs`、`DorisOptions.cs`、`NatsOptions.cs`
- **THEN** 它们分别位于所属项目的 `Microsoft/Extensions/Options/` 目录下

#### Scenario: DI 文件目录

- **WHEN** 检查 `ServiceProviderExtensions.cs`
- **THEN** 它位于所属项目的 `Microsoft/Extensions/DependencyInjection/` 目录下

#### Scenario: 核心类型文件目录

- **WHEN** 检查 `ChannelServer.cs`、`ChannelClient.cs`、`ChannelContext.cs`、`IMessageCodec.cs`、`IMessageFramer.cs`、`IChannelSession.cs`
- **THEN** 它们位于 `Galosys.Foundation.Channel` 项目的 `Galosys/Foundation/Channel/` 目录下

#### Scenario: DurableTask 核心类型文件目录

- **WHEN** 检查 `TaskHubClientExtensions.cs` 与 `ServiceProviderObjectCreator.cs`
- **THEN** 它们位于 `Galosys.Foundation.DurableTask.Core` 项目的 `Galosys/Foundation/DurableTask/Core/` 目录下

#### Scenario: Nats 核心类型文件目录

- **WHEN** 检查 `NatsSubscription`、`NatsConsumerDispatcher` 与 Nats 模块类型所在的 `.cs` 文件路径
- **THEN** 它们位于 `Galosys.Foundation.Nats.Net` 项目的 `Galosys/Foundation/Nats/Net/` 目录下

#### Scenario: Nats 消费者标记文件目录

- **WHEN** 检查消费者标记类型所在的 `.cs` 文件路径
- **THEN** 它位于 `Galosys.Foundation.Nats.Net` 项目的 `NATS.Net/` 目录下
- **AND** 该目录名与其命名空间 `NATS.Net` 一致

#### Scenario: Nats 宿主适配器文件目录

- **WHEN** 检查消费者启动器类型所在的 `.cs` 文件路径
- **THEN** 它位于 `Galosys.Foundation.Nats.Net` 项目的 `Microsoft/Extensions/Hosting/` 目录下

## ADDED Requirements

### Requirement: 消费者标记寄生在上游客户端命名空间

NATS 消费者标记类型 MUST 声明在 `NATS.Net` 命名空间。该命名空间同时由上游 `NATS.Client.Simplified` 包占用（`NATS.Net.NatsClient`、`NATS.Net.NatsClientDefaultSerializerRegistry` 等），C# 允许跨程序集合并同一命名空间，因此不构成类型冲突。该约定与 `galosys.foundation.amqp` 的 `AmqpHandlerAttribute` 声明在 `RabbitMQ.Client` 一致，是**有意为之**的例外，而不是对“核心类型位于 `Galosys.Foundation.Nats.Net`”规则的违反。

#### Scenario: 标记与上游类型共享命名空间

- **WHEN** 通过反射读取 `Galosys.Foundation.Nats.Net` 程序集
- **THEN** 消费者标记类型的 `Namespace` 为 `NATS.Net`
- **AND** 消费方 `using NATS.Net;` 可同时使用上游 `NatsClient` 与框架的消费者标记
- **AND** 两者类型名不冲突

#### Scenario: 与 AMQP 约定一致

- **WHEN** 比较 `AmqpHandlerAttribute` 与 `NatsConsumerAttribute` 的命名空间
- **THEN** 前者为 `RabbitMQ.Client`（上游客户端包命名空间）
- **AND** 后者为 `NATS.Net`（上游客户端包命名空间）

#### Scenario: 框架自身类型不与上游混淆

- **WHEN** 通过反射读取 `Galosys.Foundation.Nats.Net` 程序集
- **THEN** 消费者启动器的 `Namespace` 为 `Microsoft.Extensions.Hosting`
- **AND** 服务集合扩展类型的 `Namespace` 为 `Microsoft.Extensions.DependencyInjection`
- **AND** `NatsOptions` 的 `Namespace` 为 `Microsoft.Extensions.Options`
