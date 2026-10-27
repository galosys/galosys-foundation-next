## ADDED Requirements

### Requirement: 唯一公开服务器实现

The implementation SHALL expose sealed `ChannelServer` as the only public server implementation. `ChannelServerBase` MUST be removed from the assembly's public API, and `ChannelServerCore` MUST remain internal. All lifecycle requirements in `channel-server-transport` MUST apply to `ChannelServer`.

#### Scenario: 程序集不再暴露 ChannelServerBase

- **WHEN** 通过反射检查 `Galosys.Foundation.Primitives` 程序集中的 `Microsoft.Extensions.Hosting.Net.ChannelServerBase`
- **THEN** 该类型不存在
- **AND** `ChannelServer` 是 public sealed 类型并实现 `IChannelServer`
- **AND** `ChannelServerCore` 不是 public 类型

#### Scenario: 组合式服务器继续满足生命周期

- **WHEN** 使用 `ChannelServer` 绑定 loopback 端点并启动
- **THEN** 绑定成功后才返回，`LocalEndPoint` 非 null
- **AND** 停止后不再接受新连接
- **AND** 重复 `StopAsync` / `DisposeAsync` 不抛异常

## MODIFIED Requirements

### Requirement: 连接关闭与作用域释放顺序

The implementation SHALL satisfy this requirement. 当 `ChannelServer` 构造时传入 `IServiceScopeFactory` 时，每个连接必须拥有独立的 `IServiceScope`，通过 `ChannelContext.Services` 暴露。未传入工厂的手工构造场景使用共享空作用域（无 DI），仅用于测试或无容器宿主。连接关闭时，`ConnectionClosed` 令牌必须在作用域释放之前取消，且恰好取消一次；通过 `Register` 注册的回调不保证在作用域释放前全部执行完毕。处理器返回、连接被对端重置、`Abort` 调用三种情况都必须遵守该顺序。

#### Scenario: 处理器返回后关闭

- **WHEN** 处理器正常返回
- **THEN** `ConnectionClosed` 触发一次
- **AND** 该连接的作用域随后被释放

#### Scenario: 对端重置连接

- **WHEN** 客户端在处理器读取期间强制关闭 socket
- **THEN** 处理器观察到读取完成或异常
- **AND** `ConnectionClosed` 恰好触发一次，作用域被释放

#### Scenario: 空闲处理器观察到对端重置

- **WHEN** 处理器没有读取数据，只在等待 `ConnectionClosed`，客户端强制重置连接
- **THEN** `ConnectionClosed` 触发，处理器被唤醒
- **AND** 作用域随后被释放

#### Scenario: 客户端半关闭不触发 ConnectionClosed

- **WHEN** 客户端只关闭发送方向（FIN），服务器仍可写入
- **THEN** 处理器通过 `Reader` 观察到输入结束
- **AND** `ConnectionClosed` 不在半关闭时提前触发，处理器仍可发送响应

#### Scenario: 主动中止连接

- **WHEN** 处理器调用 `ChannelContext.Abort`
- **THEN** 连接立即终止，`ConnectionClosed` 恰好触发一次
- **AND** 处理器返回后不再向该连接写入数据
