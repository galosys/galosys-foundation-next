# P2-3 每连接消息会话 Implementation Plan

> **REQUIRED SUB-SKILL:** 使用 `executing-plans` 或 `opsx-apply-x` 逐任务执行本计划。
> 本仓库虽有 git，但按约定不自动提交；步骤中的“验证”统一用构建 + 测试，不执行 git commit。

**Goal:** 把 `IMessageFramer` 与 `IMessageCodec<T>` 组合成每连接一个的拉模型 `IChannelSession<T>`，并锁定 EOF、取消、并发、背压和所有权语义。

**Architecture:** `ChannelSession<T>` 借用 `ChannelContext`，不拥有连接；读循环调用 framer/codec，写路径先编码到池化 buffer 再写 frame。`ChannelContext` 的 internal 槽位用 CAS 保证一个连接最多一个 session。扩展方法只做组合，不引入合并接口。

**Tech Stack:** .NET 10、`System.IO.Pipelines`、`System.Buffers`、`Microsoft.Extensions.Options`、xunit 2.9.3。

---

## 1. 契约与测试先行

- [ ] 3.1 写半包重组的失败测试

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/SessionTests.cs`

```csharp
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class SessionTests
{
    [Fact]
    public async Task LineSession_HalfPackets_AreReassembled()
    {
        await using var server = await TestServer.StartAsync(async context =>
        {
            await using var session = context.AsLineTextSession();
            var line = await session.ReadAsync(context.ConnectionClosed);
            await session.WriteAsync(line, context.ConnectionClosed);
        });

        using var client = await TestClient.ConnectAsync(server.EndPoint);
        var stream = client.GetStream();

        await stream.WriteAsync("hel"u8.ToArray());
        await Task.Delay(50);
        await stream.WriteAsync("lo\n"u8.ToArray());

        Assert.Equal("hello", await ReadLineAsync(stream));
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];

        while (true)
        {
            var read = await stream.ReadAsync(one).AsTask().WaitAsync(TestWaits.DefaultTimeout);
            if (read == 0)
                throw new IOException("连接在读到换行前关闭。");

            if (one[0] == (byte)'\n')
                break;

            buffer.WriteByte(one[0]);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~SessionTests`
Expected: 编译失败，提示 `IChannelSession<T>` / `AsLineTextSession` 不存在。

- [ ] 3.2 新增 `IChannelSession<T>`

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Session/IChannelSession.cs`

```csharp
namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>每连接一个的拉模型消息会话；不拥有连接。</summary>
    public interface IChannelSession<T> : IAsyncDisposable
    {
        /// <summary>读取一条完整消息；干净 EOF 抛 <see cref="EndOfStreamException"/>。</summary>
        ValueTask<T> ReadAsync(CancellationToken ct);

        /// <summary>写入一条消息；并发写由 session 串行化。</summary>
        ValueTask WriteAsync(T message, CancellationToken ct);
    }
}
```

- [ ] 3.3 新增 `MessageChannelOptions` 与 `ChannelContext` 槽位

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Options/MessageChannelOptions.cs`
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/ChannelContext.cs`

**Step 1: 定义会话选项**

```csharp
namespace Microsoft.Extensions.Options
{
    /// <summary>消息会话配置。</summary>
    public sealed class MessageChannelOptions
    {
        /// <summary>单条消息 payload 的硬上限，默认 1 MiB。</summary>
        public int MaxMessageSize { get; set; } = 1024 * 1024;
    }
}
```

**Step 2: 在 `ChannelContext` 字段区加入槽位**

```csharp
private object? _session;
```

**Step 3: 在 `ChannelContext` 内部成员区加入方法**

```csharp
internal bool TrySetSession(object session)
    => Interlocked.CompareExchange(ref _session, session, null) is null;

internal object? Session => Volatile.Read(ref _session);
```

**Step 4: 编译**

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

- [ ] 3.4 新增池化 `PooledBufferWriter`

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Session/PooledBufferWriter.cs`

```csharp
using System.Buffers;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>基于 ArrayPool 的可增长 writer，用于先编码 payload 再加帧头。</summary>
    internal sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(4096);
        private int _written;

        public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

        public ReadOnlySequence<byte> WrittenSequence => new(_buffer, 0, _written);

        public void Advance(int count)
        {
            if (count < 0 || checked(_written + count) > _buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsSpan(_written);
        }

        private void Ensure(int sizeHint)
        {
            if (sizeHint < 1)
                sizeHint = 1;

            var required = checked(_written + sizeHint);
            if (required <= _buffer.Length)
                return;

            var next = ArrayPool<byte>.Shared.Rent(Math.Max(required, checked(_buffer.Length * 2)));
            _buffer.AsSpan(0, _written).CopyTo(next);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = next;
        }

        public void Dispose()
        {
            var buffer = _buffer;
            _buffer = Array.Empty<byte>();

            if (buffer.Length > 0)
                ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
```

- [ ] 3.5 实现 `ChannelSession<T>` 的构造与释放

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Session/ChannelSession.cs`

```csharp
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting.Net
{
    internal sealed class ChannelSession<T> : IChannelSession<T>
    {
        private readonly ChannelContext _context;
        private readonly IMessageFramer _framer;
        private readonly IMessageCodec<T> _codec;
        private readonly int _maxMessageSize;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private int _reading;
        private int _disposed;

        internal ChannelSession(
            ChannelContext context,
            IMessageFramer framer,
            IMessageCodec<T> codec,
            MessageChannelOptions options)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(framer);
            ArgumentNullException.ThrowIfNull(codec);
            ArgumentNullException.ThrowIfNull(options);

            if (options.MaxMessageSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(options), "MaxMessageSize 必须大于 0。");

            _context = context;
            _framer = framer;
            _codec = codec;
            _maxMessageSize = options.MaxMessageSize;

            if (!context.TrySetSession(this))
            {
                _writeLock.Dispose();
                throw new InvalidOperationException("每个连接只能创建一个会话。");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            await _writeLock.WaitAsync().ConfigureAwait(false);
            _writeLock.Release();

            // 不 Dispose SemaphoreSlim：可能仍有已通过 disposed 检查的等待者；
            // 它不持有非托管句柄，保留到 GC 是安全的。
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(ChannelSession<T>));
        }
    }
}
```

- [ ] 3.6 实现 `ChannelSession<T>.ReadAsync`

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Session/ChannelSession.cs`

```csharp
using System.IO;
using System.IO.Pipelines;

public async ValueTask<T> ReadAsync(CancellationToken ct)
{
    ThrowIfDisposed();

    if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
        throw new InvalidOperationException("同一个会话不允许并发读取。");

    try
    {
        while (true)
        {
            var result = await _context.Reader.ReadAsync(ct).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (buffer.IsEmpty && result.IsCompleted)
                throw new EndOfStreamException();

            FrameParseResult parse;
            try
            {
                parse = _framer.Parse(buffer);
            }
            catch (Exception ex)
            {
                _context.Abort(ex);
                throw;
            }

            if (parse.IsComplete)
            {
                if (parse.Frame.Length > _maxMessageSize)
                {
                    var ex = new InvalidDataException(
                        $"消息长度 {parse.Frame.Length} 超过上限 {_maxMessageSize}。");
                    _context.Abort(ex);
                    throw ex;
                }

                T message;
                try
                {
                    message = _codec.Decode(parse.Frame);
                }
                catch (Exception ex)
                {
                    _context.Abort(ex);
                    throw;
                }

                _context.Reader.AdvanceTo(parse.Consumed, parse.Examined);
                return message;
            }

            if (result.IsCompleted)
            {
                var ex = new InvalidDataException("连接在帧中途结束。");
                _context.Abort(ex);
                throw ex;
            }

            _context.Reader.AdvanceTo(parse.Consumed, parse.Examined);
        }
    }
    finally
    {
        Volatile.Write(ref _reading, 0);
    }
}
```

- [ ] 3.7 实现 `ChannelSession<T>.WriteAsync`

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Session/ChannelSession.cs`

```csharp
using System.IO.Pipelines;

public async ValueTask WriteAsync(T message, CancellationToken ct)
{
    ThrowIfDisposed();
    ct.ThrowIfCancellationRequested();

    await _writeLock.WaitAsync(ct).ConfigureAwait(false);
    try
    {
        ThrowIfDisposed();

        using var buffer = new PooledBufferWriter();
        _codec.Encode(buffer, message);

        if (buffer.WrittenMemory.Length > _maxMessageSize)
            throw new InvalidDataException(
                $"消息长度 {buffer.WrittenMemory.Length} 超过上限 {_maxMessageSize}。");

        try
        {
            _framer.WriteFrame(_context.Writer, buffer.WrittenSequence);
        }
        catch (Exception ex)
        {
            _context.Abort(ex);
            throw;
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

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

- [ ] 3.8 新增会话扩展方法

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Session/ChannelContextSessionExtensions.cs`

```csharp
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>从连接创建消息会话的组合入口。</summary>
    public static class ChannelContextSessionExtensions
    {
        public static IChannelSession<T> AsSession<T>(
            this ChannelContext context,
            IMessageFramer framer,
            IMessageCodec<T> codec,
            MessageChannelOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(framer);
            ArgumentNullException.ThrowIfNull(codec);

            return new ChannelSession<T>(
                context,
                framer,
                codec,
                options ?? new MessageChannelOptions());
        }

        public static IChannelSession<T> AsJsonSession<T>(
            this ChannelContext context,
            LengthFieldFramerOptions? framer = null,
            JsonSerializerOptions? json = null,
            MessageChannelOptions? options = null)
            => context.AsSession(
                new LengthPrefixedFramer(framer),
                new JsonMessageCodec<T>(json),
                options);

        public static IChannelSession<string> AsLineTextSession(
            this ChannelContext context,
            LineFramerOptions? framer = null,
            MessageChannelOptions? options = null)
            => context.AsSession(
                new LineFramer(framer),
                new Utf8StringCodec(),
                options);
    }
}
```

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors；`SessionTests` 可以编译。

## 2. 行为测试

- [ ] 3.9 补半包、粘包与一行多帧测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/SessionTests.cs`

在测试类中增加：

```csharp
private static async Task<TestServerHandle> StartLineEchoAsync(MessageChannelOptions? options = null)
{
    return await TestServer.StartAsync(async context =>
    {
        await using var session = context.AsLineTextSession(options: options);
        while (true)
        {
            string line;
            try
            {
                line = await session.ReadAsync(context.ConnectionClosed);
            }
            catch (EndOfStreamException)
            {
                break;
            }

            await session.WriteAsync(line, context.ConnectionClosed);
        }
    });
}

[Fact]
public async Task LineSession_StickyPackets_AreSplitIntoMessages()
{
    await using var server = await StartLineEchoAsync();
    using var client = await TestClient.ConnectAsync(server.EndPoint);
    var stream = client.GetStream();

    await stream.WriteAsync("a\nb\n"u8.ToArray());

    Assert.Equal("a", await ReadLineAsync(stream));
    Assert.Equal("b", await ReadLineAsync(stream));
}
```

- [ ] 3.10 补干净 EOF 与帧中途 EOF 测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/SessionTests.cs`

```csharp
[Fact]
public async Task LineSession_CleanEof_ThrowsEndOfStream()
{
    var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);

    await using var server = await TestServer.StartAsync(async context =>
    {
        await using var session = context.AsLineTextSession();
        try
        {
            await session.ReadAsync(context.ConnectionClosed);
        }
        catch (Exception ex)
        {
            observed.TrySetResult(ex);
        }
    });

    using var client = await TestClient.ConnectAsync(server.EndPoint);
    client.Client.Shutdown(SocketShutdown.Send);

    Assert.IsType<EndOfStreamException>(await observed.Task.WaitAsync(TestWaits.DefaultTimeout));
}

[Fact]
public async Task LineSession_MidFrameEof_ThrowsInvalidData()
{
    var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);

    await using var server = await TestServer.StartAsync(async context =>
    {
        await using var session = context.AsLineTextSession();
        try
        {
            await session.ReadAsync(context.ConnectionClosed);
        }
        catch (Exception ex)
        {
            observed.TrySetResult(ex);
        }
    });

    using var client = await TestClient.ConnectAsync(server.EndPoint);
    await client.GetStream().WriteAsync("partial"u8.ToArray());
    client.Client.Shutdown(SocketShutdown.Send);

    Assert.IsType<InvalidDataException>(await observed.Task.WaitAsync(TestWaits.DefaultTimeout));
}

[Fact]
public async Task LineSession_ExceedsMaxMessageSize_ThrowsInvalidData()
{
    var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);

    await using var server = await TestServer.StartAsync(async context =>
    {
        await using var session = context.AsLineTextSession(
            options: new MessageChannelOptions { MaxMessageSize = 4 });
        try
        {
            await session.ReadAsync(context.ConnectionClosed);
        }
        catch (Exception ex)
        {
            observed.TrySetResult(ex);
        }
    });

    using var client = await TestClient.ConnectAsync(server.EndPoint);
    await client.GetStream().WriteAsync("12345\n"u8.ToArray());

    Assert.IsType<InvalidDataException>(await observed.Task.WaitAsync(TestWaits.DefaultTimeout));
}
```

- [ ] 3.11 补读取消与并发读测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/SessionTests.cs`

```csharp
[Fact]
public async Task LineSession_ReadCancellation_DoesNotConsume()
{
    var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    await using var server = await TestServer.StartAsync(async context =>
    {
        await using var session = context.AsLineTextSession();

        using var cts = new CancellationTokenSource();
        var first = session.ReadAsync(cts.Token).AsTask();
        cts.Cancel();

        try
        {
            await first;
        }
        catch (OperationCanceledException)
        {
            canceled.TrySetResult();
        }

        var line = await session.ReadAsync(context.ConnectionClosed);
        await session.WriteAsync(line, context.ConnectionClosed);
    });

    using var client = await TestClient.ConnectAsync(server.EndPoint);
    var stream = client.GetStream();

    await canceled.Task.WaitAsync(TestWaits.DefaultTimeout);
    await stream.WriteAsync("ok\n"u8.ToArray());

    Assert.Equal("ok", await ReadLineAsync(stream));
}

[Fact]
public async Task LineSession_ConcurrentRead_Throws()
{
    var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondFailed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);

    await using var server = await TestServer.StartAsync(async context =>
    {
        await using var session = context.AsLineTextSession();

        var first = session.ReadAsync(context.ConnectionClosed).AsTask();
        firstStarted.TrySetResult();

        try
        {
            await session.ReadAsync(context.ConnectionClosed);
        }
        catch (Exception ex)
        {
            secondFailed.TrySetResult(ex);
        }

        var line = await first;
        await session.WriteAsync(line, context.ConnectionClosed);
    });

    using var client = await TestClient.ConnectAsync(server.EndPoint);
    var stream = client.GetStream();

    await firstStarted.Task.WaitAsync(TestWaits.DefaultTimeout);
    Assert.IsType<InvalidOperationException>(
        await secondFailed.Task.WaitAsync(TestWaits.DefaultTimeout));

    await stream.WriteAsync("go\n"u8.ToArray());
    Assert.Equal("go", await ReadLineAsync(stream));
}
```

- [ ] 3.12 补并发写、1 MiB 背压与写上限测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/SessionTests.cs`

```csharp
[Fact]
public async Task LineSession_ConcurrentWrites_AreSerialized()
{
    await using var server = await TestServer.StartAsync(async context =>
    {
        await using var session = context.AsLineTextSession();
        var first = session.WriteAsync("first", context.ConnectionClosed).AsTask();
        var second = session.WriteAsync("second", context.ConnectionClosed).AsTask();
        await Task.WhenAll(first, second);
    });

    using var client = await TestClient.ConnectAsync(server.EndPoint);
    var stream = client.GetStream();

    Assert.Equal("first", await ReadLineAsync(stream));
    Assert.Equal("second", await ReadLineAsync(stream));
}

[Fact]
public async Task LineSession_OneMiB_Backpressure()
{
    await using var server = await StartLineEchoAsync();
    using var client = await TestClient.ConnectAsync(server.EndPoint);
    var stream = client.GetStream();

    var payload = new string('a', 1024 * 1024);
    await stream.WriteAsync(Encoding.UTF8.GetBytes(payload + "\n"));

    Assert.Equal(payload, await ReadLineAsync(stream));
}
```

- [ ] 3.13 补重复 `AsSession` 与 Dispose 所有权测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/SessionTests.cs`

```csharp
[Fact]
public async Task DuplicateAsSession_Throws_AndDisposeDoesNotCloseConnection()
{
    var duplicate = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
    var closedAfterDispose = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    await using var server = await TestServer.StartAsync(async context =>
    {
        var session = context.AsLineTextSession();

        try
        {
            _ = context.AsLineTextSession();
        }
        catch (Exception ex)
        {
            duplicate.TrySetResult(ex);
        }

        await session.DisposeAsync();
        closedAfterDispose.TrySetResult(context.ConnectionClosed.IsCancellationRequested);

        await context.Writer.WriteAsync("open"u8.ToArray());
        await context.Writer.FlushAsync();
    });

    using var client = await TestClient.ConnectAsync(server.EndPoint);

    Assert.IsType<InvalidOperationException>(
        await duplicate.Task.WaitAsync(TestWaits.DefaultTimeout));
    Assert.False(await closedAfterDispose.Task.WaitAsync(TestWaits.DefaultTimeout));

    var received = await TestClient.ReadExactlyAsync(client.GetStream(), 4);
    Assert.Equal("open", Encoding.UTF8.GetString(received));
}
```

- [ ] 3.14 补 JSON 会话往返测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/SessionTests.cs`

**Step 1: 增加长度前缀读写辅助方法**

```csharp
private static async Task WriteLengthPrefixedAsync(NetworkStream stream, byte[] payload)
{
    var header = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);
    await stream.WriteAsync(header);
    await stream.WriteAsync(payload);
}

private static async Task<byte[]> ReadLengthPrefixedAsync(NetworkStream stream)
{
    var header = await TestClient.ReadExactlyAsync(stream, 4);
    var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header));
    return await TestClient.ReadExactlyAsync(stream, length);
}
```

**Step 2: 增加往返测试**

```csharp
[Fact]
public async Task JsonSession_RoundTrip()
{
    await using var server = await TestServer.StartAsync(async context =>
    {
        await using var session = context.AsJsonSession<TestMessage>();
        var message = await session.ReadAsync(context.ConnectionClosed);
        await session.WriteAsync(message, context.ConnectionClosed);
    });

    using var client = await TestClient.ConnectAsync(server.EndPoint);
    var stream = client.GetStream();

    await WriteLengthPrefixedAsync(
        stream,
        JsonSerializer.SerializeToUtf8Bytes(new TestMessage(1, "hello")));

    var response = await ReadLengthPrefixedAsync(stream);
    var decoded = JsonSerializer.Deserialize<TestMessage>(response);

    Assert.Equal(new TestMessage(1, "hello"), decoded);
}
```

**Step 3: 补 using**

文件顶部需要 `using System.Buffers.Binary;`、`using System.Text.Json;`。

- [ ] 3.15 运行 focused session 测试

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~SessionTests`
Expected: 全部 `SessionTests` 通过。

- [ ] 3.16 更新 Primitives README 的会话章节

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/README.md`

在“## 消息编解码”之后增加：

````markdown
## 消息会话

`IChannelSession<T>` 是每连接一个的拉模型会话；只在终端 handler 创建，不放在
`IChannelPipeline.Use` 中间件里。

```csharp
static async Task HandleAsync(ChannelContext context)
{
    await using var session = context.AsLineTextSession();

    while (true)
    {
        string line;
        try
        {
            line = await session.ReadAsync(context.ConnectionClosed);
        }
        catch (EndOfStreamException)
        {
            break;
        }

        await session.WriteAsync(line, context.ConnectionClosed);
    }
}
```

- 一个连接只能创建一个 session；第二次 `AsSession`/`AsLineTextSession` 抛 `InvalidOperationException`。
- `DisposeAsync` 只释放 session 自己，不关闭连接；连接由 `ChannelServer` 收尾。
- `ReadAsync` 并发调用直接抛错；读取消不会消费缓冲区，下一次可以继续。
- `WriteAsync` 用信号量串行化；写取消可能已写出半个帧，会中止连接。
- `MessageChannelOptions.MaxMessageSize` 默认 1 MiB。
- `AsJsonSession<T>()` = 长度前缀 + JSON；`AsLineTextSession()` = 换行 + UTF-8。
````

- [ ] 3.17 全量构建、测试与 OpenSpec 校验

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
Expected: 现有测试 + `FramingTests` + `MessageCodecTests` + `SessionTests` 全部通过。

Run: `openspec validate "primitives-message-session" --strict --no-interactive`
Expected: `Change 'primitives-message-session' is valid`。
