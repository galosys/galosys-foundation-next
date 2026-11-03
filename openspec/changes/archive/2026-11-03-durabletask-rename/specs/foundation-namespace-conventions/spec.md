# Spec Delta

## MODIFIED Requirements

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

## ADDED Requirements

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
