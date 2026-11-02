## Context

P4-1/P4-2 的单连接客户端已经具备状态机、TLS、关闭与重连。连接池要解决的是“多个调用方共享有限连接”而不是“一个客户端重连”：租借者拿到的是 `IChannelClient`，写消息仍走 `Connection.AsSession<T>()`。P2 的 `ChannelSession<T>` 当前每次 `WriteAsync` 都调用 `FlushAsync`；高频小消息会产生大量系统调用，设计文档把“只 flush 一次”的批量写入留给 P4。

本 change 同时新增两个独立能力：

- `channel-client-pool`：有界连接池，负责创建、复用、健康检查、等待与释放。
- `channel-session` 的批量写入：在现有 `IChannelSession<T>` 上加扩展方法，内置 session 走一次 flush，自定义实现退化为顺序写。

两者不互相依赖；连接池中的客户端可以使用 batch，也可以只用单条 `WriteAsync`。

## Goals / Non-Goals

**Goals:**

- 提供 `IChannelClientFactory` / `ChannelClientFactory`，把客户端创建从池实现中解耦。
- 提供 `ChannelClientPoolOptions` 与 `ChannelClientPool`：`MaxSize` 为硬上限，租借等待有 `RentTimeout`，断线客户端在归还或再次租借时被替换。
- 池的 `DisposeAsync` 幂等：停止租借、释放空闲连接；已租出的连接在归还时释放，不强行中断正在使用的调用方。
- 提供 `WriteBatchAsync<T>`：非空批次在同一个写锁内按顺序写出所有帧，内置 `ChannelSession<T>` 只调用一次 `FlushAsync`。
- 批量写入保持单条消息的长度头/分隔符格式；空批次不写、不 flush。
- 用真实 loopback 服务端和计数 `PipeWriter` 分别验证池与 batch。

**Non-Goals:**

- 不做负载均衡、轮询、按请求粘性路由。
- 不做空闲连接淘汰、最大生命周期、后台清理定时器。
- 不做连接池预热、动态扩缩容或指标。
- 不做事务性批量：一批消息不保证原子送达；失败后连接被中止，接收方可能看到前缀。
- 不改变 `IChannelSession<T>` 的公开成员，不新增 `IChannelSessionBatch` 公开接口。
- 不重放断开期间的消息；池化客户端仍遵循 P4 的连接级语义。

## Decisions

### 1. 工厂接口与默认实现

```csharp
namespace Microsoft.Extensions.Hosting.Net;

public interface IChannelClientFactory
{
    IChannelClient Create(ChannelClientOptions options);
}

public sealed class ChannelClientFactory : IChannelClientFactory
{
    public IChannelClient Create(ChannelClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new ChannelClient(options);
    }
}
```

- 池只依赖工厂，不直接 `new ChannelClient`；测试可以注入计数/故障工厂，后续 DI change 也可以注册自定义工厂。
- 工厂不缓存实例，每次返回新客户端；选项校验由 `ChannelClient` 构造函数完成。
- `ChannelClientPool` 构造函数接受 `IChannelClientFactory? factory = null`，null 时使用 `ChannelClientFactory`。

### 2. 池配置与上限

```csharp
namespace Microsoft.Extensions.Options;

public sealed class ChannelClientPoolOptions
{
    public ChannelClientOptions ClientOptions { get; set; } = new();
    public int MaxSize { get; set; } = 8;
    public TimeSpan RentTimeout { get; set; } = TimeSpan.FromSeconds(30);

    internal void Validate();
    internal ChannelClientPoolOptions Clone();
}
```

- `MaxSize` 必须大于 0，表示“已创建 + 正在创建 + 已租出”的客户端总数上限，而不是空闲队列长度。
- `RentTimeout` 只限制“池已满时等待归还”的时间；创建新连接的时间由 `ChannelClientOptions.ConnectTimeout` 控制，两者不叠加成隐藏超时。
- `ClientOptions` 必须非空并通过 `ChannelClientOptions.Validate()`；池在构造时克隆，之后调用方修改原对象不影响池。
- 不做 `IdleTimeout`：空闲连接保持打开，直到池释放或健康检查失败。需要淘汰时由调用方定期归还/释放或后续 change 增加。
- 池不提供 `Count` 等可观测属性，避免调用方依赖瞬时内部状态；需要指标时后续单独设计。

### 3. 租借算法与槽位预留

```csharp
public async ValueTask<IChannelClient> RentAsync(CancellationToken ct)
{
    while (true)
    {
        ThrowIfDisposed();

        if (_idle.Reader.TryRead(out var idle))
        {
            if (IsHealthy(idle))
                return Rent(idle);

            await DisposeClientAsync(idle);
            ReleaseSlot();
            continue;
        }

        if (TryReserveSlot())
        {
            var client = _factory.Create(_options.ClientOptions);
            try
            {
                await client.ConnectAsync(ct);
                return Rent(client);
            }
            catch
            {
                await DisposeClientAsync(client);
                ReleaseSlot();
                throw;
            }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.RentTimeout);
        // 等待归还；超时抛 TimeoutException，调用方取消抛 OperationCanceledException。
    }
}
```

- 槽位在创建之前预留，避免并发 `RentAsync` 同时看到 `_total < MaxSize` 而超建。
- 空闲队列用无界 `Channel<IChannelClient>`，因为槽位已经限制总数；等待者从同一个队列读取，天然公平。
- 归还时写入空闲队列；若池已释放或客户端不健康，直接释放并归还槽位。
- 等待超时映射为 `TimeoutException`；调用方 token 取消保持 `OperationCanceledException`，不转换成超时。
- 池释放时 `Writer.TryComplete()`，等待中的 `ReadAsync` 抛 `ChannelClosedException`，对外映射为 `ObjectDisposedException`。

### 4. 健康检查与替换

```csharp
private static bool IsHealthy(IChannelClient client)
    => client.State == ChannelConnectionState.Connected && client.Connection is not null;
```

- 健康检查只看状态与连接视图，不主动 ping；真正的断线由 P4-2 的 watcher 迁移状态。
- `RentAsync` 取到不健康空闲客户端时释放并归还槽位，然后重新走“创建或等待”路径。
- `ReturnAsync` 对不健康客户端直接释放，不放回空闲队列；下一个租借者会得到新连接。
- 已租出的客户端如果在租借期间断线，由调用方负责归还；池不会后台扫描租出列表，避免与调用方竞争。
- `Connection` 为 null 但状态仍为 `Connected` 视为不健康（理论上不应发生，防御性处理）。

### 5. 归还、所有权与释放

```csharp
public async ValueTask ReturnAsync(IChannelClient client)
{
    ArgumentNullException.ThrowIfNull(client);

    if (!_rented.TryRemove(client, out _))
        throw new InvalidOperationException("该客户端不属于当前连接池或已归还。");

    if (Volatile.Read(ref _disposed) != 0 || !IsHealthy(client))
    {
        await DisposeClientAsync(client);
        ReleaseSlot();
        return;
    }

    if (!_idle.Writer.TryWrite(client))
    {
        await DisposeClientAsync(client);
        ReleaseSlot();
    }
}
```

- `_rented` 是 `ConcurrentDictionary<IChannelClient, byte>`，防止重复归还或把别的池的客户端还给本池。
- 归还后池仍持有客户端；调用方不得继续使用。重复归还抛 `InvalidOperationException`。
- `DisposeAsync` 幂等：置 `_disposed`、完成空闲队列、逐个释放空闲客户端；已租出的客户端不在 `DisposeAsync` 中强制中止，而是在 `ReturnAsync` 时释放。这样不会在调用方仍在使用时把 socket 抽走。
- 如果调用方永不归还，租出的客户端不会被池释放；这是所有权契约的一部分，文档明确说明。
- `DisposeAsync` 后 `RentAsync` 抛 `ObjectDisposedException`；`ReturnAsync` 仍可接收已租出的客户端并释放它，便于 `await using` 收尾。

### 6. 批量写入：内部能力接口 + 公开扩展

`IChannelSession<T>` 的公开成员不变，批量能力通过 internal 接口暴露：

```csharp
namespace Microsoft.Extensions.Hosting.Net;

internal interface IChannelSessionBatchWriter<T>
{
    ValueTask WriteBatchAsync(IReadOnlyList<T> messages, CancellationToken ct);
}

public static class ChannelSessionBatchExtensions
{
    public static ValueTask WriteBatchAsync<T>(
        this IChannelSession<T> session,
        IReadOnlyList<T> messages,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
            return ValueTask.CompletedTask;

        if (session is IChannelSessionBatchWriter<T> writer)
            return writer.WriteBatchAsync(messages, ct);

        return WriteSequentiallyAsync(session, messages, ct);
    }
}
```

- 内置 `ChannelSession<T>` 实现 internal 接口；自定义 `IChannelSession<T>` 实现没有批量能力时，扩展退化为顺序 `WriteAsync`，保证公开扩展对所有实现可用，但只有内置实现承诺“一次 flush”。
- 空批次直接完成，不获取写锁、不触碰 `PipeWriter`，避免空 batch 产生 flush。
- 顺序回退只保证消息顺序；某条失败时，前面的消息可能已发送，异常原样传播。

### 7. `ChannelSession<T>` 的批量实现

```csharp
public async ValueTask WriteBatchAsync(IReadOnlyList<T> messages, CancellationToken ct)
{
    ArgumentNullException.ThrowIfNull(messages);
    ThrowIfDisposed();
    ct.ThrowIfCancellationRequested();

    if (messages.Count == 0)
        return;

    await _writeLock.WaitAsync(ct).ConfigureAwait(false);
    try
    {
        ThrowIfDisposed();

        foreach (var message in messages)
        {
            try
            {
                using var buffer = new PooledBufferWriter();
                _codec.Encode(buffer, message);

                if (buffer.WrittenMemory.Length > _maxMessageSize)
                    throw new InvalidDataException(
                        $"消息长度 {buffer.WrittenMemory.Length} 超过上限 {_maxMessageSize}。");

                _framer.WriteFrame(_context.Writer, buffer.WrittenSequence);
            }
            catch (Exception ex)
            {
                _context.Abort(ex);
                throw;
            }
        }

        FlushResult flush;
        try
        {
            flush = await _context.Writer.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            _context.Abort(ex);
            throw;
        }

        if (flush.IsCompleted)
            throw new IOException("连接已关闭，无法继续写入。");
    }
    finally
    {
        _writeLock.Release();
    }
}
```

- 整个批次持有 `_writeLock`，与并发 `WriteAsync` 串行化，帧不会交错。
- 每个消息仍单独编码、单独写 frame；不改变 P2 的 framer/codec 格式。
- 只有批次末尾调用一次 `FlushAsync`；背压由 `PipeWriter` 在写入过程中施加。批次大小由调用方控制，框架不额外缓冲。
- 取消在编码/写 frame 期间不会插入检查点（这些是同步调用）；在 `FlushAsync` 取消时中止连接并传播 `OperationCanceledException`。
- 编码、超限或 framer 失败发生在已经写出前面帧之后时，必须 `Abort` 连接；批量不是原子的，接收方可能看到前缀。调用方需要原子性时应自行拆分或使用应用层确认。
- `FlushAsync` 返回 `IsCompleted` 时抛 `IOException`，与单条 `WriteAsync` 一致；不主动释放连接。

### 8. 测试策略

- 连接池测试使用真实 `TestServer` 与 head/tail 计数：复用同一实例、`MaxSize` 上限、等待超时、断线替换、重复归还、池释放。
- 批量测试使用 internal `TestChannelConnection`（实现 `IChannelConnection`）与 `CountingPipeWriter`，直接断言 `FlushCount == 1` 和字节顺序，不依赖 socket。
- 另加一条 loopback 集成测试：服务端用 `AsLineTextSession` 读 N 行，客户端 `WriteBatchAsync` 发送，验证真实 `Pipe` 与帧解析。
- 计数 writer 必须转发 `GetMemory` / `GetSpan` / `Advance` / `FlushAsync` / `Complete` / `CancelPendingFlush`，不改变背压行为。

## Risks / Trade-offs

- **池满时等待超时可能掩盖慢连接** → `RentTimeout` 只限制等待归还，连接创建仍受 `ConnectTimeout` 限制；两个异常类型不同，调用方可区分。
- **归还后调用方继续使用客户端** → 所有权契约不允许；池无法检测。文档与测试明确“归还即失去所有权”。
- **池释放不强制关闭租出连接** → 避免正在处理的请求被抽走 socket；代价是调用方不归还时会泄漏，直到进程结束。需要强制回收时由后续 change 增加 `AbortRented`。
- **健康检查不主动 ping** → 刚断线但 watcher 尚未迁移状态的客户端可能被租出；`ConnectAsync`/写入会失败，调用方归还后池会替换。若需要零失败租借，可后续加入一次轻量探测。
- **批量不是原子的** → 失败后连接被中止，接收方可能看到前缀；这是避免“半帧继续写”的必要取舍。调用方需要事务语义时应在消息层实现。
- **一次 flush 可能让大批次占用较多 `Pipe` 内存** → `PipeWriter` 的 `pauseWriterThreshold` 仍会在写入时施加背压；调用方应控制批次大小。
- **自定义 session 回退为逐条写** → 公开扩展对所有实现可用，但“一次 flush”只对内置 session 成立；README 明确说明。
- **`JitterSource` 类似测试钩子** → 仅 internal，不进入公开 API；连接池/批量不需要随机源。

## Migration Plan

1. 新增 `IChannelClientFactory` / `ChannelClientFactory` 与 `ChannelClientPoolOptions`，先用单元测试锁定默认值、校验和克隆。
2. 实现 `ChannelClientPool` 的创建、槽位预留、空闲队列、归还与超时；用真实 loopback 服务端跑通复用与上限测试。
3. 补健康替换、重复归还、池释放测试；确认 `DisposeAsync` 不强制关闭租出连接。
4. 新增 internal `IChannelSessionBatchWriter<T>` 与公开 `WriteBatchAsync` 扩展，自定义 session 回退顺序写。
5. 让 `ChannelSession<T>` 实现批量路径，用计数 writer 锁定单次 flush、顺序与失败中止。
6. 补 loopback 批量集成测试，更新 README；跑构建、全量测试与 OpenSpec 校验。
7. 回滚时删除池与批量文件、移除 `ChannelSession<T>` 的接口实现即可；P2 单条写路径不变。

## Open Questions

无。空闲淘汰、指标、强制回收租出连接、事务性批量明确留给后续 change。
