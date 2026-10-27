## ADDED Requirements

### Requirement: 组合式服务器入口

The implementation SHALL provide a public sealed `ChannelServer` that implements `IChannelServer` without requiring inheritance. The constructor MUST accept `ChannelServerOptions`, an `Action<IChannelPipeline>` pipeline configuration callback, and optional `ILoggerFactory` / `IServiceScopeFactory` dependencies. The options MUST be cloned so independent `ChannelServer` instances do not share mutable configuration.

#### Scenario: 组合式服务器启动并回显

- **WHEN** 使用 `ChannelServer` 绑定 loopback 端点，并通过 `pipeline => pipeline.Run(handler)` 配置回显处理器
- **THEN** `StartAsync` 返回后 `LocalEndPoint` 非 null
- **AND** 客户端连接后可以收到完整回显
- **AND** `StopAsync` 后不再接受新连接

#### Scenario: 两个实例使用独立配置

- **WHEN** 创建两个 `ChannelServer`，分别绑定不同的 loopback 端点并设置不同的 `ShutdownTimeout` / `MaxConnections`
- **THEN** 两个实例同时监听且 `LocalEndPoint` 不同
- **AND** 停止其中一个不会停止另一个
- **AND** 修改原始 `ChannelServerOptions` 不影响任一已构造实例

### Requirement: pipeline 配置与启动失败

`configurePipeline` MUST be invoked during `StartAsync` before the listener is bound. The callback MUST call `IChannelPipeline.Run`; when no terminal handler is configured, startup MUST fail with `InvalidOperationException` and `LocalEndPoint` MUST remain null. `ChannelServer` MUST preserve the `StartAsync` / `RunAsync` / `StopAsync` / `DisposeAsync` semantics defined for `IChannelServer`.

#### Scenario: 缺少 Run 时启动失败

- **WHEN** 使用一个不调用 `Run` 的 pipeline 配置回调启动 `ChannelServer`
- **THEN** `StartAsync` 抛出 `InvalidOperationException`
- **AND** `LocalEndPoint` 保持 null
- **AND** 不会留下监听 socket
