# Design

## Context

参见 `proposal.md` - Why。

当前派发链路（`NatsConsumerDispatcher.DispatchAsync`）：

1. `_serializer.Deserialize(json, subscription.MessageType)`
2. `_root.CreateScope()`，在作用域内 `GetRequiredService(subscription.HandlerMethod.DeclaringType)`
3. `subscription.InvokableHandleAsync.Invoke(handler, new[] { message })`
4. `TargetInvocationException` 分支剥离反射包装后记录日志

`InvokableHandleAsync` 由 `ServiceProviderExtensions.ResolveHandleAsync` 在注册期用
`typeof(IMessageHandler<>).MakeGenericType(messageType).GetMethod("HandleAsync")` 预构造。
该 `MethodInfo` 指向泛型接口方法 `IMessageHandler<T>.HandleAsync(T)`。

约束：

- `NatsSubscription` 是 `internal`，可以自由改字段。
- 公共 API 不能变；处理器写法不能变。
- 每条消息一个 DI 作用域、异常吸收、重试退避都必须保持。

实测基线（.NET 10 Release、单核、空处理器、84 B JSON 载荷、20 万次迭代）：

| 环节 | 耗时 | 分配 |
|---|---|---|
| `MethodInfo.Invoke` | 63–87 ns | 32 B |
| 非泛型 `IMessageHandler` 调用 | 26–36 ns | 0 B |
| JSON 反序列化 | 1.76–2.15 µs | 144 B |
| DI scope + resolve | 639–677 ns | 336 B |
| `UTF8.GetString` | 77–115 ns | 160 B |

## Goals / Non-Goals

**Goals:**

- 移除每条消息上的反射调用与实参数组分配。
- 删除 `InvokableHandleAsync` 字段、`MakeGenericType` 探测、`TargetInvocationException` 分支。
- 保持所有可观察行为不变。

**Non-Goals:**

- 不改 JSON 反序列化（占 66%，受限于 `galosys.foundation.core` 的 `IObjectSerializer`）。
- 不改 DI 作用域策略（占 24%，是规格要求）。
- 不改并发模型、重试语义、取消令牌传递。
- 不做序列化器的 source generation。

## Decisions

### 决策 1：用非泛型 `IMessageHandler` 接口调用，而非构造委托

`Microsoft.Extensions.Messaging.IMessageHandler<T>` 继承 `IMessageHandler`，并为
`IMessageHandler.HandleAsync(IMessage)` 与 `IMessageHandler.CanHandle(IMessage)` 提供默认接口实现。
默认实现会做 `msg.GetType() == typeof(T)` 检查后转发到 `HandleAsync((T)msg)`。

因此派发可以写成：

```csharp
if (handler is not IMessageHandler messageHandler)
{
    _logger.LogError(...);
    return false;
}

return await messageHandler.HandleAsync((IMessage)message).ConfigureAwait(false);
```

**为什么不缓存 `Func<object, object, Task<bool>>`：**

- 需要 `Expression.Lambda` 或 `Delegate.CreateDelegate` 配合 `MakeGenericType`，
  代码量比接口调用大，且首次构造有编译成本。
- 收益与接口调用相同（都是一次间接调用），但多一份可失败的动态代码路径。

**为什么不保留 `MethodInfo.Invoke`：**

- 每条消息 32 B 实参数组 + 反射调用开销。
- 需要 `TargetInvocationException` 分支，异常路径多一层包装。
- 需要 `NatsSubscription` 多存一个字段。

**已实测确认**：两条路径都真正进入处理器的 `HandleAsync(T)` 泛型方法（各调用 1 次，共 2 次），
接口路径耗时 26–36 ns，低于反射的 63–87 ns。

### 决策 2：契约校验改用 `typeof(IMessage).IsAssignableFrom`

`ResolveHandleAsync` 原本用 `MakeGenericType` 抛 `ArgumentException` 来判断形参类型是否满足
`where T : IMessage`。这个探测的目的只是校验 + 取 `MethodInfo`；后者不再需要，前者用
`IsAssignableFrom` 等价表达，且不依赖异常控制流。

行为保持一致：不满足时记录警告并跳过该处理器，不建立订阅。

### 决策 3：保留防御性类型检查

`IMessageHandler<T>` 的实现类必然可转换为 `IMessageHandler`，理论上不可达。仍保留检查并以
`LogError` + `return false` 处理，避免 `InvalidCastException` 逃逸到消费循环——与现有
"异常被吸收、订阅存活" 的语义一致，同时给出更明确的错误信息。

### 决策 4：删除 `TargetInvocationException` 分支

接口调用不会包装异常：同步抛出与 `Task` 内异步抛出都直接落到通用 `catch (Exception ex)`，
由该分支记录并返回 `false`。保留原分支只会成为死代码。

## Risks / Trade-offs

- **[默认接口实现的 `CanHandle` 做精确类型匹配]** 若反序列化返回 `T` 的派生类型，派发会返回
  `false` 而非调用处理器。→ 反序列化始终以 `subscription.MessageType` 为目标类型，返回的就是
  精确类型；在派发处保留注释说明该前提。实践中不可达。
- **[覆写 `CanHandle` 的处理器行为变化]** 若某处理器实现 `IMessageHandler<T>` 但把 `CanHandle`
  覆写为返回 `false`，旧路径（反射直接调 `HandleAsync(T)`）会绕过它，新路径不会。→ 仓库内无此类
  处理器；该行为本身更符合契约语义。
- **[接口调用仍是一次虚分派]** 比直接调用多约 10–20 ns。→ 已实测仍快于反射路径，且这是可读性
  与零反射之间的合理取舍。
- **[启动扫描仍用反射]** `GetMethods` + `GetCustomAttributes` 不变，实测 729 个类型约 5.6 ms，
  一次性成本。→ 本次不处理。

## Migration Plan

纯内部重构，无数据或配置迁移。

- 部署：随下一次类库发布生效，调用方无需改动。
- 回滚：还原三个文件的改动即可，无状态残留。
- 验证：构建类库与 `NatsSample.ConsoleApp`；对本地 nats-server 跑一次端到端，确认处理器被调用、
  异常被记录且订阅存活、停机无残留循环。

## Open Questions

无。
