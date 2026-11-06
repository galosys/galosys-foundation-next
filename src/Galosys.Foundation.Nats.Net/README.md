# Galosys.Foundation.Nats.Net

Core NATS 消费者框架：特性驱动的处理器扫描、订阅、反序列化与派发。

## 能力范围

- **本库提供**：消费者侧（订阅 → 反序列化 → 派发）。
- **本库不提供**：发布端封装、JetStream、事务性 outbox。发布请直接使用 `INatsConnection.PublishAsync`。

## 快速开始

```csharp
var builder = Host.CreateDefaultBuilder(args);
builder.UseModularization();   // InternalModule 自动接线
builder.ConfigureServices(services =>
{
    services.AddNatsClient(opts => opts.ConfigureOptions(b => b.Configure(o => o.Opts = o.Opts with
    {
        Url = "nats://127.0.0.1:4222"
    })));
});
```

处理器（`NatsConsumerAttribute` 位于 `NATS.Net` 命名空间，与上游 `NatsClient` 共享，
对齐 `AmqpHandlerAttribute`/`RabbitMQ.Client` 的既有约定）：

```csharp
using NATS.Net;

[Handler]
internal class OrderCreatedHandler : IMessageHandler<OrderCreated>
{
    [NatsConsumer("orders.created", queueGroup: "billing")]
    public Task<bool> HandleAsync(OrderCreated msg)
    {
        // ...
        return Task.FromResult(true);
    }
}
```

`UseModularization()` 会自动发现本库的 `InternalModule` 并调用 `services.AddNats(configuration)`；
不使用模块化启动时，手工调用 `services.AddNats(builder.Configuration)`。

## 配置

消费者选项绑定到 `Nats` 配置节：

```json
{
  "Nats": {
    "Concurrency": 1,
    "MaxRetryCount": 0,
    "RetryBaseDelayMs": 100,
    "Handlers": {
      "orders.created": { "Concurrency": 4 }
    }
  }
}
```

| 配置项 | 默认 | 含义 |
|---|---|---|
| `Concurrency` | 1 | 每个 subject 的并发派发数 |
| `MaxRetryCount` | 0 | 处理器异常后的重试次数 |
| `RetryBaseDelayMs` | 100 | 重试基础延迟，第 n 次重试为该值 × 2ⁿ |
| `Handlers` | 空 | 按 subject 覆盖上述设置 |

连接参数（地址、认证、TLS）由上游 `NatsOpts` 经 `AddNatsClient` 管理，不在本库的配置节中。

## 语义说明

- **异常不会终止订阅**：Core NATS 无 broker 侧 redelivery，处理器异常若逃逸会永久终止该 subject 的消费。
  框架内部吸收异常、按 `MaxRetryCount` 重试、耗尽后记录日志并放弃该条消息。
- **反序列化失败跳过该条消息**：记录错误日志后继续消费。
- **每条消息一个 DI 作用域**：handler 的 scoped 依赖在单条消息处理后即释放。
- **队列组语义**：指定 `queueGroup` 时同一队列组内的多个实例负载均衡；不指定则为广播语义。
- **处理器类型需已注册**：`[Handler]` 由 `galosys.foundation.core` 的程序集扫描注册；
  未注册时首条消息处理失败并记录日志，订阅本身保持存活。
- **处理器必须实现 `IMessageHandler<T>`**：框架按非泛型 `IMessageHandler` 契约派发
  （`IMessageHandler<T>` 的默认接口实现会转发到 `HandleAsync(T)`），不做每消息反射。
  方法形参类型未实现 `IMessage` 时，该方法在扫描阶段被跳过并记录警告。

## 与 AMQP 的差异

本库与 `galosys.foundation.amqp` 心智模型一致（特性标记 → 扫描 → 派发），但 NATS Core 没有
exchange/queue/DLX 概念，`NatsConsumerAttribute` 只有 `Subject` 与 `QueueGroup`。
