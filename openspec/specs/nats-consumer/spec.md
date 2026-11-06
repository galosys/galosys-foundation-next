# nats-consumer Specification

## Purpose
为 Core NATS 提供特性驱动的消费者框架，使业务处理器只需在 `IMessageHandler<T>` 实现类的方法上标注 subject 与 queue group，即被自动扫描、订阅、反序列化并派发。
## Requirements
### Requirement: 消费者标记声明订阅意图

系统 SHALL 提供一个可用于标注消息处理方法的消费者标记类型，该类型 MUST 声明 subject 与可选的 queue group，并 MUST 位于 `NATS.Net` 命名空间（与上游 NATS 客户端包共享，对齐 `AmqpHandlerAttribute` 位于 `RabbitMQ.Client` 的既有约定）。该标记 MUST 支持重复标注在同一方法上以声明多个订阅。

#### Scenario: 标记携带 subject 与 queue group

- **WHEN** 在一个 `IMessageHandler<T>` 实现的方法上标注 subject `"orders.created"` 与 queue group `"billing"`
- **THEN** 该标记的 subject 为 `"orders.created"`
- **AND** 该标记的 queue group 为 `"billing"`

#### Scenario: 未指定 queue group 时为空

- **WHEN** 标注消费者标记时只提供 subject
- **THEN** 该标记的 queue group 为 null

#### Scenario: 标记与上游类型共享命名空间

- **WHEN** 使用方同时引用本框架与上游 NATS 客户端包，并 `using NATS.Net;`
- **THEN** 消费者标记的命名空间为 `NATS.Net`
- **AND** 可同时使用上游的 `NatsClient` 与框架的消费者标记，两者类型名不冲突
- **AND** 与 `AmqpHandlerAttribute` 声明在 `RabbitMQ.Client` 的约定一致

### Requirement: 扫描发现被标记的处理器

系统 SHALL 在宿主启动阶段扫描程序集上下文中的实现类型，查找标注了消费者标记的方法，并 MUST 将每个被标记的方法视为一个独立的订阅声明。同一处理器类型上的多个标记 MUST 各自产生独立订阅。已被成功注册的处理器方法 MUST NOT 在同一进程内被重复订阅。

#### Scenario: 发现被标记的方法

- **WHEN** 程序集中存在一个 `IMessageHandler<T>` 实现，其方法标注了消费者标记
- **THEN** 该方法被识别为一个订阅声明

#### Scenario: 同一类型上的多个标记各自订阅

- **WHEN** 一个处理器方法上标注了两个不同的 subject
- **THEN** 两个 subject 各自建立独立订阅

#### Scenario: 重复调用不重复订阅

- **WHEN** 扫描流程被触发两次
- **THEN** 第二次不产生新的订阅

#### Scenario: 忽略未被标记的方法

- **WHEN** 程序集中存在未标注消费者标记的消息处理方法
- **THEN** 该方法不产生订阅

### Requirement: 按标记订阅并路由到处理器

系统 SHALL 使用消费者标记上声明的 subject 与 queue group 建立订阅，MUST NOT 使用任何硬编码的 subject 或 queue group。收到消息后，系统 MUST 将负载交给与该订阅对应的处理器方法，并 MUST 以该方法声明的形参类型作为负载的目标类型。

#### Scenario: 使用标记上的 subject 订阅

- **WHEN** 处理器方法标注 subject `"orders.created"`
- **THEN** 系统订阅 `"orders.created"`
- **AND** 不订阅任何其它 subject

#### Scenario: 使用标记上的 queue group 订阅

- **WHEN** 处理器方法标注 queue group `"billing"`
- **THEN** 系统以 `"billing"` 作为队列组建立订阅

#### Scenario: 消息抵达时调用对应处理器

- **WHEN** 一条消息投递到已建立的订阅
- **THEN** 对应的处理器方法被调用一次
- **AND** 处理器收到反序列化后的负载

#### Scenario: 处理器失败不影响其它订阅

- **WHEN** 一个处理器方法在处理消息时抛出异常
- **THEN** 该异常被记录
- **AND** 订阅保持存活并继续接收后续消息

### Requirement: 负载反序列化为处理器形参类型

系统 SHALL 将收到的消息负载反序列化为处理器方法的第一个形参所声明的类型，并 MUST 使用可注入的对象序列化器完成该转换。反序列化失败 MUST 被记录且 MUST NOT 导致订阅终止。

#### Scenario: 反序列化为形参类型

- **WHEN** 处理器方法形参类型为消息实体类，负载为该实体的 JSON 表示
- **THEN** 处理器收到的实例类型为该消息实体类
- **AND** 其属性值与负载一致

#### Scenario: 反序列化失败被记录

- **WHEN** 负载内容无法反序列化为处理器形参类型
- **THEN** 失败被记录
- **AND** 订阅继续接收后续消息

### Requirement: 处理器实例按消息解析

系统 SHALL 为每条被接收的消息创建独立的服务作用域，并在该作用域内解析处理器实例，以避免跨消息共享有状态或被释放的依赖。处理器类型 MUST 已被注册到依赖注入容器中。

#### Scenario: 每条消息使用独立作用域

- **WHEN** 订阅连续收到两条消息
- **THEN** 两条消息各自在独立的服务作用域内解析处理器实例

#### Scenario: 处理器类型未注册时报错

- **WHEN** 处理器类型未注册到依赖注入容器
- **THEN** 处理该条消息的过程失败并抛出异常
- **AND** 异常信息标识出该处理器类型

**说明：** 处理器实例是**按消息**解析的，而非在订阅建立时一次性解析。因此“未注册”这类错误
发生在首条消息到达时，不会阻止宿主启动；异常会被记录且不影响订阅存活。

### Requirement: 消费者配置项

系统 SHALL 提供一个可从配置绑定的选项类型，用于控制消费者的并发派发数、重试次数与重试基础延迟，并 SHALL 支持按 subject 覆盖这些设置。该选项类型 MUST 为公共可见，且 MUST 位于 `Microsoft.Extensions.Options` 命名空间。未显式配置时，并发派发数 MUST 为 1，重试次数 MUST 为 0。

#### Scenario: 默认配置

- **WHEN** 未显式配置消费者选项
- **THEN** 并发派发数为 1
- **AND** 重试次数为 0

#### Scenario: 按 subject 覆盖配置

- **WHEN** 配置中为 subject `"orders.created"` 指定了不同于全局的并发派发数
- **THEN** 该 subject 的订阅使用覆盖后的并发派发数
- **AND** 其它 subject 仍使用全局值

#### Scenario: 选项可从配置节绑定

- **WHEN** 宿主提供名为 `Nats` 的配置节
- **THEN** 该配置节的内容被绑定到消费者选项

#### Scenario: 配置节名大小写不敏感

- **WHEN** 配置源中写作 `NATS` 或 `nats`
- **THEN** 绑定到消费者选项的结果与写作 `Nats` 相同

### Requirement: 订阅在宿主生命周期内建立与停止

系统 SHALL 在宿主启动阶段建立订阅，并 MUST 在宿主停机时取消所有订阅循环且不再投递消息。订阅建立失败 MUST 使宿主启动失败并暴露错误原因。系统 MUST NOT 在宿主停机后遗留仍在运行的后台消费循环。

#### Scenario: 宿主启动后订阅生效

- **WHEN** 宿主完成启动
- **THEN** 所有被标记的订阅均已建立

#### Scenario: 宿主停机时取消订阅

- **WHEN** 宿主收到停机信号
- **THEN** 所有订阅循环被取消
- **AND** 不再有消息被派发给处理器

#### Scenario: 订阅建立失败使启动失败

- **WHEN** 某个订阅在建立阶段抛出异常
- **THEN** 宿主启动失败
- **AND** 错误原因包含失败的 subject

#### Scenario: 未注册 NATS 客户端时报错

- **WHEN** 宿主未注册 `INatsConnection`（未调用 `AddNatsClient`）却启用了消费者功能
- **THEN** 宿主启动失败
- **AND** 错误信息明确指出未注册 NATS 客户端

### Requirement: 提供依赖注入注册入口

系统 SHALL 提供一个把消费者选项绑定到配置节并注册消费者生命周期的服务集合扩展方法。该方法 MUST 可重复调用而不产生重复注册。系统 SHALL 同时提供一个模块类型，使启用模块化启动的宿主无需手工调用扩展方法即可获得消费者功能。

#### Scenario: 注册扩展方法绑定配置并注册生命周期

- **WHEN** 调用消费者注册扩展方法并传入配置
- **THEN** 消费者选项被绑定到命名配置节
- **AND** 消费者生命周期被注册到服务集合

#### Scenario: 模块化宿主自动接线

- **WHEN** 宿主启用模块化启动且未手工调用注册扩展方法
- **THEN** 消费者功能自动生效

#### Scenario: 重复调用不产生重复注册

- **WHEN** 注册扩展方法被调用两次
- **THEN** 服务集合中消费者生命周期只有一份注册
