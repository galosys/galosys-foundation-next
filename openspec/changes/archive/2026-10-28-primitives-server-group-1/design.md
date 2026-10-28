## Context

`primitives-server-composition-1/2` 已经把服务器入口改为组合式 sealed `ChannelServer`，并删除了继承入口和泛型 DI 入口。单服务器用法因此变简单，但“一个宿主多个 server”仍没有统一生命周期：调用方要自己按顺序启动、在失败时回滚、停机时并行停止并收集异常。`docs/superpowers/specs/2026-10-22-primitives-server-composition-design.md` 把这项工作列为 P1.5b，并明确组只负责组合，不改变单 server 语义。

本 change 只增加 `ChannelServerGroup` 和测试。DI、宿主托管、`ChannelServerDefinition` 与 sample 迁移留给 `primitives-server-group-2`。

## Goals / Non-Goals

**Goals:**

- 提供 public sealed `ChannelServerGroup`，持有并管理一组 `IChannelServer`。
- 定义清晰的启动顺序、失败回滚、并行停机和异常聚合语义。
- 保证组不复制、不覆盖单 server 的 options、`LocalEndPoint`、`ShutdownTimeout` 与错误语义。
- 用 loopback 测试锁定双 server、独立配置、回滚、统一停机和释放幂等。

**Non-Goals:**

- 不实现 DI 注册、`AddChannelServer(definition)` 或 `ChannelServerGroupHostedService`。
- 不实现同端口协议选择、TLS、ALPN、消息层或客户端。
- 不迁移 `NetServer.ConsoleApp`。
- 不改变 `IChannelServer`、`ChannelServer`、`ChannelServerCore` 的公共成员。
- 不提供 list 增删、动态重配置或运行时替换 server。

## Decisions

### 1. 组是纯组合类型，不包装 server 状态

```csharp
public sealed class ChannelServerGroup : IAsyncDisposable, IDisposable
{
    public ChannelServerGroup(params IChannelServer[] servers);

    public IReadOnlyList<IChannelServer> Servers { get; }

    public Task StartAsync(CancellationToken ct);
    public Task StopAsync(CancellationToken ct);
    public ValueTask DisposeAsync();
    public void Dispose();
}
```

构造函数复制输入数组，避免调用方随后修改数组影响组；`Servers` 返回只读视图。空数组合法，所有操作变为 no-op。组不暴露 `LocalEndPoint`、`Options` 或 `IsStarted`，调用方通过 `Servers[i]` 读取每个 server 的真实状态。

**为什么不做 `ChannelServerGroupOptions`：** 停机超时、最大连接数等都属于每个 server 的 `ChannelServerOptions`；组级配置只会引入第二套优先级和覆盖规则。组的唯一职责是编排调用顺序。

### 2. 启动串行，失败按相反顺序回滚

`StartAsync` 按 `Servers` 顺序逐个调用 `server.StartAsync(ct)`。每个 server 成功后加入本次启动列表。任意异常（包括 `OperationCanceledException`）都进入回滚：

```csharp
for (var i = started.Count - 1; i >= 0; i--)
{
    try
    {
        await started[i].StopAsync(CancellationToken.None).ConfigureAwait(false);
    }
    catch (Exception rollbackError)
    {
        rollbackFailures.Add(rollbackError);
    }
}

if (rollbackFailures.Count > 0)
    original.Data["ChannelServerGroup.RollbackFailures"] = new AggregateException(rollbackFailures);

throw;
```

- 使用 `CancellationToken.None` 停止已启动 server，避免启动取消把回滚也取消。
- `throw;` 保留原始异常的类型、实例和堆栈；回滚异常只放入 `Data`，不覆盖原始异常。
- 未成功启动的 server 不调用 `StopAsync`。
- 回滚完成后组不保留“已启动”状态；但已调用过 `StopAsync` 的 `ChannelServer` 不能重新启动，调用方需要重建 server（或新建组）后重试。

**为什么串行启动：** 绑定失败必须能定位到具体 server；并行启动会让“哪个 server 已启动、回滚哪些”依赖完成顺序，测试和诊断都更复杂。启动通常只发生一次，串行成本可接受。

### 3. 停机并行，全部失败用 `AggregateException` 暴露

`StopAsync` 为每个 server 创建一个停止任务，统一 `Task.WhenAll` 等待。每个任务把异常捕获为返回值，而不是让 `WhenAll` 只抛第一个异常：

```csharp
var results = await Task.WhenAll(
    _servers.Select(server => StopOneAsync(server, ct))).ConfigureAwait(false);

var failures = results.Where(error => error is not null).Cast<Exception>().ToArray();
if (failures.Length > 0)
    throw new AggregateException("一个或多个服务器停止失败。", failures);
```

- 无论某个 server 是否失败，其余 server 都会被停止。
- 即使只有一个失败，也统一抛 `AggregateException`，调用方不需要区分“单异常”和“多异常”两种路径。
- 每个 server 仍遵守自己的 `ChannelServerOptions.ShutdownTimeout`；组不引入全局超时。
- `ct` 原样传给每个 `StopAsync`；按 `IChannelServer` 契约，它只取消等待，不取消停机流程。

### 4. 释放幂等，且释放所有 server

`DisposeAsync` 先调用 `StopAsync(CancellationToken.None)`，再对每个 server 调用 `DisposeAsync`。使用 `Interlocked` 加 `TaskCompletionSource` 保证：

- 第一个调用执行停止和释放；
- 并发/后续调用等待同一个完成信号，不重复释放；
- 成功后重复调用直接返回；
- 停止或释放失败时，仍然尝试所有 server，最后抛聚合异常；后续调用观察同一结果。

`Dispose()` 同步等待 `DisposeAsync()`，与 `IChannelServer` 的现有约定一致。

### 5. 组不承担线程安全承诺

`StartAsync` / `StopAsync` / `DisposeAsync` 不应并发调用；`DisposeAsync` 的幂等保护只覆盖释放路径。组内部不引入锁或串行化队列，避免把单 server 的生命周期语义复制一遍。需要并发编排的宿主应自己串行化。

## Risks / Trade-offs

- **回滚失败掩盖原始异常** → 原始异常用 `throw;` 重抛，回滚异常只放 `Data`；测试覆盖“第二个绑定失败、第一个被停止”。
- **并行停机出现部分失败** → 每个停止任务独立捕获异常，`AggregateException` 包含全部失败；测试用一个可控的 fake server 验证。
- **`Dispose` 同步阻塞** → 与现有 `IChannelServer.Dispose` 一致；异步宿主应优先 `DisposeAsync`。
- **空组被误认为配置成功** → 空组合法且 no-op，但 DI 层在 `primitives-server-group-2` 负责提示“没有定义”；本 change 不引入隐式校验。
- **组被误用为动态容器** → `Servers` 只读，构造后不能增删；需要动态集合的调用方自己管理数组。

## Migration Plan

1. 新增 `ChannelServerGroup.cs`，先实现构造、`Servers` 和空组 no-op。
2. 实现 `StartAsync` 的顺序启动与回滚。
3. 实现 `StopAsync` 的并行停止与异常聚合。
4. 实现 `DisposeAsync` / `Dispose` 的幂等释放。
5. 新增 `ChannelServerGroupTests.cs`，覆盖双 server、独立 options、回滚、停机、释放、取消和空组。
6. 更新 Primitives README 的“多服务器”一节。
7. 运行 Primitives 构建与全量测试；现有单 server 调用方不需要修改。

回滚策略：删除 `ChannelServerGroup.cs`、对应测试和 README 段落即可；不会影响 `ChannelServer` 或现有 sample。

## Open Questions

无。DI、宿主托管和 sample 迁移明确留给 `primitives-server-group-2`。
