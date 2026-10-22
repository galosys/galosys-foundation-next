# channel-pipeline Specification

## Purpose
TBD - created by archiving change primitives-socket-p1-1. Update Purpose after archive.
## Requirements
### Requirement: 管线组成与执行顺序

The implementation SHALL satisfy this requirement. `ChannelServerBase.Configure` 必须提供可组合的处理管线。默认实现必须为 `pipeline.Run(ChannelHandleAsync)`。`IChannelPipeline.Use` 添加的中间件必须按注册顺序由外向内执行，终端处理器必须恰好执行一次。`Run` 只能被调用一次，重复调用必须抛出 `InvalidOperationException`。

 默认管线执行处理器

- **WHEN** 子类不重写 `Configure`
- **THEN** 每个连接直接调用 `ChannelHandleAsync`
- **AND** 处理器收到带有效 `Transport` 的上下文

#### Scenario: 中间件按注册顺序包裹

- **WHEN** 子类在 `Configure` 中依次 `Use(A)`、`Use(B)`、`Run(handler)`
- **THEN** 执行顺序为 `A 进入 → B 进入 → handler → B 退出 → A 退出`

#### Scenario: 重复 Run

- **WHEN** `Configure` 中对同一管线调用两次 `Run`
- **THEN** 第二次调用抛出 `InvalidOperationException`

### Requirement: 中间件对传输的约束

The implementation SHALL satisfy this requirement. 中间件必须只包装或替换 `ChannelContext.Transport`，不得读取业务字节流；业务数据只能由终端处理器消费。中间件可以选择不调用 `next` 来短路连接，此时终端处理器不得被执行。

 短路连接

- **WHEN** 中间件不调用 `next` 并直接返回
- **THEN** 终端处理器不被执行
- **AND** 框架仍完成连接收尾并触发 `ConnectionClosed`

#### Scenario: 中间件替换传输

- **WHEN** 中间件用包装后的 `IDuplexPipe` 替换 `Transport`
- **THEN** 处理器看到的 `Reader`/`Writer` 来自替换后的传输
- **AND** 原始传输仍由框架释放

### Requirement: 管线异常处理

The implementation SHALL satisfy this requirement. 管线中任意中间件或终端处理器抛出的异常必须导致当前连接中止，且不得传播到接受循环或宿主。异常必须可通过 `ChannelContext.Abort` 的原因或日志观察。

 中间件抛异常

- **WHEN** 中间件在调用 `next` 前抛出异常
- **THEN** 连接被中止，`ConnectionClosed` 触发
- **AND** 服务器继续处理其他连接

#### Scenario: 终端处理器抛异常

- **WHEN** 终端处理器抛出异常
- **THEN** 该连接被中止
- **AND** 其他连接不受影响

