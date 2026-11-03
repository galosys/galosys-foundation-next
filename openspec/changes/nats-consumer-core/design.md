# Design

## Context

见 `proposal.md` — Why。本节只记录影响技术选型的现状与约束。

**可直接复用的成熟形态。** 仓库已依赖 `galosys.foundation.amqp` 26.9.16.1，其实现（经反编译确认）是本框架的现成蓝本：

| AMQP 版 | NATS 版现状 |
|---|---|
| `RabbitMQ.Client.AmqpHandlerAttribute(routingKey, broadcast)` | `NatsConsumerAttribute(subject, queueGroup)` —— 形状已对齐，命名空间不对 |
| `System.ServiceProviderExtensions.RegisterHandlerAsync` | 同一份代码抄过来，但订阅写死、派发留空 |
| `Microsoft.Extensions.Hosting.AmqpBootstrapper : IHostedLifecycleService` | `NatsConsumerBootstrapper.cs` 整个文件被注释掉 |
| `Microsoft.Extensions.Options.AmqpOptions`（含 `Handlers` 字典） | `NatsOptions` 是空 `internal class` |
| `AmqpServiceCollectionExtensions.AddAmqp(services, configuration)` | 不存在 |
| `Galosys.Foundation.Amqp.AmqpModule : IModule` | 不存在 |

关键结论：**NATS 版不需要发明新架构，只需把已抄来的半成品补齐**，并把 AMQP 特有的部分（RabbitMQ exchange/queue/DLX 语义）替换为 NATS 特有的部分（subject / queue group）。

**上游包 API 事实**（`nats.net` 3.3.1，`NATS.Client.Core`）：

```csharp
IAsyncEnumerable<NatsMsg<T>> SubscribeAsync<T>(
    string subject,
    string? queueGroup = null,
    INatsDeserialize<T>? serializer = null,
    NatsSubOpts? opts = null,
    CancellationToken cancellationToken = default);
```

`NatsMsg<T>` 是 `readonly record struct`，`Data` 为 `T?`。`INatsConnection` 来自 `NATS.Client.Core`；`AddNatsClient` 由 `NATS.Extensions.Microsoft.DependencyInjection` 提供，其默认 serializer registry 为 `NatsClientDefaultSerializerRegistry`（ad-hoc JSON）。

**仓库既有基础设施事实**（`galosys.foundation.core` 26.10.29.1）：

- `Microsoft.Extensions.Messaging.IMessageHandler<T>`：`Task<bool> HandleAsync(T msg)`；非泛型 `IMessageHandler.HandleAsync(IMessage msg)` 做类型分派。
- `Microsoft.Extensions.Messaging.MessageBase`：`Id`/`Key`/`OccurredOn`，供业务消息继承。
- `Galosys.Foundation.Core.IObjectSerializer`：`string Serialize<T>`、`object Deserialize(string value, Type returnType)`、`byte[] SerializeToUtf8Bytes<T>`。实现 `MicrosoftJsonSerializer` 使用 `System.Text.Json`，`PropertyNameCaseInsensitive = true`、`PropertyNamingPolicy = CamelCase`。由 `services.AddObjectSerializer()`（`AddCore()` 内调用）注册。
- `Microsoft.Extensions.DependencyModel.DependencyContextExtensions.GetImplementTypes()`：扫描运行时装配体中所有非抽象、非泛型的类；`DependencyContext.Default` 可能为 null。
- `System.Threading.Tasks.ConcurrentRunner.CreateTaskRunner(int workerCount)`：`IConcurrentRunner.Start(Func<CancellationToken, Task>)` / `StopAsync()`。
- `Microsoft.Extensions.DependencyInjection.IModule`：`void ConfigureServices(IServiceCollection, HostBuilderContext)` + 默认实现的 `ConfigureHost(IHostBuilder)`。**注意：接口在 `Microsoft.Extensions.DependencyInjection` 命名空间，不在 `Galosys.Foundation.Core`。**
- `UseModularization()` 通过 `DependencyContext.Default.DiscoverModules()` 自动发现 `IModule` 实现（含 `internal` 类），并逐个调用 `ConfigureServices`。因此把 `InternalModule` 放进 `Galosys.Foundation.Nats.Net` 程序集即可自动生效。

**命名空间寄生（有意为之，不是冲突）。** `NatsConsumerAttribute` 声明在 `namespace NATS.Net` —— 该命名空间确实由上游占用：`NATS.Client.Simplified` 3.3.1 声明了 `NATS.Net.NatsClient`、`NATS.Net.NatsClientDefaultSerializerRegistry`、`NATS.Net.NatsClientDefaultSerializer<T>`（`nats.net` 元包的 `NATS.Net.dll` 本身是空壳，无类型）。C# 允许跨程序集合并同一命名空间，`NatsClient` 与 `NatsConsumerAttribute` 类型名不同，因此**不构成冲突**。

这与 `galosys.foundation.amqp` 的既有约定完全一致：`AmqpHandlerAttribute` 声明在 `namespace RabbitMQ.Client`（上游客户端包命名空间），文件位于 `RabbitMQ.Client/AmqpHandlerAttribute.cs`。因此本库保持 `NATS.Net` 不变，并按“目录与命名空间一致”把文件移到 `NATS.Net/` 子目录。

## Goals / Non-Goals

**Goals:**

- 补齐半成品，让 `[NatsConsumer]` 特性真正生效：扫描 → 按特性订阅 → 反序列化 → 派发给 `IMessageHandler<T>`。
- 命名空间与物理目录符合 `foundation-namespace-conventions`；消费者标记按 AMQP 既有约定寄生在 `NATS.Net` 上游命名空间，文件放在 `NATS.Net/` 目录。
- 订阅生命周期随宿主启停，不留孤儿后台循环。
- 复用 `IObjectSerializer` 与 `IMessageHandler`，不发明新的消息模型或序列化契约。
- 保持与 `galosys.foundation.amqp` 心智模型一致，降低后续维护者的认知成本。

**Non-Goals:**

- 不做 NATS 发布端封装（`NatsTemplate` 之类）。发布侧直接用 `INatsConnection.PublishAsync` 即可，另开 change。
- 不做 JetStream（持久化、ack、重放、stream/consumer 管理）。JetStream 的 ack 语义会改变整个消费循环的形态，必须独立设计。
- 不实现 `IOutboxTransport`。仓库的事务性 outbox 需要 `MessageBus` 接线，属于另一条链路。
- 不新建 NATS 测试项目。仓库当前只有 `tests/Galosys.Foundation.Channel.Tests`，为 NATS 新建测试项目会引入"需要真实 NATS 服务器"的 CI 依赖。本 change 以编译验证 + 端到端手工验证为准；反序列化/派发逻辑刻意做成可单测的边界，留待后续 change 补测试。
- 不改 `galosys.foundation.core` 的任何公共契约。

## Decisions

### 1. 订阅时按 `byte[]` 收取，运行时再反序列化

`SubscribeAsync<T>` 是泛型方法，而 handler 的形参类型只能通过反射在运行时得知 —— 无法静态调用 `SubscribeAsync<SampleGreatEvent>()`。

**决策**：订阅时用 `SubscribeAsync<byte[]>` 收取原始负载，再用 `IObjectSerializer.Deserialize(string, Type)` 按运行时类型反序列化。

**为什么不用 `SubscribeAsync<string>` 再转**：字节路径避免一次多余的 UTF-8 解码往返，且不依赖负载必须是合法 UTF-8 文本。`IObjectSerializer` 只接受 `string`，因此实际实现是 `byte[]` → `Encoding.UTF8.GetString` → `Deserialize`。若未来需要二进制负载，此处是唯一的扩展点。

**为什么不用 NATS 自带的 `INatsDeserialize<T>`**：它同样要求编译期已知 `T`；且 `NatsClientDefaultSerializerRegistry` 的 JSON 行为与仓库既有的 `IObjectSerializer`（camelCase、大小写不敏感）不完全一致，会导致同一份 `MessageBase` 消息在 AMQP 与 NATS 两侧序列化结果不同。用 `IObjectSerializer` 保证跨传输一致。

**替代方案（否决）**：为每种消息类型生成强类型订阅代码 —— 需要额外的代码生成步骤，且违背"特性驱动"的初衷。

### 2. 派发通过非泛型 `IMessageHandler` 桥接

`IMessageHandler<T>.HandleAsync(T msg)` 是泛型方法，反射调用泛型方法需要 `MakeGenericMethod` + 参数装箱；而 `IMessageHandler.HandleAsync(IMessage msg)` 是非泛型接口方法，`MessageHandler<T>` 基类已实现类型分派（`CanHandle` 比较 `msg.GetType() == typeof(T)`）。

**决策**：构造 `IMessage` 实例（消息体即 handler 形参类型），交给非泛型 `IMessageHandler.HandleAsync(IMessage)`。

**注意**：`Microsoft.Extensions.Messaging.MessageHandler<T>` 基类会做严格类型检查（`msg.GetType() == typeof(T)`），而 `IMessageHandler<T>` 的直接实现不一定。若处理器直接实现 `IMessageHandler<T>`（示例中的 `SampleGreatHandler` 即是），非泛型 `HandleAsync(IMessage)` 需要由实现方自行提供；否则需要走 `MakeGenericMethod` 路径。

**决策细化**：优先走泛型反射路径 `MakeGenericMethod(messageType).Invoke(handler, [payload])`，因为它对"直接实现 `IMessageHandler<T>`"和"继承 `MessageHandler<T>`"两种写法都正确；`MakeGenericMethod` 每处理器方法只做一次并缓存 `MethodInfo`，不是每条消息的开销。非泛型接口路径作为处理器未实现非泛型接口时的回退。

**为什么每条消息新建作用域**：与 AMQP 版一致（`root.CreateScope()` → `GetRequiredService(mi.DeclaringType)`），保证 scoped 依赖（如 DbContext）在单条消息处理后被释放，不会跨消息泄漏。

### 3. 订阅循环由 `NatsBootstrapper` 持有，而非散落在扩展方法里

原型的做法是在 `RegisterHandlerAsync` 内部直接 `ConcurrentRunner.CreateTaskRunner(1).Start(...)`，启动后方法立刻返回，且 runner 没有被任何生命周期组件持有 —— 停机时无法取消。

**决策**：`NatsBootstrapper`（`IHostedLifecycleService`）持有 runner 集合与取消令牌源：

- `StartingAsync`：调用 `RegisterHandlers()` 取得订阅描述，为每个订阅建立一个 runner 并启动消费循环。
- `StoppingAsync`：取消令牌 → 对每个 runner 调用 `StopAsync()` → 等待循环退出。

`RegisterHandlers()` 返回订阅描述（subject / queue group / handler 方法）而非自行启动循环，由 bootstrapper 统一启动。这样"注册"与"运行"分离，职责清晰。

**`ConcurrentRunner` 的调用语义（已核对 `galosys.foundation.core` 实现）：**

`TaskConcurrentRunner.Start(Func<CancellationToken, Task>)` 会为每个 worker 建一个 `RunnerSlot`，
`RunnerSlot.RunAsync` 的循环体是：

```csharp
while (!_localCts.IsCancellationRequested)
{
    await _task(_localCts.Token).ConfigureAwait(false);
    Interlocked.Increment(ref _processedCount);
}
```

即 **task 会被反复调用，直到取消**。这带来两个必须遵守的约束：

1. **task 体内必须是"跑到底"的循环**。把 `await foreach` 放进 task 里，订阅正常结束时 task 会返回，
   slot 随即**再次调用 task → 再次订阅**，形成无限重订阅。正确做法是让 task 体内的 `await foreach`
   只在取消时结束（订阅被取消会抛 `OperationCanceledException`，被 `RunnerSlot` 捕获并置为 `Stopped`），
   或在 task 体内自行 `while` 循环直到令牌取消。
2. **task 体内抛出的非取消异常会使 slot 变为 `Faulted` 并停止**。这正是我们需要的：订阅循环自身的
   异常已由 `Decisions` 6 的派发器吸收，不会逃逸到 slot。

**`Concurrency` 与 runner 的关系**：`Concurrency = N` 时，**不**使用单个 `N` worker 的 runner
（那样 `await foreach` 只会跑在第一个 worker 上），而是为同一 subject 建立 **N 个独立的 runner**，
每个 runner 一个 worker，各自订阅同一 subject 与 queue group。NATS 的 queue group 语义保证这 N 个
消费者负载均衡；未指定 queue group 时则是 N 份广播消费（每个消费者各收一份）。

**为什么 `IHostedLifecycleService` 而非 `BackgroundService`**：AMQP 版用的是 `IHostedLifecycleService`，其 `StartingAsync` 在宿主 `StartAsync` 之前、`StartedAsync` 之后执行，订阅建立的时机早于普通 `BackgroundService.StartAsync`。与 AMQP 版保持一致。

**替代方案（否决）**：把订阅循环放进 `BackgroundService.ExecuteAsync` —— 时机稍晚，且与仓库其它 messaging 模块的形态不一致。

### 4. `NatsOptions` 只承载"我们自己的"关注点

NATS 连接参数（Url、认证、TLS）已由上游 `NatsOpts` 经 `AddNatsClient` 管理，**不应**在 `NatsOptions` 里重复一份 —— 两份真相会导致配置漂移。

**决策**：`NatsOptions` 只包含：

| 成员 | 类型 | 默认 | 含义 |
|---|---|---|---|
| `Concurrency` | `int` | `1` | 每个 subject 的并发派发数 |
| `MaxRetryCount` | `int` | `0` | handler 抛出异常后的重试次数 |
| `RetryBaseDelayMs` | `int` | `100` | 重试基础延迟（毫秒） |
| `Handlers` | `IDictionary<string, NatsHandlerProperty>` | 空 | 按 subject 覆盖上述设置 |

`NatsHandlerProperty` 形状对齐 AMQP 版的 `AmqpHandlerProperty`（`Concurrency` / `MaxRetryCount` / `RetryBaseDelayMs`），但不含 AMQP 特有的 `DurableQueue` / `EnableDlx` / `QueueName`。

**为什么 `NatsOptions` 必须 `public`**：`IOptionsMonitor<NatsOptions>` 与 `services.Configure<NatsOptions>(...)` 的绑定发生在**调用方**程序集里。`internal` 类型无法作为公共 API 的泛型参数被外部代码引用，会导致 `AddNats` 无法作为公共扩展方法暴露。这是把 `internal` 改为 `public` 的唯一原因。

**配置节命名**：`AddNats(services, configuration)` 绑定 `configuration.GetSection("Nats")`。与 AMQP 版的 `GetSection("RabbitMQ")` 形态一致，遵循 .NET 配置节 PascalCase 习惯。`IConfiguration` 的键比较不区分大小写，因此现有写作 `NATS` 或 `nats` 的配置源同样能绑上，不构成破坏性变更。连接参数（`Url`/`Username`/`Password`）与消费者选项同放 `Nats` 一个节，由 `Program.cs` 读出后交给上游 `AddNatsClient`，`NatsOptions` 绑定时会忽略这三个未知键。

### 5. 模块化启动下的自动接线

`UseModularization()` 通过 `DependencyContext.Default.DiscoverModules()` 发现 `IModule` 实现并调用 `ConfigureServices`。因此在 `Galosys.Foundation.Nats.Net` 程序集中放一个 `internal class InternalModule : IModule`，其 `ConfigureServices` 调用 `services.AddNats(context.Configuration)`，即可让模块化宿主自动获得消费者能力 —— 与 `galosys.foundation.amqp` 的 `AmqpModule` 做法一致。

**决策**：模块类型放在 `Galosys/Foundation/Nats/Net/InternalModule.cs`，命名空间 `Galosys.Foundation.Nats.Net`。

**风险**：`DiscoverModules()` 会扫描所有运行时装配体。`Galosys.Foundation.Nats.Net` 是**库**而非应用，把它作为 `IModule` 放进库意味着任何引用它的应用都会自动注册 NATS 消费者生命周期。若应用并未配置 NATS 连接，`AddNatsClient` 未被调用时 `INatsConnection` 无法解析 —— 此时 bootstrapper 必须在解析失败时给出清晰错误而非静默失败。

**缓解**：`AddNats` 内部用 `TryAddEnumerable` 语义保证幂等；bootstrapper 在 `StartingAsync` 解析 `INatsConnection` 失败时抛出带明确信息的异常，说明"未注册 NATS 客户端"。

### 6. 反序列化失败与 handler 异常的处理边界

**决策**：
- **反序列化失败**：记录日志（错误级别，含 subject 与目标类型），跳过该条消息，订阅继续。理由：一条坏消息不应导致整个订阅失效。
- **handler 抛出异常**：记录日志，按 `MaxRetryCount` 重试（指数退避：`RetryBaseDelayMs * 2^attempt`），耗尽后记录并放弃该消息。理由：与 AMQP 版的"记日志后 rethrow"不同 —— NATS Core 是 fire-and-forget，没有 broker 侧的 ack/requeue 兜底，异常若逃逸出消费循环会终止整个 `await foreach`，导致该 subject 后续所有消息永久丢失。

**为什么不让异常逃逸**：`await foreach` 体内抛出的异常会终止枚举。Core NATS 没有 redelivery 机制，订阅一旦终止就等于该 subject 静默死亡。这是与 AMQP（broker 侧 requeue）最本质的差异，必须在此处吸收。

### 7. 去重状态为进程级静态

沿用 AMQP 版的 `_handlersRegistered`（`ConcurrentDictionary<MethodInfo, DateTime>`）作为进程内已注册方法集合。

**决策**：保持进程级静态，但 `TryAdd` 只在订阅**成功建立后**调用（原型当前在 `try` 块外无条件标记，失败也会被标记为已注册，导致后续重试被跳过 —— 这是原型的实际缺陷）。

**已知局限**：以 `MethodInfo` 为 key，若同一处理器方法被标注多个 subject，第一个 subject 建立成功即标记该方法，第二个 subject 会被误跳过。

**修正**：去重 key 改为 `(MethodInfo, Subject, QueueGroup)` 三元组，用 `ConcurrentDictionary<(MethodInfo, string, string?), DateTime>`。这样"同一方法多 subject"能正确工作，且重复扫描同一 (方法, subject, queueGroup) 组合仍被拦住。

### 8. 命名空间与物理目录

| 类型 | 命名空间 | 文件位置 |
|---|---|---|
| `NatsConsumerAttribute` | `NATS.Net`（上游客户端命名空间） | `NATS.Net/NatsConsumerAttribute.cs` |
| `NatsBootstrapper` | `Microsoft.Extensions.Hosting` | `Microsoft/Extensions/Hosting/NatsBootstrapper.cs` |
| `NatsServiceCollectionExtensions` | `Microsoft.Extensions.DependencyInjection` | `Microsoft/Extensions/DependencyInjection/NatsServiceCollectionExtensions.cs` |
| `ServiceProviderExtensions` | `Microsoft.Extensions.DependencyInjection` | 保持现路径 |
| `NatsOptions` / `NatsHandlerProperty` | `Microsoft.Extensions.Options` | 保持现路径 |
| `NatsSubscription` / `NatsConsumerDispatcher` | `Galosys.Foundation.Nats.Net`（internal） | `Galosys/Foundation/Nats/Net/` |
| `InternalModule` | `Galosys.Foundation.Nats.Net` | `Galosys/Foundation/Nats/Net/InternalModule.cs` |

`NatsConsumerAttribute` **不迁移命名空间**：`NATS.Net` 是它与上游 `NatsClient` 共享的命名空间，与 `AmqpHandlerAttribute` 位于 `RabbitMQ.Client` 完全同构。本 change 只把它从项目根移到 `NATS.Net/` 目录，使“目录与命名空间一致”，公共 API 不变，**无 BREAKING 变更**。

## Risks / Trade-offs

- **库内含 `IModule` 导致自动接线过于隐式** → 引用该库的宿主会自动注册 NATS 消费者生命周期；未配置 NATS 连接时以明确的异常提示，而非静默失败。README 明确说明。
- **`RegisterHandlers` 必须为 `internal`** → 返回的 `NatsSubscription` 是 internal 类型，若 `RegisterHandlers` 为 public 会触发 CS0050（不一致的可访问性）。`ServiceProviderExtensions` 本身也是 `internal static class`，与原型一致。
- **Core NATS 无 redelivery，异常即丢消息** → handler 异常在框架内被吸收并重试，耗尽后记录日志；不依赖 broker 兜底。此为与 AMQP 语义的本质差异，已在 `Decisions` 6 说明。
- **反射派发带来每消息开销** → `MakeGenericMethod` 的结果按 handler 方法缓存，不是每条消息计算；派发本身是同步方法调用，开销可忽略。
- **每条消息创建 DI scope** → 与 AMQP 版一致；scoped 依赖的构造开销是刻意付出的隔离成本。若 handler 无状态且追求极致吞吐，可在后续 change 提供 scope 复用开关。
- **`IObjectSerializer` 走 string 中转** → `byte[]` → UTF-8 string → JSON 反序列化，多一次拷贝；对大负载有成本。当前示例负载极小，且与仓库跨传输一致性要求相比，此代价可接受。已记录为二进制负载的扩展点。
- **无自动化测试** → 本 change 不建 NATS 测试项目（需真实 NATS 服务器）。反序列化与派发被设计为可单测边界（`IObjectSerializer` 与 `IMessageHandler` 均可替换为测试替身），后续 change 补测试项目。
- **`DependencyContext.Default` 可能为 null** → 原型直接 `.GetImplementTypes()` 会 NRE。加判空，null 时记日志并跳过扫描。
- **消费者标记与上游共享 `NATS.Net` 命名空间** → `using NATS.Net;` 会同时引入 `NatsClient` 与本框架的 `NatsConsumerAttribute`。类型名不冲突，且与 `AmqpHandlerAttribute`/`RabbitMQ.Client` 同构；风险是上游未来若新增同名特性会产生歧义，届时改名即可（当前无此迹象）。
- **文档纠偏可能与历史归档冲突** → 只改根 `README.md`、`AGENTS.md` 与 `openspec/specs/repository-layout/spec.md`；`openspec/changes/archive/**` 按仓库约定不改写。

## Migration Plan

1. 把 `NatsConsumerAttribute.cs` 从项目根移到 `NATS.Net/` 目录并补 XML 文档（命名空间 `NATS.Net` 不变）；建立 `Galosys/Foundation/Nats/Net/` 目录供 `NatsSubscription` / `NatsConsumerDispatcher` / `InternalModule` 使用。
2. 实现 `NatsOptions` 与 `NatsHandlerProperty`（`public`，位于 `Microsoft.Extensions.Options`）。
3. 实现扫描与订阅核心：`ServiceProviderExtensions.RegisterHandlerAsync` —— 按特性订阅、`byte[]` 收取、`IObjectSerializer` 反序列化、泛型反射派发、异常吸收与重试、三元组去重。
4. 实现 `NatsBootstrapper`（`IHostedLifecycleService`），持有 runner 与取消令牌源，负责订阅的建立与停机取消。
5. 实现 `NatsServiceCollectionExtensions.AddNats` 与 `InternalModule`。
6. 改造示例：启用特性、实现 `HandleAsync`、删除手写 bootstrapper、生产者改发类型化消息、连接参数迁到 `appsettings.json` 的 `Nats` 节。
7. 修正根 `README.md` 与 `AGENTS.md` 的"不可编译"表述；新增 `src/Galosys.Foundation.Nats.Net/README.md`。
8. 构建验证 + 端到端验证 + `openspec validate`。

**回滚策略**：本 change 不改变任何既有公共契约（`NatsConsumerAttribute` 的命名空间与签名均保持 `NATS.Net`），只是补齐未实现的功能。回滚即还原相关文件；但由于原型状态下该库功能不可用，回滚价值有限。

## Open Questions

无。发布端封装、JetStream 与 outbox transport 三项均明确排除在本 change 外，各自作为独立 change 推进；它们不影响本 change 的规格、方案与任务拆分。
