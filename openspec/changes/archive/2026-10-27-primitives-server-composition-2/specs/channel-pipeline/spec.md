## MODIFIED Requirements

### Requirement: 管线组成与执行顺序

The implementation SHALL satisfy this requirement. `ChannelServer` 构造函数的 `Action<IChannelPipeline>` 必须提供可组合的处理管线。调用方必须在该回调中调用 `IChannelPipeline.Run` 设置终端处理器；未调用时启动必须失败。`IChannelPipeline.Use` 添加的中间件必须按注册顺序由外向内执行，终端处理器必须恰好执行一次。`Run` 只能被调用一次，重复调用必须抛出 `InvalidOperationException`。

#### Scenario: 组合式管线执行处理器

- **WHEN** 使用 `pipeline => pipeline.Run(handler)` 配置 `ChannelServer`
- **THEN** 每个连接调用该终端处理器
- **AND** 处理器收到带有效 `Transport` 的上下文

#### Scenario: 中间件按注册顺序包裹

- **WHEN** 在配置回调中依次 `Use(A)`、`Use(B)`、`Run(handler)`
- **THEN** 执行顺序为 `A 进入 → B 进入 → handler → B 退出 → A 退出`

#### Scenario: 重复 Run

- **WHEN** 在配置回调中对同一管线调用两次 `Run`
- **THEN** 第二次调用抛出 `InvalidOperationException`

#### Scenario: 缺少 Run

- **WHEN** 配置回调只调用 `Use` 而没有调用 `Run`
- **THEN** `ChannelServer.StartAsync` 抛出 `InvalidOperationException`
- **AND** `LocalEndPoint` 保持 null
