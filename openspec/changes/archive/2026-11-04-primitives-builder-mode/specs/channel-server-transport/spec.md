# Spec Delta

## MODIFIED Requirements

### Requirement: 组合式服务器入口

The implementation SHALL provide a public sealed `ChannelServer` that implements `IChannelServer` without requiring inheritance. The `ChannelServer` constructor MUST NOT be public; servers MUST be produced through the builder build path. The builder MUST accept `ChannelServerOptions`, a pipeline configuration composed from middleware and a terminal handler, and optional `ILoggerFactory` / `IServiceScopeFactory` dependencies. The options MUST be cloned so independent `ChannelServer` instances do not share mutable configuration.

#### Scenario: 组合式服务器启动并回显

- **WHEN** 通过构建器配置 loopback 端点与回显处理器，构建服务器并启动
- **THEN** `StartAsync` 返回后 `LocalEndPoint` 非 null
- **AND** 客户端连接后可以收到完整回显
- **AND** `StopAsync` 后不再接受新连接

#### Scenario: 两个实例使用独立配置

- **WHEN** 用两个构建器分别构建服务器，绑定不同的 loopback 端点并设置不同的 `ShutdownTimeout` / `MaxConnections`
- **THEN** 两个实例同时监听且 `LocalEndPoint` 不同
- **AND** 停止其中一个不会停止另一个
- **AND** 修改原始 `ChannelServerOptions` 不影响任一已构建实例

### Requirement: pipeline 配置与启动失败

The pipeline configuration MUST be applied during `StartAsync` before the listener is bound. A server built without a terminal handler MUST fail to start with `InvalidOperationException` and `LocalEndPoint` MUST remain null. `ChannelServer` MUST preserve the `StartAsync` / `RunAsync` / `StopAsync` / `DisposeAsync` semantics defined for `IChannelServer`.

#### Scenario: 缺少 Run 时启动失败

- **WHEN** 使用一个缺少终端处理器的构建结果启动 `ChannelServer`
- **THEN** `StartAsync` 抛出 `InvalidOperationException`
- **AND** `LocalEndPoint` 保持 null
- **AND** 不会留下监听 socket
