# channel-pipeline Specification

## Purpose
定义 `ChannelServer` 组合式处理管线的组成、中间件顺序、终端处理器约束与异常收尾语义。
## Requirements
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

### Requirement: 中间件对传输的约束

The implementation SHALL satisfy this requirement. 中间件必须只包装或替换 `ChannelContext.Transport`，不得读取业务字节流；业务数据只能由终端处理器消费。中间件可以选择不调用 `next` 来短路连接，此时终端处理器不得被执行。

#### Scenario: 短路连接

- **WHEN** 中间件不调用 `next` 并直接返回
- **THEN** 终端处理器不被执行
- **AND** 框架仍完成连接收尾并触发 `ConnectionClosed`

#### Scenario: 中间件替换传输

- **WHEN** 中间件用包装后的 `IDuplexPipe` 替换 `Transport`
- **THEN** 处理器看到的 `Reader`/`Writer` 来自替换后的传输
- **AND** 原始传输仍由框架释放

### Requirement: 管线异常处理

The implementation SHALL satisfy this requirement. 管线中任意中间件或终端处理器抛出的异常必须导致当前连接中止，且不得传播到接受循环或宿主。异常必须可通过 `ChannelContext.Abort` 的原因或日志观察。

#### Scenario: 中间件抛异常

- **WHEN** 中间件在调用 `next` 前抛出异常
- **THEN** 连接被中止，`ConnectionClosed` 触发
- **AND** 服务器继续处理其他连接

#### Scenario: 终端处理器抛异常

- **WHEN** 终端处理器抛出异常
- **THEN** 该连接被中止
- **AND** 其他连接不受影响
