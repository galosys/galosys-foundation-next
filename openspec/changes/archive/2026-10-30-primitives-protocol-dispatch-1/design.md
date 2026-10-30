## Context

`docs/superpowers/specs/2026-10-22-primitives-server-composition-design.md` §5.3 把同端口多协议拆到 P2.5：`IChannelProtocolSelector` 先窥探连接，选中 `IChannelProtocol`，再由该协议的 handler 处理连接。P1.5 只解决了多 server 组合，没有协议选择；如果现在直接在业务 handler 里读首字节分支，会把“等半包、不能消费、取消、错误中止”这些基础设施语义复制到每个协议。

本 change 建立 P2.5 的最小骨架：两个契约加一个分发器。它不包含任何具体选择器（前缀、ALPN、TLS），也不改变 `ChannelContext`、pipeline 或 server。

## Goals / Non-Goals

**Goals:**

- 定义 `IChannelProtocol` 与 `IChannelProtocolSelector` 的最小公共契约。
- 提供 `ProtocolDispatcher`，其方法组可直接作为 `ChannelHandlerDelegate` 传入 `pipeline.Run`。
- 明确选择器调用一次、使用 `ConnectionClosed`、选中后调用协议。
- 明确无匹配/选择器异常的 `InvalidDataException` 中止语义和取消语义。
- 用 fake selector / protocol 锁定调用顺序、异常、取消和不消费输入。

**Non-Goals:**

- 不实现 `PrefixProtocolSelector`、首字节匹配、半包等待或 `MaxPrefixSize`。
- 不做 ALPN、TLS、`SslStream` 或 TLS 握手后的二次选择。
- 不做协议注册表、DI 注册、配置绑定或 `ProtocolDispatcherBuilder`。
- 不在分发器内部读取输入；窥探完全由选择器负责。
- 不改变 `ChannelPipeline` 中间件契约，也不把协议选择做成中间件。

## Decisions

### 1. 两个契约保持最小

```csharp
public interface IChannelProtocol
{
    string Name { get; }

    ValueTask HandleAsync(ChannelContext context, CancellationToken cancellationToken);
}

public interface IChannelProtocolSelector
{
    ValueTask<IChannelProtocol?> SelectAsync(
        ChannelContext context,
        CancellationToken cancellationToken);
}
```

- `Name` 用于日志、诊断和测试断言；不参与匹配。
- `SelectAsync` 返回 `null` 表示没有匹配协议；分发器负责中止连接。
- `HandleAsync` 返回 `ValueTask`，与现有传输/管道异步风格一致；协议 handler 可以同步完成。
- 选择器和协议都不拥有连接；连接关闭、transport 收尾仍由 `ChannelServerCore` 负责。

**为什么选择器返回可空协议，而不是 `bool TrySelect`：** `ValueTask<IChannelProtocol?>` 能表达异步等待和“无匹配”，调用方只需要一次 `await`，也不需要 out 参数在异步方法中的限制。

### 2. `ProtocolDispatcher` 直接匹配 `ChannelHandlerDelegate`

```csharp
public sealed class ProtocolDispatcher
{
    public ProtocolDispatcher(IChannelProtocolSelector selector);

    public Task HandleAsync(ChannelContext context);
}
```

`HandleAsync(ChannelContext)` 的签名与 `ChannelHandlerDelegate` 完全一致，因此：

```csharp
var dispatcher = new ProtocolDispatcher(selector);
var server = new ChannelServer(
    options,
    pipeline => pipeline.Run(dispatcher.HandleAsync));
```

分发器不实现 `IChannelPipeline`、不注册中间件、不持有 server。它只是一个终端 handler；pipeline 顺序、TLS 包装、遥测中间件仍由调用方决定。

### 3. 选择器只调用一次，令牌统一使用 `ConnectionClosed`

```csharp
var cancellationToken = context.ConnectionClosed;
var protocol = await _selector.SelectAsync(context, cancellationToken).ConfigureAwait(false);
await protocol.HandleAsync(context, cancellationToken).ConfigureAwait(false);
```

- 选择器只调用一次；分发器不重试、不回退到其他选择器。
- 选择器和协议收到同一个 `context.ConnectionClosed` 令牌；连接关闭时两者都能被唤醒。
- 如果进入分发器时令牌已取消，直接返回，不调用选择器。
- 选择完成后、调用协议前再次检查取消；连接已关闭时不再启动协议。
- 协议 handler 抛出的异常不捕获，交给 `ChannelServerCore` 观察并中止连接；这与普通 handler 的语义一致。

### 4. 选择失败用 `InvalidDataException` 中止，不向调用方抛异常

分发器把“无法确定协议”视为连接级数据错误：

```csharp
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
    return;
}
catch (Exception error)
{
    context.Abort(error as InvalidDataException
        ?? new InvalidDataException("协议选择失败。", error));
    return;
}

if (protocol is null)
{
    context.Abort(new InvalidDataException("没有匹配的协议。"));
    return;
}
```

- 使用 `context.Abort(reason)` 而不是 `throw`：`Abort` 是连接级信号，`ChannelServerCore` 会在 handler 返回后完成收尾；抛异常会重复走“handler 异常”路径，并可能产生重复日志。
- 选择器异常包装为 `InvalidDataException`，原始异常放入 `InnerException`；如果选择器已经抛 `InvalidDataException`，直接复用，避免双重包装。
- `AbortReason` 保留第一个原因；后续 framework 收尾不会覆盖它。
- 选择器返回 null 与“无匹配”使用同一路径，调用方不需要区分。

**替代方案：** 让分发器抛 `InvalidDataException`，由 server 的异常路径中止。这样单元测试必须依赖 server 才能看到 `AbortReason`，而且异常会被记录成 handler 故障；`Abort` + 返回更贴近“协议无法识别”的语义，因此否决。

### 5. 选择器只窥探不消费是契约，不是分发器可强制的能力

分发器无法检查 `PipeReader` 的消费位置；它只负责调用选择器。因此契约明确要求：

- 选择器只能通过 `context.Reader.ReadAsync` 观察；
- 每次成功读取后必须 `AdvanceTo(buffer.Start, buffer.End)`；
- 不得把 `Consumed` 推进到 `buffer.Start` 之后；
- 返回协议后，输入仍应完整保留给协议 handler。

测试用一个“读取后返回 null”的 fake selector，在分发器中止后再次读取同一连接，断言原始字节仍在。这样把契约变成可回归的测试。

### 6. 取消不是协议错误

连接关闭时，`ConnectionClosed` 取消，选择器可能抛 `OperationCanceledException`。分发器在 `cancellationToken.IsCancellationRequested` 为 true 时直接返回，不调用 `Abort`，也不产生 `InvalidDataException`。这样正常停机和客户端断开不会制造“协议错误”日志。

### 7. 本 change 不引入 DI 和具体选择器

`ProtocolDispatcher` 的构造函数只接收 selector，不接收 `ILogger`、`IServiceProvider` 或 options。需要日志的调用方可以在自己的 selector/handler 中记录。具体选择器（前缀、ALPN）在后续 change 中实现，避免把“分发骨架”和“匹配算法”绑在一起。

## Risks / Trade-offs

- **选择器消费字节** → 契约 + fake selector 测试；分发器无法在运行时强制，代码审查和 README 都要强调。
- **选择器阻塞导致连接不处理** → 选择器必须自行处理半包/超时；P2.5 的前缀选择器会在后续 change 中提供 `ReadAsync` 等待实现。
- **`Abort` 后返回而不是抛出** → 上层看不到异常，但 `AbortReason` 可诊断；如果调用方需要异常，可在自己的 selector 中抛，分发器会包装。
- **没有日志** → 保持最小依赖；后续可加可选 `ILogger`，不改变契约。
- **协议 handler 异常传播** → 与普通 handler 一致，由 server 观察；不会被分发器吞掉。

## Migration Plan

1. 新增 `IChannelProtocol.cs` 与 `IChannelProtocolSelector.cs`。
2. 新增 `ProtocolDispatcher.cs`，先实现构造、空检查和选择器调用。
3. 加入失败/取消分支：`InvalidDataException` 包装、`context.Abort`、取消返回。
4. 新增 `ProtocolDispatcherTests.cs`，用 fake connection/selector/protocol 覆盖成功、顺序、无匹配、异常、取消和不消费输入。
5. 更新 Primitives README 的协议分发章节。
6. 运行 Primitives 构建与全量测试。

回滚策略：删除三个新源文件、测试和 README 段落；不影响现有 server 或 pipeline。

## Open Questions

无。前缀选择器、ALPN 和 TLS 明确留给 `primitives-protocol-dispatch-2` 及后续阶段。
