# Proposal

## Why

`NatsConsumerDispatcher.DispatchAsync` 在每条消息上用 `MethodInfo.Invoke` 调用处理器，并为此在
`NatsSubscription` 上缓存了一个 `InvokableHandleAsync` 的 `MethodInfo`。实测（.NET 10 Release、
单核、空处理器、84 B 载荷）这条反射调用占单条消息派发耗时的 1.3–1.9%（63–87 ns），另分配
32 B/条用于实参数组。

比耗时更值得处理的是它带来的复杂度：`ResolveHandleAsync` 需要 `MakeGenericType` + `GetMethod`
探测契约、`DispatchAsync` 需要专门的 `TargetInvocationException` 分支来剥离反射包装、
`NatsSubscription` 需要多存一个字段。这些都为一次可以被接口调用替代的调用而存在。

`Microsoft.Extensions.Messaging.IMessageHandler<T>` 带有默认接口实现，会转发到非泛型的
`IMessageHandler.HandleAsync(IMessage)`。因此任何 `IMessageHandler<T>` 实现类天然就是
`IMessageHandler`，框架可以直接按接口调用。仓库里成熟的 AMQP 参考实现
（`Galosys.Foundation.Amqp` 的 `ServiceProviderExtensions`）正是这样派发的。

## What Changes

- `NatsConsumerDispatcher.DispatchAsync` 改为把处理器实例转换为 `IMessageHandler` 并调用
  `HandleAsync(IMessage)`，删除 `MethodInfo.Invoke` 与每条消息的实参数组分配。
- 删除 `NatsSubscription.InvokableHandleAsync` 字段。
- `ServiceProviderExtensions.ResolveHandleAsync` 不再用 `MakeGenericType` + `GetMethod` 探测契约，
  改为用 `typeof(IMessage).IsAssignableFrom(messageType)` 校验形参类型，保留原有的
  "跳过并记录警告、不建立订阅" 行为。
- 删除 `TargetInvocationException` 专用 catch 分支：接口调用不再包装异常，同步与异步失败都由
  通用分支记录并返回 `false`。
- 派发前若处理器实例无法转换为 `IMessageHandler`，记录错误并返回 `false`（防御性分支，
  对 `IMessageHandler<T>` 实现类不可达）。

无 **BREAKING** 变更：公共 API 不变、处理器写法不变、订阅与重试语义不变。

## Capabilities

### New Capabilities

无。

### Modified Capabilities

无。本次是纯内部重构：处理器调用、日志、重试、DI 作用域、异常吸收等可观察行为均不改变，
因此不产生 spec delta，`.openspec.yaml` 声明 `skip_specs: true`。

## Impact

- **代码**：`src/Galosys.Foundation.Nats.Net` 下的
  `Galosys/Foundation/Nats/Net/NatsConsumerDispatcher.cs`、
  `Galosys/Foundation/Nats/Net/NatsSubscription.cs`、
  `Microsoft/Extensions/DependencyInjection/ServiceProviderExtensions.cs`。
- **公共 API**：无变化（`NatsSubscription` 为 `internal`）。
- **依赖**：无新增；`System.Reflection` 仅剩扫描阶段使用。
- **行为**：无变化。处理器异常仍被记录且订阅存活；重试次数与退避不变；每条消息仍使用独立
  DI 作用域。
- **性能**：每条消息减少约 40–50 ns 与 32 B 分配；实测单核派发吞吐提升 1.3–1.9%。
- **文档**：`src/Galosys.Foundation.Nats.Net/README.md` 需补充一句派发契约说明
  （处理器必须实现 `IMessageHandler<T>`，框架经非泛型 `IMessageHandler` 派发）。
