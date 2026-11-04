# Spec Delta

## MODIFIED Requirements

### Requirement: 核心类型位于 Galosys.Foundation.Channel 命名空间

`Galosys.Foundation.Channel` 类库的核心公共类型 MUST 声明在 `Galosys.Foundation.Channel` 命名空间。该命名空间 MUST 覆盖服务端、客户端、连接上下文、消息会话、分帧、编解码、管道中间件、协议分发、传输实现抽象与构建器。该库 MUST NOT 在 `Microsoft.Extensions.DependencyInjection` 或 `Microsoft.Extensions.Hosting` 命名空间下声明任何类型。

#### Scenario: 核心类型命名空间

- **WHEN** 通过反射读取 `Galosys.Foundation.Channel` 程序集
- **THEN** `ChannelServer`、`ChannelClient`、`ChannelContext`、`IChannelServer`、`IChannelClient`、`IChannelSession` 的 `Namespace` 均为 `Galosys.Foundation.Channel`
- **AND** `IMessageCodec`、`IMessageFramer`、`IMessageCodec` 的实现类、`ChannelPipeline`、`IChannelProtocol` 的 `Namespace` 均为 `Galosys.Foundation.Channel`
- **AND** `ChannelServerBuilder`、`ChannelClientBuilder`、`ChannelClientPoolBuilder` 的 `Namespace` 均为 `Galosys.Foundation.Channel`
- **AND** 程序集中不存在 `Microsoft.Extensions.Hosting.Net` 命名空间下的任何类型

#### Scenario: 宿主适配器与 DI 扩展的命名空间

- **WHEN** 通过反射读取 `Galosys.Foundation.Channel` 程序集
- **THEN** 程序集中不存在 `Microsoft.Extensions.Hosting` 命名空间下的任何类型
- **AND** 程序集中不存在 `Microsoft.Extensions.DependencyInjection` 命名空间下的任何类型
- **AND** 程序集中不存在 `Galosys.Foundation.Channel.ChannelServerDefinition`

#### Scenario: 与 BCL Channel 无歧义

- **WHEN** 库的使用方同时引用 `Galosys.Foundation.Channel` 与 `System.Threading.Channels`
- **THEN** 两者可以共存于同一编译单元
- **AND** 程序集不导出名为 `Channel` 的公共类型
