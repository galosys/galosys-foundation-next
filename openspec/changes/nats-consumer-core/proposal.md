# Proposal

## Why

`src/Galosys.Foundation.Nats.Net` 只是一个从 AMQP 版抄了一半的原型：订阅 subject 与 queue group 硬编码为 `"sample.greet"` / `"nats-sample-console-app"`，`[NatsConsumer]` 特性的 `Subject`/`QueueGroup` 形同虚设；收到消息后只执行 `await Task.CompletedTask`，**没有任何 handler 被调用**；`NatsOptions` 是 `internal` 空类；`NatsConsumerBootstrapper.cs` 整个文件被注释掉且命名空间写成 `NatsSample.ConsoleApp.Infrastructure`；`NatsConsumerAttribute` 缺 XML 文档。

结果是这层封装目前**没有任何生产价值**：使用者必须绕过它、自己写 `SubscribeAsync` 才能收消息，而仓库里唯一想用它的示例 `samples/NatsSample.ConsoleApp` 里 `[NatsConsumer]` 特性被注释掉、`HandleAsync` 直接抛 `NotImplementedException`。同时根 `README.md` 与 `AGENTS.md` 都声称该类库"当前不可编译"，与事实不符（实测可编译，0 error）。

本 change 把这个原型补成**可用的 Core NATS 消费者框架**：特性驱动扫描 → 按 subject 订阅 → 反序列化 → 派发给 `IMessageHandler<T>`，与仓库内 `galosys.foundation.amqp` 的成熟形态对齐，让 NATS 消费方也能享受和 AMQP 一致的书写体验。

## What Changes

- **`NatsConsumerAttribute` 保留在 `NATS.Net` 命名空间**，与 `galosys.foundation.amqp` 的 `AmqpHandlerAttribute` 声明在 `RabbitMQ.Client`（上游客户端包命名空间）遵循同一约定；仅补齐 XML 文档注释消除 CS1591 警告，`Subject`/`QueueGroup` 语义与命名空间均不变。文件按“目录与命名空间一致”移到 `NATS.Net/` 子目录。
- **`NatsOptions` 升级为 `public` 的真实配置类型**（位于 `Microsoft.Extensions.Options`）：承载 `Concurrency`（并发派发数）、`MaxRetryCount`、`RetryBaseDelayMs`、`Handlers`（按 subject 覆盖的 per-subject 配置），对齐 `AmqpOptions` 的形态。
- **`ServiceProviderExtensions.RegisterHandlerAsync` 完成实现**：扫描 `[NatsConsumer]` 标记的方法，使用**特性上的** `Subject`/`QueueGroup` 建立订阅；订阅时按 `byte[]` 收取原始负载，再用 `IObjectSerializer` 反序列化为 handler 形参类型；每条消息新建 `IServiceScope` 解析 handler 实例并调用 `IMessageHandler.HandleAsync`；失败路径写入 `ILogger` 而非静默吞掉。
- **新增 `Microsoft.Extensions.Hosting.NatsBootstrapper`**（`IHostedLifecycleService`）：在 `StartingAsync` 阶段触发 handler 扫描与订阅建立，并持有订阅循环的生命周期，随宿主停机而取消。
- **新增 `NatsServiceCollectionExtensions.AddNats(IServiceCollection, IConfiguration)`**：绑定 `Nats` 配置节到 `NatsOptions` 并注册 bootstrapper；新增 `Galosys.Foundation.Nats.Net.InternalModule : IModule` 作为 `UseModularization()` 下的默认接线入口。
- **示例接线**：`samples/NatsSample.ConsoleApp` 启用 `[NatsConsumer]` 特性、实现 `HandleAsync`、删除重复的手写 `NatsBootstrapper`（框架 bootstrapper 接管订阅）、生产者改为发布类型化消息以验证序列化路径；NATS 连接参数从代码硬编码迁移到 `appsettings.json`。
- **文档纠偏**：根 `README.md`、`AGENTS.md` 与 `openspec/specs/repository-layout/spec.md` 中"`Galosys.Foundation.Nats.Net` 当前不可编译"的说法与事实不符，改为准确描述。
- **明确不在本 change 内**：不提供 NATS 发布端封装（`NatsTemplate` 之类）、不支持 JetStream、不实现 `IOutboxTransport`。这三项各自价值独立、依赖不同，作为后续独立 change 推进。

## Capabilities

### New Capabilities

- `nats-consumer`: 为 Core NATS 提供特性驱动的消费者框架：`[NatsConsumer]` 标记与扫描规则、subject/queue group 订阅语义、负载反序列化与 `IMessageHandler` 派发、并发与重试配置、宿主生命周期内的订阅建立与停机。

### Modified Capabilities

- `foundation-namespace-conventions`: `NatsOptions` 由 `internal` 空类改为 `public` 真实配置类型；明确 `NatsConsumerAttribute` 寄生在 `NATS.Net`（上游客户端命名空间）是**有意为之**的例外，与 `AmqpHandlerAttribute` 寄生在 `RabbitMQ.Client` 一致，并记录其物理目录为 `NATS.Net/`。
- `repository-layout`: 修正"`NatsSample.ConsoleApp` 是已知不可编译项目"的错误断言，改为记录该示例依赖本地 NATS 服务器方能产生端到端消息。

## Impact

- 代码：
  - 移动并补写 `src/Galosys.Foundation.Nats.Net/NatsConsumerAttribute.cs` → `src/Galosys.Foundation.Nats.Net/NATS.Net/NatsConsumerAttribute.cs`（命名空间不变，补 XML 文档）。
  - 重写 `src/Galosys.Foundation.Nats.Net/Microsoft/Extensions/Options/NatsOptions.cs`（`public` 真实选项）。
  - 重写 `src/Galosys.Foundation.Nats.Net/Microsoft/Extensions/DependencyInjection/ServiceProviderExtensions.cs`（完成扫描/订阅/反序列化/派发）。
  - 重写 `src/Galosys.Foundation.Nats.Net/NatsConsumerBootstrapper.cs` → `Microsoft.Extensions.Hosting.NatsBootstrapper`。
  - 新增 `src/Galosys.Foundation.Nats.Net/Microsoft/Extensions/DependencyInjection/NatsServiceCollectionExtensions.cs`。
  - 新增 `src/Galosys.Foundation.Nats.Net/Galosys/Foundation/Nats/Net/InternalModule.cs`。
  - 修改 `samples/NatsSample.ConsoleApp/` 下 `Program.cs`、`Worker.cs`、`Adapter/Handlers/SampleGreatHandler.cs`、`appsettings.json`；删除 `samples/NatsSample.ConsoleApp/NatsBootstrapper.cs`。
  - 修改根 `README.md`、`AGENTS.md`。
- 公共 API：`public NatsConsumerAttribute` 的命名空间与签名均**不变**（仍为 `NATS.Net`）；新增 `public NatsOptions`、`public NatsBootstrapper`、`public static NatsServiceCollectionExtensions`。本 change 不产生任何 BREAKING 变更。
- 测试：仓库当前仅有 `tests/Galosys.Foundation.Channel.Tests`，无 NATS 测试项目。本 change 以**编译验证 + 端到端手工验证**为主，不新建测试项目（避免引入需要真实 NATS 服务器的 CI 依赖）；反序列化与派发逻辑设计为可单测的纯函数边界，留待后续 change 补测试项目。
- 依赖：不新增 NuGet 包。复用既有 `nats.net` 3.3.1（`NATS.Client.Core` 的 `INatsConnection`/`SubscribeAsync`）与 `galosys.foundation.core` 26.10.29.1（`IObjectSerializer`、`IMessageHandler`、`MessageBase`、`ConcurrentRunner`、`IModule`）。
- 前置：无需其它 change；但依赖上游包 `nats.net` 3.3.1 的 `INatsConnection.SubscribeAsync<T>` 与 `NATS.Extensions.Microsoft.DependencyInjection` 的 `AddNatsClient`（示例已在用）。
- 验证：
  - `dotnet build src/Galosys.Foundation.Nats.Net/Galosys.Foundation.Nats.Net.csproj`
  - `dotnet build samples/NatsSample.ConsoleApp/NatsSample.ConsoleApp.csproj`
  - `openspec validate "nats-consumer-core" --strict --no-interactive`
  - 端到端：`nats-server` 监听 `127.0.0.1:4222`（账号 `appuser`）后运行 `dotnet run --project samples/NatsSample.ConsoleApp/NatsSample.ConsoleApp.csproj`，观察到 handler 输出即视为通过。
