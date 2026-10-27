# P5-1 NamedPipe 传输 Implementation Plan

> **REQUIRED SUB-SKILL:** 使用 `executing-plans` 或 `opsx-apply-x` 逐任务执行本计划。
> 本仓库虽有 git，但按约定不自动提交；步骤中的“验证”统一用构建 + 测试，不执行 git commit。

**Goal:** 在 `ChannelServerCore` 上增加 internal listener 工厂缝与 NamedPipe 服务端实现，保持 `IChannelServer` / `ChannelContext` 契约、每连接 scope、`MaxConnections`、优雅停机与取消语义不变。

**Architecture:** `ChannelServerCore` 只依赖 internal `IChannelListenerFactory`；`SocketChannelListenerFactory` 包装现有 TCP listener，`NamedPipeChannelListenerFactory` 创建 `NamedPipeChannelListener`。NamedPipe 连接用 `NamedPipeServerStream` + `PipeReader.Create` / `PipeWriter.Create` 组合出 `IDuplexPipe`，再交给现有 `ChannelContext`。公共配置只暴露 `ChannelTransportKind`、`NamedPipeEndPoint` 与 `ChannelServerOptions.Transport`。

**Tech Stack:** .NET 10、`System.IO.Pipes`、`System.IO.Pipelines`、`System.Net.Sockets`、xunit 2.9.3、Microsoft.Extensions.DependencyInjection/Logging Abstractions。

---

## 1. 公共配置表面

- [ ] 1.1 增加 `ChannelTransportKind` 与 `NamedPipeEndPoint`

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Options/ChannelTransportKind.cs`
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/NamedPipeEndPoint.cs`

**Step 1: 写入枚举**

```csharp
namespace Microsoft.Extensions.Options
{
    /// <summary>服务端传输类型。</summary>
    public enum ChannelTransportKind
    {
        /// <summary>TCP socket，默认值。</summary>
        Tcp = 0,

        /// <summary>Windows 命名管道。</summary>
        NamedPipe = 1,
    }
}
```

**Step 2: 写入端点类型**

```csharp
using System.Net;
using System.Net.Sockets;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>Windows 命名管道端点。</summary>
    public sealed class NamedPipeEndPoint : EndPoint
    {
        /// <summary>创建命名管道端点。</summary>
        public NamedPipeEndPoint(string pipeName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

            if (pipeName.Length > 256)
                throw new ArgumentException("管道名不能超过 256 个字符。", nameof(pipeName));

            if (pipeName.IndexOfAny(['\\', '/']) >= 0)
                throw new ArgumentException("管道名不能包含路径分隔符。", nameof(pipeName));

            PipeName = pipeName;
        }

        /// <summary>管道名（不含服务器名前缀）。</summary>
        public string PipeName { get; }

        /// <inheritdoc />
        public override AddressFamily AddressFamily => AddressFamily.Unknown;

        /// <inheritdoc />
        public override string ToString() => $"pipe:{PipeName}";

        /// <inheritdoc />
        public override bool Equals(object? obj)
            => obj is NamedPipeEndPoint other
                && string.Equals(PipeName, other.PipeName, StringComparison.Ordinal);

        /// <inheritdoc />
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(PipeName);
    }
}
```

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

- [ ] 1.2 给 `ChannelServerOptions` 增加 `Transport` 与启动前校验

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Options/ChannelServerOptions.cs`

**Step 1: 增加属性**

```csharp
/// <summary>传输类型，默认 TCP。</summary>
public ChannelTransportKind Transport { get; set; } = ChannelTransportKind.Tcp;
```

**Step 2: 在 `Validate()` 的 `EndPoint` 判空之后加入传输校验**

```csharp
switch (Transport)
{
    case ChannelTransportKind.Tcp when EndPoint is NamedPipeEndPoint:
        throw new ArgumentException("TCP 传输不能使用 NamedPipeEndPoint。", nameof(EndPoint));

    case ChannelTransportKind.NamedPipe when EndPoint is not NamedPipeEndPoint:
        throw new ArgumentException("NamedPipe 传输必须使用 NamedPipeEndPoint。", nameof(EndPoint));

    case ChannelTransportKind.NamedPipe when CreateBoundListenSocket is not null:
        throw new ArgumentException(
            "NamedPipe 传输不支持 CreateBoundListenSocket。",
            nameof(CreateBoundListenSocket));

    case ChannelTransportKind.Tcp:
    case ChannelTransportKind.NamedPipe:
        break;

    default:
        throw new ArgumentOutOfRangeException(
            nameof(Transport),
            Transport,
            "不支持的传输类型。");
}
```

文件顶部补 `using Microsoft.Extensions.Hosting.Net;`。`Clone()` 使用 `MemberwiseClone`，无需额外复制枚举值；确认 `Transport` 被保留。

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors；现有 TCP options 测试仍通过。

## 2. Listener 工厂缝

- [ ] 2.1 增加 internal `IChannelListenerFactory` 与解析器

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Transports/IChannelListenerFactory.cs`
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Transports/SocketChannelListenerFactory.cs`

**Step 1: 契约**

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting.Net
{
    internal interface IChannelListenerFactory
    {
        ValueTask<IChannelListener> BindAsync(
            ChannelServerOptions options,
            ILoggerFactory? loggerFactory,
            CancellationToken ct);
    }
}
```

**Step 2: 包装现有 TCP listener**

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting.Net
{
    internal sealed class SocketChannelListenerFactory : IChannelListenerFactory
    {
        public static SocketChannelListenerFactory Instance { get; } = new();

        public async ValueTask<IChannelListener> BindAsync(
            ChannelServerOptions options,
            ILoggerFactory? loggerFactory,
            CancellationToken ct)
        {
            return await SocketChannelListener
                .BindAsync(options, loggerFactory, ct)
                .ConfigureAwait(false);
        }
    }
}
```

此时 `NamedPipeChannelListenerFactory` 尚不存在，先创建占位实现并抛 `NotSupportedException` 会导致后续任务返工；改为本任务只创建接口与 TCP 工厂，`ChannelListenerFactory` 的 NamedPipe 分支在 4.1 完成后接入。若编译器要求完整 switch，可暂时将 NamedPipe 分支指向 `SocketChannelListenerFactory.Instance`，并在 4.1 替换。

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

## 3. NamedPipe 连接

- [ ] 3.1 创建 `NamedPipeChannelConnection` 的属性与 Pipe 组合

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Transports/NamedPipeChannelConnection.cs`

**Step 1: 写入连接骨架**

```csharp
using System.Buffers;
using System.IO.Pipes;
using System.IO.Pipelines;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting.Net
{
    internal sealed class NamedPipeChannelConnection : IChannelConnection
    {
        private readonly NamedPipeServerStream _stream;
        private readonly PipeReader _reader;
        private readonly PipeWriter _writer;
        private readonly IDuplexPipe _transport;
        private readonly TaskCompletionSource<Exception?> _termination =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public NamedPipeChannelConnection(
            NamedPipeServerStream stream,
            string pipeName,
            ChannelServerOptions options,
            ILogger? logger)
        {
            _stream = stream;
            LocalEndPoint = new NamedPipeEndPoint(pipeName);
            RemoteEndPoint = null;

            var readBufferSize = (int)Math.Min(
                options.MaxReadBufferSize ?? 1024 * 1024,
                int.MaxValue);

            _reader = PipeReader.Create(stream, new StreamPipeReaderOptions(
                pool: MemoryPool<byte>.Shared,
                bufferSize: readBufferSize,
                minimumReadSize: 4096,
                leaveOpen: true));

            _writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(
                pool: MemoryPool<byte>.Shared,
                minimumBufferSize: 4096,
                leaveOpen: true));

            _transport = new DuplexPipe(_reader, _writer);
        }

        public EndPoint? LocalEndPoint { get; }

        public EndPoint? RemoteEndPoint { get; }

        public IDuplexPipe Transport => _transport;

        public Task<Exception?> Termination => _termination.Task;
    }
}
```

`logger` 参数暂时保存到字段或用于后续 debug 日志；不要在构造函数中读取业务数据。

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors（`IChannelConnection` 还缺 `Abort`/`DisposeAsync` 时会有错误，下一步补齐；若先报缺成员，继续 3.2 后再构建）。

- [ ] 3.2 实现 `Abort` 与 `DisposeAsync`

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Transports/NamedPipeChannelConnection.cs`

**Step 1: 增加幂等字段**

```csharp
private int _aborted;
private int _disposed;
```

**Step 2: 实现中止与释放**

```csharp
public void Abort(Exception? reason)
{
    if (Interlocked.Exchange(ref _aborted, 1) != 0)
        return;

    _termination.TrySetResult(reason);

    try
    {
        _reader.CancelPendingRead();
        _writer.CancelPendingFlush();
    }
    catch (Exception)
    {
        // 管道已经关闭时取消 pending 操作可能抛异常，忽略。
    }

    try
    {
        _stream.Dispose();
    }
    catch (Exception)
    {
    }
}

public async ValueTask DisposeAsync()
{
    if (Interlocked.Exchange(ref _disposed, 1) != 0)
    {
        await _termination.Task.ConfigureAwait(false);
        return;
    }

    try
    {
        if (Volatile.Read(ref _aborted) == 0)
        {
            try
            {
                await _writer.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            try
            {
                await _reader.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        await _stream.DisposeAsync().ConfigureAwait(false);
        _termination.TrySetResult(null);
    }
    finally
    {
        // 不在这里释放 _termination；ChannelContext 仍会读取 Termination。
    }
}
```

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

## 4. NamedPipe 监听器

- [ ] 4.1 实现 `NamedPipeChannelListener.BindAsync` 与 pending 实例

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Transports/NamedPipeChannelListener.cs`
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Transports/NamedPipeChannelListenerFactory.cs`
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Transports/ChannelListenerFactory.cs`

**Step 1: 写入 listener 骨架**

```csharp
using System.IO.Pipes;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting.Net
{
    internal sealed class NamedPipeChannelListener : IChannelListener
    {
        private const int InitialPendingInstances = 2;

        private readonly string _pipeName;
        private readonly ChannelServerOptions _options;
        private readonly ILogger? _logger;
        private readonly Queue<NamedPipeServerStream> _pending = new();
        private readonly object _gate = new();
        private int _unbound;

        private NamedPipeChannelListener(
            string pipeName,
            ChannelServerOptions options,
            ILogger? logger)
        {
            _pipeName = pipeName;
            _options = options;
            _logger = logger;
            LocalEndPoint = new NamedPipeEndPoint(pipeName);
        }

        public EndPoint? LocalEndPoint { get; }

        public static ValueTask<NamedPipeChannelListener> BindAsync(
            ChannelServerOptions options,
            ILoggerFactory? loggerFactory,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var endpoint = (NamedPipeEndPoint)options.EndPoint;
            var listener = new NamedPipeChannelListener(
                endpoint.PipeName,
                options,
                loggerFactory?.CreateLogger<NamedPipeChannelListener>());

            try
            {
                listener.EnsurePending(InitialPendingInstances);
                return ValueTask.FromResult(listener);
            }
            catch
            {
                listener.DisposePending();
                throw;
            }
        }

        private NamedPipeServerStream CreateServerStream()
            => new(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

        private void EnsurePending(int count)
        {
            lock (_gate)
            {
                while (_pending.Count < count)
                    _pending.Enqueue(CreateServerStream());
            }
        }

        private void DisposePending()
        {
            lock (_gate)
            {
                while (_pending.Count > 0)
                    _pending.Dequeue().Dispose();
            }
        }
    }
}
```

**Step 2: 增加 NamedPipe 工厂与解析器**

```csharp
internal sealed class NamedPipeChannelListenerFactory : IChannelListenerFactory
{
    public static NamedPipeChannelListenerFactory Instance { get; } = new();

    public async ValueTask<IChannelListener> BindAsync(
        ChannelServerOptions options,
        ILoggerFactory? loggerFactory,
        CancellationToken ct)
    {
        return await NamedPipeChannelListener
            .BindAsync(options, loggerFactory, ct)
            .ConfigureAwait(false);
    }
}
```

```csharp
internal static class ChannelListenerFactory
{
    public static IChannelListenerFactory Create(ChannelServerOptions options)
        => options.Transport switch
        {
            ChannelTransportKind.Tcp => SocketChannelListenerFactory.Instance,
            ChannelTransportKind.NamedPipe => NamedPipeChannelListenerFactory.Instance,
            _ => throw new ArgumentOutOfRangeException(
                nameof(options),
                options.Transport,
                "不支持的传输类型。"),
        };
}
```

`ChannelListenerFactory.cs` 还需要 `using Microsoft.Extensions.Options;`。

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors（`AcceptAsync`/`UnbindAsync` 尚未实现时继续 4.2）。

- [ ] 4.2 实现 `AcceptAsync`、`UnbindAsync` 与 `DisposeAsync`

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Transports/NamedPipeChannelListener.cs`

**Step 1: 实现 accept**

```csharp
public async ValueTask<IChannelConnection?> AcceptAsync(CancellationToken ct)
{
    NamedPipeServerStream stream;

    lock (_gate)
    {
        if (Volatile.Read(ref _unbound) != 0)
            return null;

        if (_pending.Count == 0)
            EnsurePending(1);

        stream = _pending.Dequeue();
    }

    try
    {
        await stream.WaitForConnectionAsync(ct).ConfigureAwait(false);
    }
    catch
    {
        stream.Dispose();
        throw;
    }

    lock (_gate)
    {
        if (Volatile.Read(ref _unbound) == 0)
        {
            try
            {
                EnsurePending(1);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "创建下一个命名管道实例失败，将在下次 accept 时重试。");
            }
        }
    }

    return new NamedPipeChannelConnection(stream, _pipeName, _options, _logger);
}
```

**Step 2: 实现解绑与释放**

```csharp
public ValueTask UnbindAsync(CancellationToken ct)
{
    if (Interlocked.Exchange(ref _unbound, 1) == 0)
        DisposePending();

    return ValueTask.CompletedTask;
}

public ValueTask DisposeAsync()
{
    UnbindAsync(CancellationToken.None);
    return ValueTask.CompletedTask;
}
```

`EnsurePending` 在持有 `_gate` 时被调用；如果把它改成 `lock` 内部再调用，确保没有重复加锁。`DisposePending` 同样在锁内执行。

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

- [ ] 4.3 让 `ChannelServerCore` 通过工厂绑定 listener

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/ChannelServerCore.cs`

**Step 1: 增加字段并在构造函数解析**

```csharp
private readonly IChannelListenerFactory _listenerFactory;

public ChannelServerCore(
    ChannelServerOptions options,
    Action<IChannelPipeline> configurePipeline,
    ILoggerFactory? loggerFactory,
    IServiceScopeFactory? scopeFactory)
{
    // 保留现有校验与 options.Clone()
    _listenerFactory = ChannelListenerFactory.Create(_options);
}
```

**Step 2: 替换 `StartAsync` 中的直接绑定**

```csharp
var listener = await _listenerFactory
    .BindAsync(_options, LoggerFactory, ct)
    .ConfigureAwait(false);
```

不要改变 `LocalEndPoint` 赋值、`_stopping` 创建或 accept loop 启动顺序。

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~ServerLifecycleTests`
Expected: TCP 生命周期测试全部通过。

## 5. NamedPipe 测试

- [ ] 5.1 增加唯一管道名/客户端辅助与回显测试

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/NamedPipeTestSupport.cs`
- Create: `tests/Galosys.Foundation.Primitives.Tests/NamedPipeTransportTests.cs`

**Step 1: 测试辅助**

```csharp
using System.IO.Pipes;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

internal static class NamedPipeTestSupport
{
    public static string CreatePipeName()
        => "gf-primitives-" + Guid.NewGuid().ToString("N");

    public static async Task<NamedPipeClientStream> ConnectAsync(
        string pipeName,
        CancellationToken ct = default)
    {
        var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            await client.ConnectAsync(
                (int)TestWaits.DefaultTimeout.TotalMilliseconds,
                ct);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public static async Task<byte[]> ReadExactlyAsync(
        Stream stream,
        int count,
        CancellationToken ct = default)
    {
        var buffer = new byte[count];
        var offset = 0;

        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0)
                throw new IOException("命名管道在读取完成前关闭。");

            offset += read;
        }

        return buffer;
    }

    public static ChannelServer CreateServer(
        string pipeName,
        Func<ChannelContext, Task> handler,
        Action<ChannelServerOptions>? configure = null)
    {
        var options = new ChannelServerOptions
        {
            Transport = ChannelTransportKind.NamedPipe,
            EndPoint = new NamedPipeEndPoint(pipeName),
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        };

        configure?.Invoke(options);
        return new ChannelServer(options, pipeline => pipeline.Run(handler));
    }

    public static async Task HoldUntilClosedAsync(ChannelContext context)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, context.ConnectionClosed);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public static async Task AssertClosedAsync(
        Stream stream,
        CancellationToken ct = default)
    {
        try
        {
            var read = await stream.ReadAsync(new byte[1], ct);
            Assert.Equal(0, read);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }
}
```

**Step 2: 回显测试**

```csharp
using System.Text;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class NamedPipeTransportTests
{
    [Fact]
    public async Task StartAsync_BindsAndEchoes()
    {
        var pipeName = NamedPipeTestSupport.CreatePipeName();
        var server = NamedPipeTestSupport.CreateServer(pipeName, TestHandlers.EchoAsync);

        await server.StartAsync(CancellationToken.None);

        try
        {
            await using var client = await NamedPipeTestSupport.ConnectAsync(pipeName);

            await client.WriteAsync("ping"u8.ToArray());
            var echoed = await NamedPipeTestSupport.ReadExactlyAsync(client, 4);

            Assert.Equal("ping", Encoding.UTF8.GetString(echoed));
            var endpoint = Assert.IsType<NamedPipeEndPoint>(server.LocalEndPoint);
            Assert.Equal(pipeName, endpoint.PipeName);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~NamedPipeTransportTests`
Expected: `StartAsync_BindsAndEchoes` 通过。

- [ ] 5.2 增加两个并发客户端与 `MaxConnections` 测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/NamedPipeTransportTests.cs`

**Step 1: 两个并发客户端**

```csharp
[Fact]
public async Task TwoClients_ConnectConcurrently_AndEchoIndependently()
{
    var pipeName = NamedPipeTestSupport.CreatePipeName();
    var server = NamedPipeTestSupport.CreateServer(pipeName, TestHandlers.EchoAsync);
    await server.StartAsync(CancellationToken.None);

    try
    {
        await using var first = await NamedPipeTestSupport.ConnectAsync(pipeName);
        await using var second = await NamedPipeTestSupport.ConnectAsync(pipeName);

        await first.WriteAsync("alpha"u8.ToArray());
        await second.WriteAsync("bravo"u8.ToArray());

        var firstReply = await NamedPipeTestSupport.ReadExactlyAsync(first, 5);
        var secondReply = await NamedPipeTestSupport.ReadExactlyAsync(second, 5);

        Assert.Equal("alpha", Encoding.UTF8.GetString(firstReply));
        Assert.Equal("bravo", Encoding.UTF8.GetString(secondReply));
    }
    finally
    {
        await server.DisposeAsync();
    }
}
```

**Step 2: 最大连接数**

```csharp
[Fact]
public async Task MaxConnections_RejectsSecondNamedPipeClient()
{
    var pipeName = NamedPipeTestSupport.CreatePipeName();
    var server = NamedPipeTestSupport.CreateServer(
        pipeName,
        NamedPipeTestSupport.HoldUntilClosedAsync,
        options => options.MaxConnections = 1);

    await server.StartAsync(CancellationToken.None);

    try
    {
        await using var first = await NamedPipeTestSupport.ConnectAsync(pipeName);
        await using var second = await NamedPipeTestSupport.ConnectAsync(pipeName);

        await second.WriteAsync("x"u8.ToArray());
        var read = await second.ReadAsync(new byte[1]).AsTask().WaitAsync(TestWaits.DefaultTimeout);
        Assert.Equal(0, read);
    }
    finally
    {
        await server.DisposeAsync();
    }
}
```

`HoldUntilClosedAsync` 只等待 `context.ConnectionClosed`；`DisposeAsync` 会中止连接并释放它。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~NamedPipeTransportTests`
Expected: 两个新测试与回显测试全部通过。

- [ ] 5.3 增加管道名占用测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/NamedPipeTransportTests.cs`

**Step 1: 用单实例 blocker 占用管道名**

```csharp
[Fact]
public async Task StartAsync_Throws_WhenPipeNameIsInUse()
{
    var pipeName = NamedPipeTestSupport.CreatePipeName();

    using var blocker = new NamedPipeServerStream(
        pipeName,
        PipeDirection.InOut,
        maxNumberOfServerInstances: 1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous);

    var server = NamedPipeTestSupport.CreateServer(pipeName, TestHandlers.EchoAsync);

    var exception = await Record.ExceptionAsync(
        () => server.StartAsync(CancellationToken.None));

    Assert.NotNull(exception);
    Assert.True(
        exception is IOException or UnauthorizedAccessException,
        $"期望 IOException 或 UnauthorizedAccessException，实际为 {exception.GetType().FullName}。");
    Assert.Null(server.LocalEndPoint);

    await server.DisposeAsync();
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~StartAsync_Throws_WhenPipeNameIsInUse`
Expected: 测试通过；启动失败后 `LocalEndPoint` 为 null。

- [ ] 5.4 增加优雅停机与取消测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/NamedPipeTransportTests.cs`

**Step 1: 优雅停机**

```csharp
[Fact]
public async Task StopAsync_StopsAcceptingAndAbortsAfterShutdownTimeout()
{
    var pipeName = NamedPipeTestSupport.CreatePipeName();
    var server = NamedPipeTestSupport.CreateServer(
        pipeName,
        NamedPipeTestSupport.HoldUntilClosedAsync,
        options => options.ShutdownTimeout = TimeSpan.FromMilliseconds(200));

    await server.StartAsync(CancellationToken.None);

    try
    {
        await using var client = await NamedPipeTestSupport.ConnectAsync(pipeName);

        await server.StopAsync(CancellationToken.None).WaitAsync(TestWaits.DefaultTimeout);

        Assert.Null(server.LocalEndPoint);
        await NamedPipeTestSupport.AssertClosedAsync(client);
    }
    finally
    {
        await server.DisposeAsync();
    }
}
```

`HoldUntilClosedAsync` 只等待 `context.ConnectionClosed`；停机超时后 `ChannelContext.Abort` 会取消该令牌并让处理返回，因此 `StopAsync` 不会无限等待。

**Step 2: RunAsync 取消**

```csharp
[Fact]
public async Task RunAsync_Cancel_StopsServer()
{
    var pipeName = NamedPipeTestSupport.CreatePipeName();
    var server = NamedPipeTestSupport.CreateServer(pipeName, TestHandlers.EchoAsync);
    using var cts = new CancellationTokenSource();

    var runTask = server.RunAsync(cts.Token);
    await TestClient.WaitUntilAsync(
        () => Task.FromResult(server.LocalEndPoint is not null),
        "NamedPipe 服务器未在超时时间内启动。");

    cts.Cancel();
    await runTask.WaitAsync(TestWaits.DefaultTimeout);

    Assert.Null(server.LocalEndPoint);
    await server.DisposeAsync();
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~NamedPipeTransportTests`
Expected: 停机与取消测试通过，且不会留下后台 accept loop。

- [ ] 5.5 增加传输选择/校验测试与 internal 类型断言

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/NamedPipeTransportTests.cs`
- Modify: `tests/Galosys.Foundation.Primitives.Tests/NamespaceConventionTests.cs`

**Step 1: 选择与校验测试**

```csharp
[Fact]
public void Options_DefaultTransport_IsTcp()
{
    var options = new ChannelServerOptions
    {
        EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
    };

    Assert.Equal(ChannelTransportKind.Tcp, options.Transport);
}

[Fact]
public async Task StartAsync_Throws_WhenTransportAndEndpointMismatch()
{
    var namedPipeWithTcp = new ChannelServer(
        new ChannelServerOptions
        {
            Transport = ChannelTransportKind.NamedPipe,
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        },
        pipeline => pipeline.Run(TestHandlers.EchoAsync));

    await Assert.ThrowsAsync<ArgumentException>(
        () => namedPipeWithTcp.StartAsync(CancellationToken.None));
    Assert.Null(namedPipeWithTcp.LocalEndPoint);
    await namedPipeWithTcp.DisposeAsync();
}

[Fact]
public async Task StartAsync_Throws_WhenTransportIsUndefined()
{
    var server = new ChannelServer(
        new ChannelServerOptions
        {
            Transport = (ChannelTransportKind)99,
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        },
        pipeline => pipeline.Run(TestHandlers.EchoAsync));

    await Assert.ThrowsAnyAsync<ArgumentException>(
        () => server.StartAsync(CancellationToken.None));
    await server.DisposeAsync();
}

[Fact]
public void NamedPipeEndPoint_RejectsInvalidName()
{
    Assert.Throws<ArgumentException>(() => new NamedPipeEndPoint(""));
    Assert.Throws<ArgumentException>(() => new NamedPipeEndPoint("bad/name"));
    Assert.Throws<ArgumentException>(() => new NamedPipeEndPoint("bad\\name"));
}
```

**Step 2: internal 类型断言**

```csharp
[Fact]
public void TransportContracts_AreInternal()
{
    var assembly = typeof(ChannelServer).Assembly;

    foreach (var name in new[]
    {
        "Microsoft.Extensions.Hosting.Net.IChannelListener",
        "Microsoft.Extensions.Hosting.Net.IChannelConnection",
        "Microsoft.Extensions.Hosting.Net.IChannelListenerFactory",
        "Microsoft.Extensions.Hosting.Net.NamedPipeChannelListener",
        "Microsoft.Extensions.Hosting.Net.NamedPipeChannelConnection",
    })
    {
        var type = assembly.GetType(name);
        Assert.NotNull(type);
        Assert.False(type.IsPublic);
        Assert.False(type.IsNestedPublic);
    }
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~NamedPipeTransportTests|FullyQualifiedName~NamespaceConventionTests"`
Expected: 全部通过；`IChannelListenerFactory` 等类型不是 public。

## 6. 文档与验证

- [ ] 6.1 更新 Primitives README 的 NamedPipe 用法

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/README.md`

在“配置”章节后增加：

```markdown
## 命名管道（Windows）

```csharp
var server = new ChannelServer(
    new ChannelServerOptions
    {
        Transport = ChannelTransportKind.NamedPipe,
        EndPoint = new NamedPipeEndPoint("galosys-primitives-demo"),
        ShutdownTimeout = TimeSpan.FromSeconds(10),
    },
    pipeline => pipeline.Run(HandleAsync));

await server.StartAsync(cancellationToken);
```

客户端：

```csharp
await using var client = new NamedPipeClientStream(
    ".",
    "galosys-primitives-demo",
    PipeDirection.InOut,
    PipeOptions.Asynchronous);

await client.ConnectAsync(cancellationToken);
```

- 仅 Windows 验证；管道名是机器级命名空间，生产环境必须使用唯一名称。
- `NamedPipeEndPoint` 只接受管道名，不接受 `\` 或 `/`。
- `RemoteEndPoint` 为 null；Windows 命名管道不暴露对端地址。
- `Backlog`、`NoDelay`、`KeepAlive`、`LingerState`、`ReuseAddress`、`DualMode`、`CreateBoundListenSocket` 仅对 TCP 生效，NamedPipe 下被忽略。
- 需要两个客户端并发连接时，listener 会预创建两个 pending 实例；无需调用方额外配置。
```

Run: `Select-String -Path src/Galosys.Foundation.Primitives/README.md -Pattern "命名管道"`
Expected: 能匹配到新增章节标题。

- [ ] 6.2 构建、跑全量测试并校验 OpenSpec

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
Expected: 既有测试与全部 `NamedPipeTransportTests` 通过。

Run: `openspec validate "primitives-namedpipe" --strict --no-interactive`
Expected: `Change 'primitives-namedpipe' is valid`。

Run: `openspec status --change "primitives-namedpipe" --json`
Expected: `proposal`、`design`、`specs`、`tasks` 四个 artifact 均为 `done`。
