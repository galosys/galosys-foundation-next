# foundation-namespace-conventions Specification

## Purpose
定义 foundation 类库中 Options 类型、DI 辅助类型的命名空间与物理目录约定，确保类型归属可预测。
## Requirements
### Requirement: Options 类型位于 Microsoft.Extensions.Options

foundation 类库中所有以 `Options` 结尾的配置类型 MUST 声明在 `Microsoft.Extensions.Options` 命名空间。

#### Scenario: Primitives 的 Options 命名空间

- **WHEN** 通过反射读取 `Galosys.Foundation.Channel` 程序集中的 `ChannelServerOptions` 与 `ChannelKeepAliveOptions`
- **THEN** 两个类型的 `Namespace` 均为 `Microsoft.Extensions.Options`
- **AND** 程序集中不存在 `Galosys.Foundation.Channel.ChannelServerOptions` 或 `Galosys.Foundation.Channel.ChannelKeepAliveOptions`

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

#### Scenario: 核心类型文件目录

- **WHEN** 检查 `ChannelServer.cs`、`ChannelClient.cs`、`ChannelContext.cs`、`IMessageCodec.cs`、`IMessageFramer.cs`、`IChannelSession.cs`
- **THEN** 它们位于 `Galosys.Foundation.Channel` 项目的 `Galosys/Foundation/Channel/` 目录下

#### Scenario: DurableTask 核心类型文件目录

- **WHEN** 检查 `TaskHubClientExtensions.cs` 与 `ServiceProviderObjectCreator.cs`
- **THEN** 它们位于 `Galosys.Foundation.DurableTask.Core` 项目的 `Galosys/Foundation/DurableTask/Core/` 目录下

### Requirement: 核心类型位于 Galosys.Foundation.Channel 命名空间

`Galosys.Foundation.Channel` 类库的核心公共类型 MUST 声明在 `Galosys.Foundation.Channel` 命名空间。该命名空间 MUST 覆盖服务端、客户端、连接上下文、消息会话、分帧、编解码、管道中间件、协议分发与传输实现抽象。宿主生命周期适配器 MUST 声明在 `Microsoft.Extensions.Hosting`，DI 扩展 MUST 声明在 `Microsoft.Extensions.DependencyInjection`。

#### Scenario: 核心类型命名空间

- **WHEN** 通过反射读取 `Galosys.Foundation.Channel` 程序集
- **THEN** `ChannelServer`、`ChannelClient`、`ChannelContext`、`IChannelServer`、`IChannelClient`、`IChannelSession` 的 `Namespace` 均为 `Galosys.Foundation.Channel`
- **AND** `IMessageCodec`、`IMessageFramer`、`IMessageCodec` 的实现类、`ChannelPipeline`、`IChannelProtocol` 的 `Namespace` 均为 `Galosys.Foundation.Channel`
- **AND** 程序集中不存在 `Microsoft.Extensions.Hosting.Net` 命名空间下的任何类型

#### Scenario: 宿主适配器与 DI 扩展的命名空间

- **WHEN** 通过反射读取 `Galosys.Foundation.Channel` 程序集
- **THEN** `ChannelServerGroupHostedService` 的 `Namespace` 为 `Microsoft.Extensions.Hosting`
- **AND** `ChannelServerGroupServiceCollectionExtensions` 的 `Namespace` 为 `Microsoft.Extensions.DependencyInjection`

#### Scenario: 与 BCL Channel 无歧义

- **WHEN** 库的使用方同时引用 `Galosys.Foundation.Channel` 与 `System.Threading.Channels`
- **THEN** 两者可以共存于同一编译单元
- **AND** 程序集不导出名为 `Channel` 的公共类型

### Requirement: DurableTask 核心类型位于 Galosys.Foundation.DurableTask.Core 命名空间

`Galosys.Foundation.DurableTask.Core` 类库的核心公共类型 MUST 声明在 `Galosys.Foundation.DurableTask.Core` 命名空间。该库 MUST NOT 在上游 `Microsoft.Azure.Durabletask.Core` 包的 `DurableTask.Core` 命名空间下声明任何类型。宿主生命周期适配器 MUST 声明在 `Microsoft.Extensions.Hosting`，DI 扩展 MUST 声明在 `Microsoft.Extensions.DependencyInjection`。

#### Scenario: 核心类型命名空间

- **WHEN** 通过反射读取 `Galosys.Foundation.DurableTask.Core` 程序集
- **THEN** `TaskHubClientExtensions` 的 `Namespace` 为 `Galosys.Foundation.DurableTask.Core`
- **AND** 程序集中不存在 `DurableTask.Core` 命名空间下的任何类型

#### Scenario: 宿主适配器与 DI 扩展的命名空间

- **WHEN** 通过反射读取 `Galosys.Foundation.DurableTask.Core` 程序集
- **THEN** `DurableTaskHostedService` 的 `Namespace` 为 `Microsoft.Extensions.Hosting`
- **AND** `DurableTaskServiceCollectionExtensions` 的 `Namespace` 为 `Microsoft.Extensions.DependencyInjection`

#### Scenario: 上游包命名空间由包单独拥有

- **WHEN** 消费方同时引用 `Galosys.Foundation.DurableTask.Core` 与上游 `Microsoft.Azure.Durabletask.Core` 包
- **THEN** `using DurableTask.Core;` 仍能解析到上游包的 `TaskOrchestration<,>`、`TaskActivity<,>` 与 `TaskHubClient`
- **AND** 本库的类型不被该 `using` 意外引入，需显式 `using Galosys.Foundation.DurableTask.Core;` 才能使用
