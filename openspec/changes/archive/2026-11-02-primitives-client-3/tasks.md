# P4-3 连接池与批量写入 Implementation Plan

> **REQUIRED SUB-SKILL:** 使用 `executing-plans` 或 `opsx-apply-x` 逐任务执行本计划。
> 本仓库虽有 git，但按约定不自动提交；步骤中的“验证”统一用构建 + 测试，不执行 git commit。

**Goal:** 在不改变 `IChannelClient` 与 `IChannelSession<T>` 公开契约的前提下，新增有界客户端连接池、客户端工厂与批量写入扩展；池内客户端可复用、健康检查和替换，批量写入在同一个写锁内按顺序编码多帧且只 flush 一次。

**Architecture:** `ChannelClientPool` 用槽位计数限制“已创建 + 正在创建 + 已租出”的总数，用空闲队列保存健康客户端，用信号量唤醒池满时的等待者；租借超时与调用方取消分别映射为 `TimeoutException` 与 `OperationCanceledException`。批量写入通过 internal `IChannelSessionBatchWriter<T>` 让内置 `ChannelSession<T>` 走一次 flush 的快路径，自定义 session 由公开扩展退化为顺序 `WriteAsync`。

**Tech Stack:** .NET 10、`System.Threading.Channels`、`System.Collections.Concurrent`、`System.IO.Pipelines`、`Microsoft.Extensions.Options`、xunit 2.9.3。

---

## 1. 工厂与池配置

- [x] 1.1 写 `ChannelClientPoolOptions` 失败测试并实现默认值、校验与克隆 — 已完成。

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Options/ChannelClientPoolOptions.cs`
- Create: `tests/Galosys.Foundation.Primitives.Tests/ChannelClientPoolOptionsTests.cs`

实现：

```csharp
using System;

namespace Microsoft.Extensions.Options
{
    /// <summary>连接池配置。</summary>
    public sealed class ChannelClientPoolOptions
    {
        /// <summary>池内客户端的连接配置；构造池时会克隆。</summary>
        public ChannelClientOptions ClientOptions { get; set; } = new();

        /// <summary>已创建 + 正在创建 + 已租出的客户端总数上限，默认 8。</summary>
        public int MaxSize { get; set; } = 8;

        /// <summary>池满时等待归还的最长时间，默认 30 秒。</summary>
        public TimeSpan RentTimeout { get; set; } = TimeSpan.FromSeconds(30);

        internal void Validate()
        {
            if (ClientOptions is null)
                throw new ArgumentException("必须提供 ClientOptions。", nameof(ClientOptions));

            ClientOptions.Validate();

            if (MaxSize <= 0)
                throw new ArgumentException("MaxSize 必须大于 0。", nameof(MaxSize));

            if (RentTimeout <= TimeSpan.Zero || RentTimeout > TimeSpan.FromDays(1))
                throw new ArgumentException("RentTimeout 必须大于 0 且不超过 1 天。", nameof(RentTimeout));
        }

        internal ChannelClientPoolOptions Clone()
        {
            return new ChannelClientPoolOptions
            {
                ClientOptions = ClientOptions.Clone(),
                MaxSize = MaxSize,
                RentTimeout = RentTimeout,
            };
        }
    }
}
```

测试至少覆盖：

```csharp
[Fact]
public void Defaults_MatchDesign()
{
    var options = new ChannelClientPoolOptions();

    Assert.Equal(8, options.MaxSize);
    Assert.Equal(TimeSpan.FromSeconds(30), options.RentTimeout);
    Assert.NotNull(options.ClientOptions);
}

[Fact]
public void Validate_Throws_WhenMaxSizeNotPositive()
{
    var options = new ChannelClientPoolOptions
    {
        ClientOptions = ValidClientOptions(),
        MaxSize = 0,
    };

    Assert.Throws<ArgumentException>(() => options.Validate());
}

[Fact]
public void Clone_IsolatesClientOptions()
{
    var options = new ChannelClientPoolOptions
    {
        ClientOptions = ValidClientOptions(),
        MaxSize = 2,
    };

    var clone = options.Clone();
    options.ClientOptions.EndPoint = new IPEndPoint(IPAddress.Loopback, 1);

    Assert.NotSame(options.ClientOptions, clone.ClientOptions);
    Assert.NotEqual(options.ClientOptions.EndPoint, clone.ClientOptions.EndPoint);
    Assert.Equal(2, clone.MaxSize);
}
```

验证：

```powershell
dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelClientPoolOptionsTests"
```

Expected: 全部通过。

- [x] 1.2 写 `IChannelClientFactory` / `ChannelClientFactory` 测试并实现 — 已完成。

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Client/IChannelClientFactory.cs`
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Client/ChannelClientFactory.cs`
- Create: `tests/Galosys.Foundation.Primitives.Tests/ChannelClientFactoryTests.cs`

实现：

```csharp
namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>客户端创建工厂；连接池通过它创建客户端。</summary>
    public interface IChannelClientFactory
    {
        /// <summary>创建新的客户端；每次调用都返回新实例。</summary>
        IChannelClient Create(ChannelClientOptions options);
    }
}
```

```csharp
namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>默认客户端工厂。</summary>
    public sealed class ChannelClientFactory : IChannelClientFactory
    {
        /// <inheritdoc />
        public IChannelClient Create(ChannelClientOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return new ChannelClient(options);
        }
    }
}
```

测试：

```csharp
[Fact]
public void Create_ReturnsNewClientEachTime()
{
    var factory = new ChannelClientFactory();
    var options = new ChannelClientOptions
    {
        EndPoint = new IPEndPoint(IPAddress.Loopback, 12345),
    };

    var first = factory.Create(options);
    var second = factory.Create(options);

    Assert.NotSame(first, second);
    Assert.IsAssignableFrom<IChannelClient>(first);
}

[Fact]
public void Create_Throws_WhenOptionsNull()
{
    var factory = new ChannelClientFactory();

    Assert.Throws<ArgumentNullException>(() => factory.Create(null!));
}
```

验证：

```powershell
dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelClientFactoryTests"
```

Expected: 全部通过。

## 2. 连接池核心

- [x] 2.1 实现 `ChannelClientPool` 的构造、槽位预留、租借、复用、上限、超时与取消 — 已完成。

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Client/ChannelClientPool.cs`
- Create: `tests/Galosys.Foundation.Primitives.Tests/ChannelClientPoolTests.cs`

公共签名：

```csharp
public sealed class ChannelClientPool : IAsyncDisposable
{
    public ChannelClientPool(
        ChannelClientPoolOptions options,
        IChannelClientFactory? factory = null);

    public ValueTask<IChannelClient> RentAsync(CancellationToken ct);

    public ValueTask ReturnAsync(IChannelClient client);

    public ValueTask DisposeAsync();
}
```

内部字段与算法要点：

```csharp
private readonly ChannelClientPoolOptions _options;
private readonly IChannelClientFactory _factory;
private readonly Channel<IChannelClient> _idle = Channel.CreateUnbounded<IChannelClient>();
private readonly SemaphoreSlim _available = new(0);
private readonly ConcurrentDictionary<IChannelClient, byte> _rented = new();
private readonly CancellationTokenSource _disposeCts = new();
private int _total;
private int _disposed;
```

- 构造时 `options.Clone()` + `Validate()`；未传工厂时使用 `ChannelClientFactory`。
- `TryReserveSlot()` 用 `Interlocked.CompareExchange` 把 `_total` 增加到 `MaxSize` 为止；`ReleaseSlot()` 递减。
- `RentAsync` 循环：先 `_idle.Reader.TryRead`；健康则登记到 `_rented` 并返回；不健康则释放并继续。
- 没有空闲且槽位可用时创建客户端并 `ConnectAsync`；失败时释放客户端与槽位，原异常传播。
- 池满时用一次性 deadline 等待 `_available`；等待期间调用方取消抛 `OperationCanceledException`；超时抛 `TimeoutException`。
- 池释放通过 `_disposeCts` 取消等待，并映射为 `ObjectDisposedException`；不能只依赖 `ChannelClosedException`。
- `ReturnAsync` 在释放不健康客户端后必须 `_available.Release()` 唤醒等待者，避免等待者误超时。

测试至少覆盖：

```csharp
[Fact]
public async Task RentAsync_ReusesReturnedClient()
{
    await using var server = await TestServer.StartAsync(TestHandlers.EchoAsync);
    await using var pool = CreatePool(server.EndPoint, maxSize: 1);

    var first = await pool.RentAsync(CancellationToken.None);
    await pool.ReturnAsync(first);
    var second = await pool.RentAsync(CancellationToken.None);

    Assert.Same(first, second);
    await pool.ReturnAsync(second);
}

[Fact]
public async Task RentAsync_WaitsWhenPoolFull()
{
    await using var server = await TestServer.StartAsync(TestHandlers.EchoAsync);
    await using var pool = CreatePool(server.EndPoint, maxSize: 1, rentTimeout: TimeSpan.FromSeconds(2));

    var first = await pool.RentAsync(CancellationToken.None);
    var secondTask = pool.RentAsync(CancellationToken.None).AsTask();

    await Task.Delay(100);
    Assert.False(secondTask.IsCompleted);

    await pool.ReturnAsync(first);
    var second = await secondTask.WaitAsync(TestWaits.DefaultTimeout);

    Assert.Same(first, second);
    await pool.ReturnAsync(second);
}

[Fact]
public async Task RentAsync_TimesOutWhenPoolExhausted()
{
    await using var server = await TestServer.StartAsync(TestHandlers.EchoAsync);
    await using var pool = CreatePool(
        server.EndPoint,
        maxSize: 1,
        rentTimeout: TimeSpan.FromMilliseconds(100));

    var first = await pool.RentAsync(CancellationToken.None);

    await Assert.ThrowsAsync<TimeoutException>(
        () => pool.RentAsync(CancellationToken.None).AsTask());

    Assert.Equal(ChannelConnectionState.Connected, first.State);
    await pool.ReturnAsync(first);
}
```

辅助方法：

```csharp
private static ChannelClientPool CreatePool(
    IPEndPoint endPoint,
    int maxSize,
    TimeSpan? rentTimeout = null)
    => new(new ChannelClientPoolOptions
    {
        ClientOptions = new ChannelClientOptions
        {
            EndPoint = endPoint,
            ConnectTimeout = TimeSpan.FromSeconds(2),
        },
        MaxSize = maxSize,
        RentTimeout = rentTimeout ?? TimeSpan.FromSeconds(2),
    });
```

补充测试：

- 并发租借不超过 `MaxSize`：用计数工厂包一层 `ChannelClientFactory`，启动多个 rent/return 任务，断言创建数不超过上限。
- 创建失败释放槽位：先对关闭端口 rent 失败，再在同一端点启动服务器，第二次 rent 成功。
- 池满等待期间调用方取消：第二个 `RentAsync` 用可取消 token，取消后断言 `OperationCanceledException`。

验证：

```powershell
dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelClientPoolTests"
```

Expected: 租借相关测试全部通过。

- [x] 2.2 实现健康检查、空闲替换与 `ReturnAsync` 所有权校验 — 已完成。

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Client/ChannelClientPool.cs`
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ChannelClientPoolTests.cs`

实现：

```csharp
private static bool IsHealthy(IChannelClient client)
    => client.State == ChannelConnectionState.Connected && client.Connection is not null;

public async ValueTask ReturnAsync(IChannelClient client)
{
    ArgumentNullException.ThrowIfNull(client);

    if (!_rented.TryRemove(client, out _))
        throw new InvalidOperationException("该客户端不属于当前连接池或已归还。");

    if (Volatile.Read(ref _disposed) != 0 || !IsHealthy(client))
    {
        await DisposeClientAsync(client).ConfigureAwait(false);
        ReleaseSlot();
        _available.Release();
        return;
    }

    if (!_idle.Writer.TryWrite(client))
    {
        await DisposeClientAsync(client).ConfigureAwait(false);
        ReleaseSlot();
    }

    _available.Release();
}
```

测试至少覆盖：

- `ReturnAsync_Throws_WhenClientNull` → `ArgumentNullException`。
- `ReturnAsync_Throws_WhenClientAlreadyReturned` → 第二次 `InvalidOperationException`。
- `ReturnAsync_Throws_WhenClientFromAnotherPool` → 池 B 不释放池 A 的客户端。
- `ReturnAsync_DisposesUnhealthyClient`：服务端第一条连接主动 `Abort`，客户端变为非 `Connected` 后归还；下一次 rent 创建新客户端。
- `RentAsync_ReplacesUnhealthyIdleClient`：先 rent/return 健康客户端，再让服务端中止该连接；下一次 rent 释放旧客户端并返回新实例。
- 池不后台扫描租出客户端：租出后断线，池不会自动创建替换连接；归还后才释放并替换。

验证：

```powershell
dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelClientPoolTests"
```

Expected: 健康替换与归还测试全部通过。

- [x] 2.3 实现 `DisposeAsync` 的幂等、停止租借、释放空闲与租出连接延迟释放 — 已完成。

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Client/ChannelClientPool.cs`
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ChannelClientPoolTests.cs`

实现：

```csharp
public async ValueTask DisposeAsync()
{
    if (Interlocked.Exchange(ref _disposed, 1) != 0)
        return;

    _disposeCts.Cancel();
    _idle.Writer.TryComplete();

    while (_idle.Reader.TryRead(out var client))
        await DisposeClientAsync(client).ConfigureAwait(false);
}
```

- `RentAsync` 开头与等待循环内都检查 `_disposed`；等待被 `_disposeCts` 取消时抛 `ObjectDisposedException`。
- `ReturnAsync` 在池释放后仍接受已租出的客户端：释放客户端、归还槽位，不抛异常。
- 不强制关闭仍租出的客户端；调用方归还时才释放。
- `DisposeAsync` 可重复调用。

测试至少覆盖：

- 释放空闲客户端后 `State == Closed`，后续 `RentAsync` 抛 `ObjectDisposedException`。
- 释放时仍有租出客户端：客户端保持 `Connected`；归还后变为 `Closed`。
- 连续两次 `DisposeAsync` 正常返回。
- 等待中的 `RentAsync` 在池释放后抛 `ObjectDisposedException`，不挂起。

验证：

```powershell
dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelClientPoolTests"
```

Expected: 连接池测试全部通过。

## 3. 批量写入

- [x] 3.1 新增 internal 批量接口与公开 `WriteBatchAsync` 扩展，覆盖空批次、空参数与自定义 session 回退 — 已完成。

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Session/IChannelSessionBatchWriter.cs`
- Create: `src/Galosys.Foundation.Primitives/Session/ChannelSessionBatchExtensions.cs`
- Create: `tests/Galosys.Foundation.Primitives.Tests/ChannelSessionBatchTests.cs`

实现：

```csharp
namespace Microsoft.Extensions.Hosting.Net
{
    internal interface IChannelSessionBatchWriter<T>
    {
        ValueTask WriteBatchAsync(IReadOnlyList<T> messages, CancellationToken ct);
    }
}
```

```csharp
namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>消息会话的批量写入扩展。</summary>
    public static class ChannelSessionBatchExtensions
    {
        /// <summary>按顺序写入一批消息；空批次不写、不 flush。</summary>
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

        private static async ValueTask WriteSequentiallyAsync<T>(
            IChannelSession<T> session,
            IReadOnlyList<T> messages,
            CancellationToken ct)
        {
            foreach (var message in messages)
                await session.WriteAsync(message, ct).ConfigureAwait(false);
        }
    }
}
```

测试用一个记录调用的 fake session：

```csharp
private sealed class RecordingSession<T> : IChannelSession<T>
{
    public List<T> Written { get; } = new();

    public ValueTask<T> ReadAsync(CancellationToken ct)
        => throw new NotSupportedException();

    public ValueTask WriteAsync(T message, CancellationToken ct)
    {
        Written.Add(message);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

覆盖：

- 空列表：不调用 `WriteAsync`，返回已完成的 `ValueTask`。
- `session` / `messages` 为 null：`ArgumentNullException`。
- 自定义 session：按输入顺序逐条写，取消 token 传递。
- 单条写入路径不变：现有 `SessionTests` 继续通过。

验证：

```powershell
dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelSessionBatchTests"
```

Expected: 扩展契约测试全部通过。

- [x] 3.2 让 `ChannelSession<T>` 实现批量路径，锁定单次 flush、顺序与空批次无操作 — 已完成。

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Session/ChannelSession.cs`
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ChannelSessionBatchTests.cs`
- Create: `tests/Galosys.Foundation.Primitives.Tests/BatchTestSupport.cs`

`ChannelSession<T>` 增加 `IChannelSessionBatchWriter<T>` 实现：

```csharp
internal sealed class ChannelSession<T> : IChannelSession<T>, IChannelSessionBatchWriter<T>
{
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
                using var buffer = new PooledBufferWriter();
                _codec.Encode(buffer, message);

                if (buffer.WrittenMemory.Length > _maxMessageSize)
                    throw new InvalidDataException(
                        $"消息长度 {buffer.WrittenMemory.Length} 超过上限 {_maxMessageSize}。");

                _framer.WriteFrame(_context.Writer, buffer.WrittenSequence);
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
        catch (Exception ex)
        {
            _context.Abort(ex);
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
```

> 注意：`catch` 会捕获 `OperationCanceledException` 与 `InvalidDataException`；内置批量路径对任何批次失败都中止连接，避免半批数据被后续写入误认为完整消息。`FlushAsync` 的 `IsCompleted` 与单条 `WriteAsync` 一致，只抛 `IOException`，不额外 `Abort`。

测试支持：

```csharp
internal sealed class TestChannelConnection : IChannelConnection
{
    private readonly TaskCompletionSource<Exception?> _termination =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TestChannelConnection(IDuplexPipe transport) => Transport = transport;

    public EndPoint? LocalEndPoint => null;
    public EndPoint? RemoteEndPoint => null;
    public IDuplexPipe Transport { get; }
    public Task<Exception?> Termination => _termination.Task;
    public Task Completion => _completion.Task;
    public Exception? AbortReason { get; private set; }

    public void Abort(Exception? reason)
    {
        AbortReason = reason;
        _termination.TrySetResult(reason);
        _completion.TrySetResult();
    }

    public ValueTask DisposeAsync()
    {
        _completion.TrySetResult();
        return ValueTask.CompletedTask;
    }
}

internal sealed class CountingPipeWriter : PipeWriter
{
    private readonly PipeWriter _inner;
    private int _flushCount;

    public CountingPipeWriter(PipeWriter inner) => _inner = inner;

    public int FlushCount => Volatile.Read(ref _flushCount);

    public override void Advance(int bytes) => _inner.Advance(bytes);
    public override Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);
    public override Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);
    public override void CancelPendingFlush() => _inner.CancelPendingFlush();
    public override void Complete(Exception? exception = null) => _inner.Complete(exception);

    public override ValueTask<FlushResult> FlushAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _flushCount);
        return _inner.FlushAsync(ct);
    }
}
```

测试：

```csharp
[Fact]
public async Task WriteBatchAsync_WritesAllFramesAndFlushesOnce()
{
    var pipe = new Pipe();
    var writer = new CountingPipeWriter(pipe.Writer);
    var connection = new TestChannelConnection(
        new DuplexPipe(pipe.Reader, writer));
    var context = new ChannelContext(
        "batch",
        connection,
        EmptyServiceProvider.Instance);
    await using var session = context.AsJsonSession<int>(
        options: new MessageChannelOptions { MaxMessageSize = 1024 });

    await session.WriteBatchAsync(new[] { 1, 2, 3 });

    Assert.Equal(1, writer.FlushCount);

    var first = await session.ReadAsync(CancellationToken.None); // 若测试不方便读回，可用独立 PipeReader 解析
}
```

更简单的断言方式：只写不读，使用 `pipe.Reader.ReadAsync` 解析三个长度前缀帧并反序列化；或使用 `session.ReadAsync` 会与写端共享同一连接，不适合单向测试。建议在测试里直接从 `pipe.Reader` 解析：

```csharp
var read = await pipe.Reader.ReadAsync();
var buffer = read.Buffer;
var messages = new List<int>();
var codec = new JsonMessageCodec<int>();
var framer = new LengthPrefixedFramer();
while (framer.Parse(buffer) is { IsComplete: true } parsed)
{
    messages.Add(codec.Decode(parsed.Frame));
    buffer = buffer.Slice(parsed.Consumed);
}
Assert.Equal(new[] { 1, 2, 3 }, messages);
```

补充：

- 空批次：`FlushCount == 0`，连接仍可用。
- 帧格式与逐条 `WriteAsync` 一致：同一组消息逐条写与批量写产生的字节相同（可比较两次独立 `Pipe` 的输出）。

验证：

```powershell
dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelSessionBatchTests"
```

Expected: 单次 flush、顺序与空批次测试全部通过。

- [x] 3.3 覆盖批量失败、超限、取消与单条写并发串行化 — 已完成。

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Session/ChannelSession.cs`
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ChannelSessionBatchTests.cs`

增加测试：

- 第二条消息编码失败：第一条帧已写出，`AbortReason` 非 null，原异常传播。
- 第二条消息超过 `MaxMessageSize`：连接中止，`InvalidDataException`。
- `FlushAsync` 取消：`OperationCanceledException`，`AbortReason` 非 null，不继续写。
- 并发 `WriteAsync` + `WriteBatchAsync`：两边的帧不交错；接收端能解析全部消息。
- 批次持有写锁：批量期间单条写等待，批量完成后单条写才写入。

编码失败 fake codec：

```csharp
private sealed class FailingCodec : IMessageCodec<int>
{
    public int Decode(ReadOnlySequence<byte> frame) => throw new NotSupportedException();

    public void Encode(IBufferWriter<byte> writer, int message)
    {
        if (message == 2)
            throw new InvalidOperationException("encode-boom");

        writer.Write(BitConverter.GetBytes(message));
    }
}
```

验证：

```powershell
dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelSessionBatchTests"
```

Expected: 失败与并发测试全部通过。

- [x] 3.4 增加 batch 的真实 loopback 集成测试 — 已完成。

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ChannelSessionBatchTests.cs`

测试：

```csharp
[Fact]
public async Task WriteBatchAsync_LoopbackLinesArriveInOrder()
{
    var received = new List<string>();
    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    await using var server = await TestServer.StartAsync(async context =>
    {
        await using var session = context.AsLineTextSession();
        for (var i = 0; i < 3; i++)
            received.Add(await session.ReadAsync(context.ConnectionClosed));

        done.TrySetResult();
    });

    await using var client = new ChannelClient(new ChannelClientOptions
    {
        EndPoint = server.EndPoint,
        ConnectTimeout = TimeSpan.FromSeconds(2),
    });
    await client.ConnectAsync(CancellationToken.None);

    await using var session = client.Connection!.AsLineTextSession();
    await session.WriteBatchAsync(new[] { "alpha", "beta", "gamma" });

    await done.Task.WaitAsync(TestWaits.DefaultTimeout);
    Assert.Equal(new[] { "alpha", "beta", "gamma" }, received);
}
```

验证：

```powershell
dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~WriteBatchAsync_LoopbackLinesArriveInOrder"
```

Expected: 通过，服务端按顺序收到三条消息。

## 4. 文档与全量验证

- [x] 4.1 更新 Primitives README 的连接池与批量写入章节（归档阶段执行） — 已完成：新增“连接池（P4-3）”与“批量写入（P4-3）”章节，并更新开头与阶段说明。

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/README.md`

增加：

```markdown
## 连接池（P4-3）

```csharp
await using var pool = new ChannelClientPool(new ChannelClientPoolOptions
{
    ClientOptions = new ChannelClientOptions
    {
        EndPoint = endPoint,
        ConnectTimeout = TimeSpan.FromSeconds(5),
    },
    MaxSize = 8,
    RentTimeout = TimeSpan.FromSeconds(30),
});

var client = await pool.RentAsync(cancellationToken);
try
{
    // 使用 client.Connection
}
finally
{
    await pool.ReturnAsync(client);
}
```

- `MaxSize` 是已创建 + 正在创建 + 已租出的硬上限。
- 池满时 `RentAsync` 最多等待 `RentTimeout`，超时抛 `TimeoutException`。
- 归还即转移所有权；重复归还或归还其他池的客户端抛 `InvalidOperationException`。
- 池释放不强制关闭已租出客户端，归还时才释放。

## 批量写入（P4-3）

```csharp
await using var session = client.Connection!.AsLineTextSession();
await session.WriteBatchAsync(new[] { "alpha", "beta", "gamma" });
```

- 非空批次持有同一个写锁，按顺序编码并写出多帧，只调用一次 `FlushAsync`。
- 空批次不写、不 flush。
- 内置 session 走单次 flush 快路径；自定义 `IChannelSession<T>` 退化为顺序 `WriteAsync`。
- 批次失败会中止连接，不保证原子送达。
```

同时更新文件开头的阶段说明与末尾阶段列表，把 P4-3 标记为当前阶段，并把“连接池与 batch 不在本阶段”改为已完成说明。

> **apply-x 阶段跳过此任务**，保持 `- [ ]`，由 `archive-x` 完成。

- [x] 4.2 跑构建、全量测试与 OpenSpec 严格校验 — 已完成：构建 0 errors，244/244 测试通过，`openspec validate --strict` 通过。

```powershell
dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj
dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj
openspec validate "primitives-client-3" --strict --no-interactive
```

Expected:
- 构建 0 errors。
- 现有测试与新增 `ChannelClientPoolOptionsTests`、`ChannelClientFactoryTests`、`ChannelClientPoolTests`、`ChannelSessionBatchTests` 全部通过。
- OpenSpec 严格校验通过。

## Workflow follow-up

- 归档前完成 README 任务 4.1。
- 使用 `openspec archive primitives-client-3 -y` 归档，并确认 `openspec/specs/channel-client-pool/spec.md` 与 `openspec/specs/channel-session/spec.md` 已同步。
- 归档后运行 `openspec validate --archived --no-interactive`，确认归档 change 任务全部完成。
