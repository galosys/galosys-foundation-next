# P4-1 单连接客户端 Implementation Plan

> **REQUIRED SUB-SKILL:** 使用 `executing-plans` 或 `opsx-apply-x` 逐任务执行本计划。
> 本仓库虽有 git，但按约定不自动提交；步骤中的“验证”统一用构建 + 测试，不执行 git commit。

**Goal:** 新增 `IChannelClient` / `ChannelClient` / `ChannelClientOptions` 与连接状态事件，用真实 loopback socket 锁定明文与 TLS 连接、并发幂等、失败分类、关闭终态和旧连接失效；本阶段不实现重连、连接池和 batch。

**Architecture:** `ChannelClient` 创建 `Socket`，连接成功后复用 internal `SocketChannelConnection` 与 `ChannelContext`；配置 TLS 时先用 `TlsDuplexPipe` 完成客户端握手，再通过 `context.UseTransport(tls)` 把解密管道暴露为 `Connection`。状态与连接字段由单个锁保护，`ConnectAsync` 合并并发尝试，`CloseAsync` / `DisposeAsync` 走同一条终态收尾路径。

**Tech Stack:** .NET 10、`System.Net.Sockets`、`System.IO.Pipelines`、`System.Net.Security`、Microsoft.Extensions.Options、xunit 2.9.3。

---

## 1. 契约、状态与配置

- [ ] 1.1 写 `ChannelClientOptions` 失败测试

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/ChannelClientOptionsTests.cs`

```csharp
using System.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class ChannelClientOptionsTests
{
    [Fact]
    public void Defaults_UseTenSecondTimeoutAndSystemTime()
    {
        var options = new ChannelClientOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 12345),
        };

        Assert.Equal(TimeSpan.FromSeconds(10), options.ConnectTimeout);
        Assert.Same(TimeProvider.System, options.TimeProvider);
        Assert.Null(options.KeepAlive);
        Assert.Null(options.Tls);
    }

    [Fact]
    public void Validate_Throws_WhenEndPointMissing()
    {
        var options = new ChannelClientOptions();

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void Validate_Throws_WhenConnectTimeoutInvalid()
    {
        var options = new ChannelClientOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 12345),
            ConnectTimeout = TimeSpan.Zero,
        };

        Assert.Throws<ArgumentException>(() => options.Validate());
    }
}
```

**Step 2: 运行测试，确认编译失败**

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelClientOptionsTests"`
Expected: 编译失败，提示 `ChannelClientOptions` 不存在（这是本任务的“红”）。

- [ ] 1.2 新增 `ChannelClientOptions`

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Options/ChannelClientOptions.cs`

```csharp
using System.Net;

namespace Microsoft.Extensions.Options
{
    /// <summary>客户端连接配置。</summary>
    public sealed class ChannelClientOptions
    {
        /// <summary>目标端点。必须设置。</summary>
        public EndPoint EndPoint { get; set; } = default!;

        /// <summary>TCP 连接与可选 TLS 握手的超时，默认 10 秒。</summary>
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>TCP keep-alive 配置；null 表示使用系统默认。</summary>
        public ChannelKeepAliveOptions? KeepAlive { get; set; }

        /// <summary>客户端 TLS 配置；null 表示明文连接。</summary>
        public ChannelTlsOptions? Tls { get; set; }

        /// <summary>时间源；重连与超时测试可注入假时钟。</summary>
        public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

        internal void Validate()
        {
            if (EndPoint is null)
                throw new ArgumentException("必须提供目标端点 EndPoint。", nameof(EndPoint));

            if (ConnectTimeout <= TimeSpan.Zero || ConnectTimeout > TimeSpan.FromDays(1))
                throw new ArgumentException("ConnectTimeout 必须大于 0 且不超过 1 天。", nameof(ConnectTimeout));

            if (TimeProvider is null)
                throw new ArgumentException("TimeProvider 不能为空。", nameof(TimeProvider));

            if (KeepAlive is { Enabled: true } keepAlive)
            {
                if (keepAlive.KeepAliveTime < TimeSpan.FromSeconds(1))
                    throw new ArgumentException("KeepAliveTime 必须至少为 1 秒。", nameof(KeepAlive));

                if (keepAlive.KeepAliveInterval < TimeSpan.FromSeconds(1))
                    throw new ArgumentException("KeepAliveInterval 必须至少为 1 秒。", nameof(KeepAlive));

                if (keepAlive.KeepAliveRetryCount < 0)
                    throw new ArgumentException("KeepAliveRetryCount 不能为负数。", nameof(KeepAlive));
            }

            Tls?.ValidateClient();
        }

        internal ChannelClientOptions Clone()
        {
            var clone = (ChannelClientOptions)MemberwiseClone();

            if (KeepAlive is not null)
            {
                clone.KeepAlive = new ChannelKeepAliveOptions
                {
                    Enabled = KeepAlive.Enabled,
                    KeepAliveTime = KeepAlive.KeepAliveTime,
                    KeepAliveInterval = KeepAlive.KeepAliveInterval,
                    KeepAliveRetryCount = KeepAlive.KeepAliveRetryCount,
                };
            }

            // ChannelTlsOptions 含证书与回调，不深拷贝；调用方不得在连接期间修改同一实例。
            return clone;
        }
    }
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelClientOptionsTests"`
Expected: 三个测试通过。

- [ ] 1.3 新增状态、事件、失败分类与 `IChannelClient` 契约

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Client/ChannelConnectionState.cs`
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Client/ChannelConnectionFailure.cs`
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Client/ChannelStateChangedEventArgs.cs`
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Client/IChannelClient.cs`

`ChannelConnectionState.cs`：

```csharp
namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>客户端连接状态。</summary>
    public enum ChannelConnectionState
    {
        /// <summary>未连接。</summary>
        Disconnected = 0,

        /// <summary>正在建立连接。</summary>
        Connecting = 1,

        /// <summary>已连接且可读写字节。</summary>
        Connected = 2,

        /// <summary>正在重连；P4-2 使用。</summary>
        Reconnecting = 3,

        /// <summary>终态，不可再连接。</summary>
        Closed = 4,
    }
}
```

`ChannelConnectionFailure.cs`：

```csharp
using System.Net.Sockets;
using System.Security.Authentication;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>连接失败分类。</summary>
    public enum ChannelConnectionFailureKind
    {
        ConnectionRefused,
        TimedOut,
        HostUnreachable,
        Network,
        Authentication,
        Canceled,
        Configuration,
        Unknown,
    }

    /// <summary>一次连接失败的分类结果。</summary>
    public sealed class ChannelConnectionFailure
    {
        public ChannelConnectionFailure(ChannelConnectionFailureKind kind, Exception exception)
        {
            Kind = kind;
            Exception = exception ?? throw new ArgumentNullException(nameof(exception));
        }

        public ChannelConnectionFailureKind Kind { get; }

        public Exception Exception { get; }

        public bool IsRetryable => Kind is
            ChannelConnectionFailureKind.ConnectionRefused or
            ChannelConnectionFailureKind.TimedOut or
            ChannelConnectionFailureKind.HostUnreachable or
            ChannelConnectionFailureKind.Network;

        internal static ChannelConnectionFailure FromException(Exception exception)
            => exception switch
            {
                SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
                    new(ChannelConnectionFailureKind.ConnectionRefused, exception),
                SocketException { SocketErrorCode: SocketError.TimedOut } =>
                    new(ChannelConnectionFailureKind.TimedOut, exception),
                SocketException { SocketErrorCode: SocketError.HostUnreachable or SocketError.NetworkUnreachable } =>
                    new(ChannelConnectionFailureKind.HostUnreachable, exception),
                SocketException or IOException =>
                    new(ChannelConnectionFailureKind.Network, exception),
                TimeoutException =>
                    new(ChannelConnectionFailureKind.TimedOut, exception),
                AuthenticationException =>
                    new(ChannelConnectionFailureKind.Authentication, exception),
                OperationCanceledException =>
                    new(ChannelConnectionFailureKind.Canceled, exception),
                ArgumentException or InvalidOperationException =>
                    new(ChannelConnectionFailureKind.Configuration, exception),
                _ =>
                    new(ChannelConnectionFailureKind.Unknown, exception),
            };
    }
}
```

`ChannelStateChangedEventArgs.cs`：

```csharp
namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>客户端状态迁移事件参数。</summary>
    public sealed class ChannelStateChangedEventArgs : EventArgs
    {
        public ChannelStateChangedEventArgs(
            ChannelConnectionState oldState,
            ChannelConnectionState newState,
            ChannelConnectionFailure? failure = null)
        {
            OldState = oldState;
            NewState = newState;
            Failure = failure;
        }

        public ChannelConnectionState OldState { get; }

        public ChannelConnectionState NewState { get; }

        public ChannelConnectionFailure? Failure { get; }
    }
}
```

`IChannelClient.cs`：

```csharp
namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>单连接客户端契约；重连与连接池在后续 change 叠加。</summary>
    public interface IChannelClient : IAsyncDisposable
    {
        /// <summary>当前状态。</summary>
        ChannelConnectionState State { get; }

        /// <summary>仅在 <see cref="ChannelConnectionState.Connected"/> 时非 null。</summary>
        ChannelContext? Connection { get; }

        /// <summary>状态迁移事件；在锁外同步触发。</summary>
        event EventHandler<ChannelStateChangedEventArgs>? StateChanged;

        /// <summary>建立连接；并发调用共享同一次尝试。</summary>
        Task ConnectAsync(CancellationToken ct);

        /// <summary>优雅关闭并进入终态。</summary>
        Task CloseAsync(CancellationToken ct);
    }
}
```

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

- [ ] 1.4 给 internal 连接增加 `Completion` 并补传输测试

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Transports/IChannelConnection.cs`
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Transports/SocketChannelConnection.cs`
- Modify: `tests/Galosys.Foundation.Primitives.Tests/TransportTests.cs`

在 `IChannelConnection` 的 `Termination` 之后增加：

```csharp
/// <summary>
/// 接收方向结束信号。对端 EOF、socket 故障、<see cref="Abort"/> 或释放时完成；
/// 不携带异常，错误原因从 <see cref="Termination"/> 读取。
/// </summary>
Task Completion { get; }
```

在 `SocketChannelConnection` 中增加字段与属性：

```csharp
private readonly TaskCompletionSource _completion =
    new(TaskCreationOptions.RunContinuationsAsynchronously);

public Task Completion => _completion.Task;

private void SignalCompletion() => _completion.TrySetResult();
```

在 `ReceiveLoopAsync` 的 `finally` 中、`CompleteInputWriter(error)` 之后调用 `SignalCompletion()`；在 `Abort` 开头与 `DisposeAsync` 的 `finally` 中也调用一次。重复调用必须无副作用。

在 `TransportTests` 增加：

```csharp
[Fact]
public async Task Completion_SignalsWhenPeerClosesSendDirection()
{
    await using var server = await TestServer.StartAsync(_ => Task.CompletedTask);
    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    await socket.ConnectAsync(server.EndPoint);

    await using var connection = new SocketChannelConnection(
        socket,
        new ChannelServerOptions { EndPoint = server.EndPoint },
        logger: null);

    socket.Shutdown(SocketShutdown.Send);

    await connection.Completion.WaitAsync(TestWaits.DefaultTimeout);
    Assert.True(connection.Completion.IsCompletedSuccessfully);
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~Completion_SignalsWhenPeerClosesSendDirection"`
Expected: 测试通过。

## 2. 单连接建立

- [ ] 2.1 写明文回显失败测试

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/ChannelClientTests.cs`

```csharp
using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class ChannelClientTests
{
    [Fact]
    public async Task ConnectAsync_EchoesBytesAndPublishesStates()
    {
        await using var server = await TestServer.StartAsync(TestHandlers.EchoAsync);
        await using var client = new ChannelClient(new ChannelClientOptions
        {
            EndPoint = server.EndPoint,
            ConnectTimeout = TimeSpan.FromSeconds(2),
        });

        var states = new List<(ChannelConnectionState Old, ChannelConnectionState New)>();
        client.StateChanged += (_, args) => states.Add((args.OldState, args.NewState));

        await client.ConnectAsync(CancellationToken.None).WaitAsync(TestWaits.DefaultTimeout);

        Assert.Equal(ChannelConnectionState.Connected, client.State);
        Assert.NotNull(client.Connection);
        Assert.Equal(
            new[]
            {
                (ChannelConnectionState.Disconnected, ChannelConnectionState.Connecting),
                (ChannelConnectionState.Connecting, ChannelConnectionState.Connected),
            },
            states);

        await client.Connection!.Writer.WriteAsync("ping"u8.ToArray());
        var payload = await ReadExactlyAsync(client.Connection.Reader, 4);
        Assert.Equal("ping", Encoding.UTF8.GetString(payload));
    }

    private static async Task<byte[]> ReadExactlyAsync(PipeReader reader, int count)
    {
        var buffer = new byte[count];
        var offset = 0;

        while (offset < count)
        {
            var result = await reader.ReadAsync().AsTask().WaitAsync(TestWaits.DefaultTimeout);
            var slice = result.Buffer.Slice(0, Math.Min(result.Buffer.Length, count - offset));
            slice.CopyTo(buffer.AsSpan(offset));
            offset += (int)slice.Length;
            reader.AdvanceTo(slice.End);
        }

        return buffer;
    }
}
```

**Step 2: 运行测试，确认编译失败**

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelClientTests"`
Expected: 编译失败，提示 `ChannelClient` 不存在（这是本任务的“红”）。

- [ ] 2.2 实现 `ChannelClient` 的连接与状态迁移

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Client/ChannelClient.cs`

```csharp
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>基于 socket 的单连接客户端。</summary>
    public sealed class ChannelClient : IChannelClient
    {
        private readonly object _gate = new();
        private readonly ChannelClientOptions _options;
        private readonly CancellationTokenSource _closed = new();

        private ChannelConnectionState _state = ChannelConnectionState.Disconnected;
        private Task? _connectTask;
        private CancellationTokenSource? _connectCts;
        private ChannelContext? _connection;
        private int _disposed;

        public ChannelClient(ChannelClientOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            _options = options.Clone();
            _options.Validate();
        }

        public ChannelConnectionState State
        {
            get
            {
                lock (_gate)
                    return _state;
            }
        }

        public ChannelContext? Connection
        {
            get
            {
                lock (_gate)
                    return _state == ChannelConnectionState.Connected ? _connection : null;
            }
        }

        public event EventHandler<ChannelStateChangedEventArgs>? StateChanged;

        public async Task ConnectAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            Task connectTask;

            lock (_gate)
            {
                ThrowIfDisposedLocked();

                if (_state == ChannelConnectionState.Closed)
                    throw new InvalidOperationException("客户端已关闭，不能再次连接。");

                if (_state == ChannelConnectionState.Connected)
                    return;

                if (_connectTask is null)
                {
                    _connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
                    _connectCts.CancelAfter(_options.ConnectTimeout);
                    _connectTask = ConnectCoreAsync(_connectCts, ct);
                }

                connectTask = _connectTask;
            }

            await connectTask.WaitAsync(ct).ConfigureAwait(false);
        }

        public async Task CloseAsync(CancellationToken ct)
        {
            ChannelContext? context;
            ChannelStateChangedEventArgs? args = null;

            lock (_gate)
            {
                if (_state == ChannelConnectionState.Closed)
                    return;

                args = new ChannelStateChangedEventArgs(_state, ChannelConnectionState.Closed);
                _state = ChannelConnectionState.Closed;
                context = _connection;
                _connection = null;
                _connectCts?.Cancel();
            }

            RaiseStateChanged(args);
            _closed.Cancel();

            if (context is not null)
                await context.DisposeAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }

        private async Task ConnectCoreAsync(CancellationTokenSource cts, CancellationToken callerToken)
        {
            await Task.Yield();

            Socket? socket = null;
            SocketChannelConnection? connection = null;
            ChannelContext? context = null;

            try
            {
                lock (_gate)
                {
                    if (_state == ChannelConnectionState.Closed)
                        throw new OperationCanceledException(_closed.Token);
                }

                SetState(ChannelConnectionState.Connecting);

                socket = CreateSocket(_options.EndPoint);
                ApplySocketOptions(socket, _options);
                await socket.ConnectAsync(_options.EndPoint, cts.Token).ConfigureAwait(false);

                connection = new SocketChannelConnection(
                    socket,
                    new ChannelServerOptions { EndPoint = _options.EndPoint },
                    logger: null);
                socket = null;

                context = new ChannelContext(
                    Guid.NewGuid().ToString("N"),
                    connection,
                    EmptyServiceProvider.Instance);

                lock (_gate)
                {
                    if (_state == ChannelConnectionState.Closed)
                        throw new OperationCanceledException(_closed.Token);

                    _connection = context;
                }

                connection = null;
                context = null;

                SetState(ChannelConnectionState.Connected);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                var canceledByClose = _closed.IsCancellationRequested;
                var canceledByCaller = callerToken.IsCancellationRequested && !canceledByClose;

                var failure = canceledByClose
                    ? new ChannelConnectionFailure(
                        ChannelConnectionFailureKind.Canceled,
                        new OperationCanceledException(_closed.Token))
                    : canceledByCaller
                        ? new ChannelConnectionFailure(
                            ChannelConnectionFailureKind.Canceled,
                            new OperationCanceledException(callerToken))
                        : new ChannelConnectionFailure(
                            ChannelConnectionFailureKind.TimedOut,
                            new TimeoutException($"连接在 {_options.ConnectTimeout} 内未完成。"));

                await CleanupAttemptAsync(socket, connection, context).ConfigureAwait(false);

                if (!canceledByClose)
                    SetState(ChannelConnectionState.Disconnected, failure);

                throw failure.Exception;
            }
            catch (Exception ex)
            {
                var failure = ChannelConnectionFailure.FromException(ex);
                await CleanupAttemptAsync(socket, connection, context).ConfigureAwait(false);
                SetState(ChannelConnectionState.Disconnected, failure);
                throw;
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_connectCts, cts))
                        _connectCts = null;

                    _connectTask = null;
                }

                cts.Dispose();
            }
        }

        private static Socket CreateSocket(EndPoint endPoint)
        {
            var protocol = endPoint is IPEndPoint ? ProtocolType.Tcp : ProtocolType.Unspecified;
            return new Socket(endPoint.AddressFamily, SocketType.Stream, protocol);
        }

        private static void ApplySocketOptions(Socket socket, ChannelClientOptions options)
        {
            if (socket.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                return;

            socket.NoDelay = true;

            if (options.KeepAlive is not { Enabled: true } keepAlive)
                return;

            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(
                SocketOptionLevel.Tcp,
                SocketOptionName.TcpKeepAliveTime,
                (int)keepAlive.KeepAliveTime.TotalSeconds);
            socket.SetSocketOption(
                SocketOptionLevel.Tcp,
                SocketOptionName.TcpKeepAliveInterval,
                (int)keepAlive.KeepAliveInterval.TotalSeconds);
            socket.SetSocketOption(
                SocketOptionLevel.Tcp,
                SocketOptionName.TcpKeepAliveRetryCount,
                keepAlive.KeepAliveRetryCount);
        }

        private static async ValueTask CleanupAttemptAsync(
            Socket? socket,
            SocketChannelConnection? connection,
            ChannelContext? context)
        {
            if (context is not null)
            {
                try
                {
                    await context.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }
            else if (connection is not null)
            {
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            socket?.Dispose();
        }

        private void SetState(
            ChannelConnectionState state,
            ChannelConnectionFailure? failure = null)
        {
            ChannelStateChangedEventArgs? args = null;

            lock (_gate)
            {
                if (_state == state || _state == ChannelConnectionState.Closed)
                    return;

                args = new ChannelStateChangedEventArgs(_state, state, failure);
                _state = state;
            }

            RaiseStateChanged(args);
        }

        private void RaiseStateChanged(ChannelStateChangedEventArgs? args)
        {
            if (args is null)
                return;

            try
            {
                StateChanged?.Invoke(this, args);
            }
            catch (Exception)
            {
                // 订阅方异常不得破坏连接状态机。
            }
        }

        private void ThrowIfDisposedLocked()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(ChannelClient));
        }
    }
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelClientTests"`
Expected: `ConnectAsync_EchoesBytesAndPublishesStates` 通过。

- [ ] 2.3 接入客户端 TLS

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Client/ChannelClient.cs`

在字段区增加 `private TlsDuplexPipe? _tls;`。在 `ConnectCoreAsync` 创建 `context` 之前插入：

```csharp
if (_options.Tls is not null)
{
    tls = await TlsDuplexPipe.CreateAsync(
        connection.Transport,
        _options.Tls,
        isServer: false,
        cts.Token).ConfigureAwait(false);
}

context = new ChannelContext(
    Guid.NewGuid().ToString("N"),
    connection,
    EmptyServiceProvider.Instance);

if (tls is not null)
    context.UseTransport(tls);

lock (_gate)
{
    if (_state == ChannelConnectionState.Closed)
        throw new OperationCanceledException(_closed.Token);

    _connection = context;
    _tls = tls;
}

connection = null;
tls = null;
context = null;
```

同时把局部变量声明改为 `TlsDuplexPipe? tls = null;`，`CleanupAttemptAsync` 增加 `TlsDuplexPipe? tls` 参数并在释放 context/connection 之前先 `await tls.DisposeAsync()`；`CloseAsync` 在释放 context 之前先释放 `_tls` 并把字段置 null。

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

- [ ] 2.4 补并发幂等与已连接重复调用测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ChannelClientTests.cs`

```csharp
[Fact]
public async Task ConnectAsync_ConcurrentCalls_ShareOneConnection()
{
    var accepted = 0;
    await using var server = await TestServer.StartAsync(async context =>
    {
        Interlocked.Increment(ref accepted);
        await TestHandlers.EchoAsync(context);
    });

    await using var client = new ChannelClient(new ChannelClientOptions
    {
        EndPoint = server.EndPoint,
        ConnectTimeout = TimeSpan.FromSeconds(2),
    });

    var first = client.ConnectAsync(CancellationToken.None);
    var second = client.ConnectAsync(CancellationToken.None);
    await Task.WhenAll(first, second).WaitAsync(TestWaits.DefaultTimeout);

    var connection = client.Connection;
    Assert.NotNull(connection);
    await client.ConnectAsync(CancellationToken.None).WaitAsync(TestWaits.DefaultTimeout);

    Assert.Same(connection, client.Connection);
    Assert.Equal(1, Volatile.Read(ref accepted));
}

[Fact]
public async Task ConnectAsync_WhenRefused_PublishesRetryableFailure()
{
    var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    var endpoint = (IPEndPoint)probe.LocalEndpoint;
    probe.Stop();

    await using var client = new ChannelClient(new ChannelClientOptions
    {
        EndPoint = endpoint,
        ConnectTimeout = TimeSpan.FromSeconds(2),
    });

    ChannelConnectionFailure? failure = null;
    client.StateChanged += (_, args) =>
    {
        if (args.NewState == ChannelConnectionState.Disconnected)
            failure = args.Failure;
    };

    await Assert.ThrowsAnyAsync<SocketException>(
        () => client.ConnectAsync(CancellationToken.None).WaitAsync(TestWaits.DefaultTimeout));

    Assert.Equal(ChannelConnectionState.Disconnected, client.State);
    Assert.NotNull(failure);
    Assert.Equal(ChannelConnectionFailureKind.ConnectionRefused, failure!.Kind);
    Assert.True(failure.IsRetryable);
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelClientTests"`
Expected: 全部 `ChannelClientTests` 通过。

## 3. 关闭、释放与远端断开

- [ ] 3.1 写关闭与旧连接失效测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ChannelClientTests.cs`

```csharp
[Fact]
public async Task CloseAsync_DrainsOutput_AndIsTerminal()
{
    var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

    await using var server = await TestServer.StartAsync(async context =>
    {
        var result = await context.Reader.ReadAsync();
        received.TrySetResult(result.Buffer.ToArray());
        context.Reader.AdvanceTo(result.Buffer.End);
    });

    await using var client = new ChannelClient(new ChannelClientOptions
    {
        EndPoint = server.EndPoint,
        ConnectTimeout = TimeSpan.FromSeconds(2),
    });

    await client.ConnectAsync(CancellationToken.None);
    await client.Connection!.Writer.WriteAsync("bye"u8.ToArray());
    await client.CloseAsync(CancellationToken.None);

    Assert.Equal(ChannelConnectionState.Closed, client.State);
    Assert.Null(client.Connection);
    Assert.Equal("bye", Encoding.UTF8.GetString(await received.Task.WaitAsync(TestWaits.DefaultTimeout)));

    await Assert.ThrowsAsync<InvalidOperationException>(
        () => client.ConnectAsync(CancellationToken.None));
}

[Fact]
public async Task DisposeAsync_IsIdempotent_AndInvalidatesOldConnection()
{
    await using var server = await TestServer.StartAsync(TestHandlers.EchoAsync);
    var client = new ChannelClient(new ChannelClientOptions
    {
        EndPoint = server.EndPoint,
        ConnectTimeout = TimeSpan.FromSeconds(2),
    });

    await client.ConnectAsync(CancellationToken.None);
    var oldConnection = client.Connection!;

    await client.DisposeAsync();
    await client.DisposeAsync();

    Assert.Equal(ChannelConnectionState.Closed, client.State);
    Assert.True(oldConnection.ConnectionClosed.IsCancellationRequested);
    await Assert.ThrowsAnyAsync<Exception>(
        async () => await oldConnection.Writer.WriteAsync("x"u8.ToArray()));
}

[Fact]
public async Task ConnectionLost_TransitionsToDisconnected()
{
    await using var server = await TestServer.StartAsync(context =>
    {
        context.Abort(new IOException("server-drop"));
        return Task.CompletedTask;
    });

    await using var client = new ChannelClient(new ChannelClientOptions
    {
        EndPoint = server.EndPoint,
        ConnectTimeout = TimeSpan.FromSeconds(2),
    });

    await client.ConnectAsync(CancellationToken.None);
    var connection = client.Connection!;

    await TestClient.WaitUntilAsync(
        () => Task.FromResult(client.State == ChannelConnectionState.Disconnected),
        "客户端未在远端断开后迁移到 Disconnected。");

    Assert.Null(client.Connection);
    Assert.True(connection.ConnectionClosed.IsCancellationRequested);
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelClientTests"`
Expected: `CloseAsync_DrainsOutput_AndIsTerminal` 与 `ConnectionLost_TransitionsToDisconnected` 失败；`DisposeAsync_IsIdempotent_AndInvalidatesOldConnection` 可能因远端未断开而失败（这是本任务的“红”）。

- [ ] 3.2 实现有界关闭、释放与连接尝试取消

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Client/ChannelClient.cs`

增加字段：

```csharp
private static readonly TimeSpan CloseDrainTimeout = TimeSpan.FromSeconds(5);
private Task? _shutdownTask;
private SocketChannelConnection? _transportConnection;
```

用以下实现替换 `CloseAsync` / `DisposeAsync`：

```csharp
public Task CloseAsync(CancellationToken ct)
{
    lock (_gate)
    {
        _shutdownTask ??= ShutdownCoreAsync(ct);
        return _shutdownTask;
    }
}

public async ValueTask DisposeAsync()
{
    if (Interlocked.Exchange(ref _disposed, 1) != 0)
        return;

    await CloseAsync(CancellationToken.None).ConfigureAwait(false);
}

private async Task ShutdownCoreAsync(CancellationToken ct)
{
    await Task.Yield();

    ChannelContext? context;
    TlsDuplexPipe? tls;
    CancellationTokenSource? connectCts;
    Task? connectTask;
    ChannelStateChangedEventArgs? args = null;

    lock (_gate)
    {
        if (_state != ChannelConnectionState.Closed)
        {
            args = new ChannelStateChangedEventArgs(_state, ChannelConnectionState.Closed);
            _state = ChannelConnectionState.Closed;
        }

        context = _connection;
        tls = _tls;
        connectCts = _connectCts;
        connectTask = _connectTask;
        _connection = null;
        _tls = null;
        _transportConnection = null;
    }

    RaiseStateChanged(args);

    try
    {
        connectCts?.Cancel();
    }
    catch (ObjectDisposedException)
    {
    }

    if (connectTask is not null)
    {
        try
        {
            await connectTask.WaitAsync(CloseDrainTimeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    drainCts.CancelAfter(CloseDrainTimeout);

    try
    {
        if (tls is not null)
            await tls.DisposeAsync().AsTask().WaitAsync(drainCts.Token).ConfigureAwait(false);

        if (context is not null)
            await context.DisposeAsync().AsTask().WaitAsync(drainCts.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        context?.Abort(new OperationCanceledException("关闭等待被取消或超时。"));

        if (context is not null)
        {
            try
            {
                await context.DisposeAsync().AsTask()
                    .WaitAsync(CloseDrainTimeout).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }
}
```

`_transportConnection` 在连接成功时赋值，由 3.4 的观察器使用。此任务结束后运行：

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelClientTests"`
Expected: 关闭与释放测试通过；`ConnectionLost` 仍失败。

- [ ] 3.3 补远端正常 EOF 与重复关闭测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ChannelClientTests.cs`

```csharp
[Fact]
public async Task RemoteEof_TransitionsToDisconnected()
{
    await using var server = await TestServer.StartAsync(context =>
    {
        context.Writer.Complete();
        return Task.CompletedTask;
    });

    await using var client = new ChannelClient(new ChannelClientOptions
    {
        EndPoint = server.EndPoint,
        ConnectTimeout = TimeSpan.FromSeconds(2),
    });

    await client.ConnectAsync(CancellationToken.None);
    await TestClient.WaitUntilAsync(
        () => Task.FromResult(client.State == ChannelConnectionState.Disconnected),
        "客户端未在对端 EOF 后迁移到 Disconnected。");

    Assert.Null(client.Connection);
}

[Fact]
public async Task CloseAsync_IsIdempotent_AndDoesNotRaiseClosedTwice()
{
    await using var server = await TestServer.StartAsync(TestHandlers.EchoAsync);
    await using var client = new ChannelClient(new ChannelClientOptions
    {
        EndPoint = server.EndPoint,
        ConnectTimeout = TimeSpan.FromSeconds(2),
    });

    await client.ConnectAsync(CancellationToken.None);

    var closedEvents = 0;
    client.StateChanged += (_, args) =>
    {
        if (args.NewState == ChannelConnectionState.Closed)
            Interlocked.Increment(ref closedEvents);
    };

    await client.CloseAsync(CancellationToken.None);
    await client.CloseAsync(CancellationToken.None);

    Assert.Equal(ChannelConnectionState.Closed, client.State);
    Assert.Equal(1, Volatile.Read(ref closedEvents));
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelClientTests"`
Expected: 两个测试通过。

- [ ] 3.4 实现 `Completion` 观察器

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Client/ChannelClient.cs`

增加字段 `private SocketChannelConnection? _transportConnection;`，在 `ConnectCoreAsync` 成功赋值处写入 `_transportConnection = connection;`。增加：

```csharp
private void WatchCompletion()
{
    _ = WatchCompletionAsync();
}

private async Task WatchCompletionAsync()
{
    SocketChannelConnection? connection;
    ChannelContext? context;
    TlsDuplexPipe? tls;

    lock (_gate)
    {
        connection = _transportConnection;
        context = _connection;
        tls = _tls;
    }

    if (connection is null || context is null)
        return;

    await connection.Completion.ConfigureAwait(false);

    lock (_gate)
    {
        if (_state != ChannelConnectionState.Connected || !ReferenceEquals(_connection, context))
            return;

        _connection = null;
        _transportConnection = null;
        _tls = null;
    }

    if (tls is not null)
    {
        try
        {
            await tls.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    try
    {
        await context.DisposeAsync().ConfigureAwait(false);
    }
    catch (Exception)
    {
    }

    var error = connection.Termination.IsCompletedSuccessfully
        ? connection.Termination.Result
        : null;

    SetState(
        ChannelConnectionState.Disconnected,
        error is null ? null : ChannelConnectionFailure.FromException(error));
}
```

在 `ConnectCoreAsync` 的 `SetState(Connected)` 之后调用 `WatchCompletion()`。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelClientTests"`
Expected: 全部 `ChannelClientTests` 通过。

## 4. TLS、清理与验证

- [ ] 4.1 补 TLS 回显与证书失败测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ChannelClientTests.cs`

```csharp
[Fact]
public async Task ConnectAsync_WithTls_EchoesBytes()
{
    using var certificate = TlsTestCertificates.CreateSelfSigned();

    await using var server = new ChannelServer(
        new ChannelServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        },
        pipeline =>
        {
            pipeline.UseTls(new ChannelTlsOptions
            {
                ServerOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                },
            });
            pipeline.Run(TestHandlers.EchoAsync);
        });

    await server.StartAsync(CancellationToken.None);

    await using var client = new ChannelClient(new ChannelClientOptions
    {
        EndPoint = server.LocalEndPoint!,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        Tls = new ChannelTlsOptions
        {
            ClientOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                RemoteCertificateValidationCallback = (_, remote, _, _) =>
                    remote is not null
                    && remote.GetCertHashString(HashAlgorithmName.SHA256)
                        == certificate.GetCertHashString(HashAlgorithmName.SHA256),
            },
        },
    });

    await client.ConnectAsync(CancellationToken.None).WaitAsync(TestWaits.DefaultTimeout);
    await client.Connection!.Writer.WriteAsync("tls"u8.ToArray());
    var payload = await ReadExactlyAsync(client.Connection.Reader, 3);
    Assert.Equal("tls", Encoding.UTF8.GetString(payload));
}

[Fact]
public async Task ConnectAsync_WithRejectedCertificate_FailsWithoutRetryFlag()
{
    using var certificate = TlsTestCertificates.CreateSelfSigned();

    await using var server = new ChannelServer(
        new ChannelServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        },
        pipeline =>
        {
            pipeline.UseTls(new ChannelTlsOptions
            {
                ServerOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                },
            });
            pipeline.Run(TestHandlers.EchoAsync);
        });

    await server.StartAsync(CancellationToken.None);

    await using var client = new ChannelClient(new ChannelClientOptions
    {
        EndPoint = server.LocalEndPoint!,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        Tls = new ChannelTlsOptions
        {
            ClientOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                RemoteCertificateValidationCallback = (_, _, _, _) => false,
            },
        },
    });

    ChannelConnectionFailure? failure = null;
    client.StateChanged += (_, args) =>
    {
        if (args.NewState == ChannelConnectionState.Disconnected)
            failure = args.Failure;
    };

    await Assert.ThrowsAsync<AuthenticationException>(
        () => client.ConnectAsync(CancellationToken.None));

    Assert.Equal(ChannelConnectionState.Disconnected, client.State);
    Assert.NotNull(failure);
    Assert.Equal(ChannelConnectionFailureKind.Authentication, failure!.Kind);
    Assert.False(failure.IsRetryable);
}
```

在文件顶部补充 `System.Net.Security`、`System.Security.Authentication`、`System.Security.Cryptography`。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelClientTests"`
Expected: 两个 TLS 测试通过。

- [ ] 4.2 删除 `ChannelClientBase` 占位并锁定命名空间

**Files:**
- Delete: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/ChannelClientBase.cs`
- Modify: `tests/Galosys.Foundation.Primitives.Tests/NamespaceConventionTests.cs`

在 `NamespaceConventionTests` 增加：

```csharp
[Fact]
public void ChannelClientBase_IsRemoved()
{
    var assembly = typeof(ChannelClient).Assembly;

    Assert.Null(assembly.GetType("Microsoft.Extensions.Hosting.Net.ChannelClientBase"));
    Assert.Equal(
        "Microsoft.Extensions.Hosting.Net",
        typeof(ChannelClient).Namespace);
    Assert.Equal(
        "Microsoft.Extensions.Options",
        typeof(ChannelClientOptions).Namespace);
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~NamespaceConventionTests"`
Expected: 全部通过。

- [ ] 4.3 更新 Primitives README 的客户端章节

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/README.md`

增加：

```markdown
## 单连接客户端（P4-1）

```csharp
await using var client = new ChannelClient(new ChannelClientOptions
{
    EndPoint = new IPEndPoint(IPAddress.Loopback, 8000),
    ConnectTimeout = TimeSpan.FromSeconds(5),
});

await client.ConnectAsync(cancellationToken);

await client.Connection!.Writer.WriteAsync(bytes);
var read = await client.Connection.Reader.ReadAsync();
```

- `ConnectAsync` 幂等，并发调用共享一次连接尝试；`CloseAsync` / `DisposeAsync` 进入终态 `Closed`。
- 客户端只暴露字节 `Connection`，消息收发通过 P2 的 `Connection.AsSession<T>()`。
- `ChannelClientOptions.Tls` 非空时先完成客户端 TLS 握手，再暴露 `Connection`；失败不回退明文。
- 重连、连接池与 batch 不在本阶段。
```

- [ ] 4.4 跑构建、全量测试与 OpenSpec 校验

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
Expected: 现有测试与新增 `ChannelClientOptionsTests` / `ChannelClientTests` 全部通过。

Run: `openspec validate "primitives-client-1" --strict --no-interactive`
Expected: `Change 'primitives-client-1' is valid`。
