# P1-1 服务端内核与契约 Implementation Plan

> **REQUIRED SUB-SKILL:** 使用 `executing-plans` 或 `opsx-apply-x` 逐任务执行本计划。
> 本仓库不是 git 仓库，计划中的“提交”步骤统一替换为“构建 + 测试”验证，不执行 `git` 命令。

**Goal:** 用 Kestrel 公共 transport 替换 `Galosys.Foundation.Primitives` 的自研 socket 内核，冻结服务端公共契约，并用 loopback 集成测试锁定行为。

**Architecture:** `ChannelServerBase` 通过 internal `KestrelChannelListener` 绑定 `SocketTransportFactory`；每连接创建 `ChannelContext`（包装 `ConnectionContext`）与独立 `IServiceScope`；`Configure(IChannelPipeline)` 构建一次委托链并被所有连接复用；`StopAsync` 停止 accept、等待在途连接、超时 abort。

**Tech Stack:** .NET 10、`Microsoft.AspNetCore.App` 共享框架（Kestrel Transport.Sockets）、`System.IO.Pipelines`、xunit 2.9.3。

---

## 1. 测试工程与依赖

- [x] 1.1 创建测试项目（完成）

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`

**Step 1: 写入项目文件**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <RootNamespace>Galosys.Foundation.Primitives.Tests</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Galosys.Foundation.Primitives\Galosys.Foundation.Primitives.csproj" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

</Project>
```

**Step 2: 还原依赖**

Run: `dotnet restore tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
Expected: `Restored ...`，无 NU1101/NU1102 错误。

- [x] 1.2 给 Primitives 加共享框架引用与内部可见性（完成）

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`

**Step 1: 在现有 `<ItemGroup>` 之后追加**

```xml
	<ItemGroup>
		<FrameworkReference Include="Microsoft.AspNetCore.App" />
	</ItemGroup>

	<ItemGroup>
		<InternalsVisibleTo Include="Galosys.Foundation.Primitives.Tests" />
	</ItemGroup>
```

**Step 2: 构建 Primitives**

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: `Build succeeded`。若出现 `NETSDK1100`，确认只构建该 `.csproj` 而不是解决方案。

- [x] 1.3 建立测试辅助类型（先编译失败）（完成）

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/TestSupport.cs`

**Step 1: 写入辅助类型**

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

internal static class TestWaits
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
}

internal sealed class TestChannelServer : ChannelServerBase
{
    private readonly Func<ChannelContext, Task> _handler;

    public TestChannelServer(ChannelServerOptions options, Func<ChannelContext, Task> handler)
        : base(options)
    {
        _handler = handler;
    }

    protected override Task ChannelHandleAsync(ChannelContext context) => _handler(context);
}

internal sealed class ConfiguredTestServer : ChannelServerBase
{
    private readonly Action<IChannelPipeline> _configure;
    private readonly Func<ChannelContext, Task> _handler;

    public ConfiguredTestServer(
        ChannelServerOptions options,
        Action<IChannelPipeline> configure,
        Func<ChannelContext, Task> handler)
        : base(options)
    {
        _configure = configure;
        _handler = handler;
    }

    protected override void Configure(IChannelPipeline pipeline) => _configure(pipeline);

    protected override Task ChannelHandleAsync(ChannelContext context) => _handler(context);
}

internal sealed class TestServerHandle : IAsyncDisposable
{
    public TestServerHandle(ChannelServerBase server, IPEndPoint endPoint)
    {
        Server = server;
        EndPoint = endPoint;
    }

    public ChannelServerBase Server { get; }

    public IPEndPoint EndPoint { get; }

    public async ValueTask DisposeAsync() => await Server.DisposeAsync().ConfigureAwait(false);
}

internal static class TestServer
{
    public static async Task<TestServerHandle> StartAsync(
        Func<ChannelContext, Task> handler,
        Action<ChannelServerOptions>? configure = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        var options = new ChannelServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        };
        configure?.Invoke(options);

        var server = new TestChannelServer(options, handler)
        {
            ScopeFactory = scopeFactory,
        };

        await server.StartAsync(CancellationToken.None).ConfigureAwait(false);
        return new TestServerHandle(server, (IPEndPoint)server.LocalEndPoint!);
    }
}

internal static class TestClient
{
    public static async Task<TcpClient> ConnectAsync(IPEndPoint endPoint)
    {
        var client = new TcpClient();
        await client.ConnectAsync(endPoint.Address, endPoint.Port).WaitAsync(TestWaits.DefaultTimeout);
        return client;
    }

    public static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset)).AsTask().WaitAsync(TestWaits.DefaultTimeout);
            if (read == 0)
                throw new IOException("连接在读取完成前关闭。");

            offset += read;
        }

        return buffer;
    }

    public static Task<int> ReadOneByteAsync(NetworkStream stream)
        => stream.ReadAsync(new byte[1]).AsTask().WaitAsync(TestWaits.DefaultTimeout);

    public static async Task WaitUntilListeningAsync(ChannelServerBase server)
    {
        var deadline = DateTime.UtcNow + TestWaits.DefaultTimeout;
        while (server.LocalEndPoint is null)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("服务器未在超时时间内完成绑定。");

            await Task.Delay(10);
        }
    }
}

internal static class TestHandlers
{
    public static async Task EchoAsync(ChannelContext context)
    {
        while (true)
        {
            var result = await context.Reader.ReadAsync();
            if (result.IsCompleted && result.Buffer.IsEmpty)
                break;

            foreach (var segment in result.Buffer)
                await context.Writer.WriteAsync(segment);

            context.Reader.AdvanceTo(result.Buffer.End);
            await context.Writer.FlushAsync();
        }
    }

    public static async Task HoldAsync(ChannelContext context, CancellationToken release)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(release, context.ConnectionClosed);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

internal sealed class TrackingScopeFactory : IServiceScopeFactory
{
    private readonly ConcurrentQueue<string> _events = new();

    public int CreatedCount;

    public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyCollection<string> Events => _events;

    public IServiceScope CreateScope()
    {
        Interlocked.Increment(ref CreatedCount);
        return new Scope(this);
    }

    public void Record(string value) => _events.Enqueue(value);

    private void NotifyDisposed()
    {
        _events.Enqueue("scope-disposed");
        Disposed.TrySetResult();
    }

    private sealed class Scope : IServiceScope, IAsyncDisposable
    {
        private readonly TrackingScopeFactory _owner;

        public Scope(TrackingScopeFactory owner) => _owner = owner;

        public IServiceProvider ServiceProvider { get; } = new EmptyProvider();

        public void Dispose() => _owner.NotifyDisposed();

        public ValueTask DisposeAsync()
        {
            _owner.NotifyDisposed();
            return ValueTask.CompletedTask;
        }

        private sealed class EmptyProvider : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }
    }
}
```

**Step 2: 构建，确认按预期失败**

Run: `dotnet build tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
Expected: 编译错误，提示 `ChannelContext` 缺少 `ConnectionClosed`/`Services`、`IChannelServer` 缺少 `RunAsync`、`ChannelServerOptions` 不存在。这正是要实现的契约。

## 2. Options 与校验

- [x] 2.1 写 Options 校验测试（完成）

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/OptionsValidationTests.cs`

**Step 1: 写入测试**

```csharp
using System.Net;
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class OptionsValidationTests
{
    [Fact]
    public async Task StartAsync_Throws_WhenEndPointMissing()
    {
        var server = new TestChannelServer(new ChannelServerOptions(), _ => Task.CompletedTask);

        await Assert.ThrowsAsync<ArgumentException>(() => server.StartAsync(CancellationToken.None));
        Assert.Null(server.LocalEndPoint);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task StartAsync_Throws_WhenBacklogInvalid(int backlog)
    {
        var server = new TestChannelServer(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                Backlog = backlog,
            },
            _ => Task.CompletedTask);

        await Assert.ThrowsAsync<ArgumentException>(() => server.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_Throws_WhenShutdownTimeoutInvalid()
    {
        var server = new TestChannelServer(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ShutdownTimeout = TimeSpan.Zero,
            },
            _ => Task.CompletedTask);

        await Assert.ThrowsAsync<ArgumentException>(() => server.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_Succeeds_WithBacklogOfOne()
    {
        await using var handle = await TestServer.StartAsync(_ => Task.CompletedTask, options => options.Backlog = 1);

        Assert.NotNull(handle.Server.LocalEndPoint);
    }
}
```

- [x] 2.2 实现 ChannelServerOptions 与 ChannelKeepAliveOptions（完成）

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Options/ChannelServerOptions.cs`
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Options/ChannelKeepAliveOptions.cs`

**Step 1: 写入 `ChannelServerOptions.cs`**

```csharp
using System.Net;
using System.Net.Sockets;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>服务端监听器配置。</summary>
    public sealed class ChannelServerOptions
    {
        /// <summary>监听端点。必须设置。</summary>
        public EndPoint EndPoint { get; set; } = default!;

        /// <summary>监听 backlog，默认 512。</summary>
        public int Backlog { get; set; } = 512;

        /// <summary>最大并发连接数，0 表示不限制。</summary>
        public int MaxConnections { get; set; }

        /// <summary>是否禁用 Nagle 算法。</summary>
        public bool NoDelay { get; set; } = true;

        /// <summary>TCP keep-alive 配置。</summary>
        public ChannelKeepAliveOptions? KeepAlive { get; set; }

        /// <summary>SO_LINGER 配置。</summary>
        public LingerOption? LingerState { get; set; }

        /// <summary>是否启用 SO_REUSEADDR。</summary>
        public bool ReuseAddress { get; set; }

        /// <summary>IPv6 监听时是否启用双栈。</summary>
        public bool DualMode { get; set; }

        /// <summary>优雅停机等待在途连接的最长时间。</summary>
        public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>读缓冲上限，null 表示由内核决定。</summary>
        public long? MaxReadBufferSize { get; set; }

        /// <summary>写缓冲上限，null 表示由内核决定。</summary>
        public long? MaxWriteBufferSize { get; set; }

        /// <summary>IO 队列数量，0 表示由内核决定。</summary>
        public int IOQueueCount { get; set; }

        /// <summary>自定义创建并绑定监听 socket 的工厂。</summary>
        public Func<EndPoint, Socket>? CreateBoundListenSocket { get; set; }

        internal void Validate()
        {
            if (EndPoint is null)
                throw new ArgumentException("必须提供监听端点 EndPoint。", nameof(EndPoint));

            if (Backlog <= 0)
                throw new ArgumentOutOfRangeException(nameof(Backlog), Backlog, "Backlog 必须大于 0。");

            if (MaxConnections < 0)
                throw new ArgumentOutOfRangeException(nameof(MaxConnections), MaxConnections, "MaxConnections 不能为负数。");

            if (ShutdownTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(ShutdownTimeout), ShutdownTimeout, "ShutdownTimeout 必须大于 0。");

            if (MaxReadBufferSize is <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxReadBufferSize), MaxReadBufferSize, "MaxReadBufferSize 必须大于 0。");

            if (MaxWriteBufferSize is <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxWriteBufferSize), MaxWriteBufferSize, "MaxWriteBufferSize 必须大于 0。");

            if (IOQueueCount < 0)
                throw new ArgumentOutOfRangeException(nameof(IOQueueCount), IOQueueCount, "IOQueueCount 不能为负数。");
        }
    }
}
```

**Step 2: 写入 `ChannelKeepAliveOptions.cs`**

```csharp
namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>TCP keep-alive 配置。</summary>
    public sealed class ChannelKeepAliveOptions
    {
        /// <summary>是否启用 keep-alive。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>空闲多久后开始探测。</summary>
        public TimeSpan KeepAliveTime { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>探测间隔。</summary>
        public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>探测失败重试次数。</summary>
        public int KeepAliveRetryCount { get; set; } = 9;
    }
}
```

- [x] 2.3 运行 Options 测试（完成）

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~OptionsValidationTests"`
Expected: 5 个测试通过（含 2 个 Theory 用例）。若仍因缺少 `ChannelServerBase`/`ChannelContext` 契约而编译失败，先完成 Task 4.4–5.2，再回到本任务。

## 3. 管线

- [x] 3.1 写管线单元测试（完成）

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/PipelineTests.cs`

**Step 1: 写入测试**

```csharp
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class PipelineTests
{
    [Fact]
    public async Task Build_ExecutesMiddlewareInRegistrationOrder()
    {
        var log = new List<string>();
        var pipeline = new ChannelPipeline();
        pipeline.Use(async (context, next) =>
        {
            log.Add("A-in");
            await next(context);
            log.Add("A-out");
        });
        pipeline.Use(async (context, next) =>
        {
            log.Add("B-in");
            await next(context);
            log.Add("B-out");
        });
        pipeline.Run(_ =>
        {
            log.Add("handler");
            return Task.CompletedTask;
        });

        await pipeline.Build()(null!);

        Assert.Equal(new[] { "A-in", "B-in", "handler", "B-out", "A-out" }, log);
    }

    [Fact]
    public void Run_CannotBeCalledTwice()
    {
        var pipeline = new ChannelPipeline();
        pipeline.Run(_ => Task.CompletedTask);

        Assert.Throws<InvalidOperationException>(() => pipeline.Run(_ => Task.CompletedTask));
    }

    [Fact]
    public void Build_Throws_WhenRunWasNotCalled()
    {
        var pipeline = new ChannelPipeline();
        pipeline.Use((context, next) => next(context));

        Assert.Throws<InvalidOperationException>(() => pipeline.Build());
    }
}
```

- [x] 3.2 实现管线类型（完成）

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Pipeline/ChannelHandlerDelegate.cs`
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Pipeline/ChannelMiddleware.cs`
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Pipeline/IChannelPipeline.cs`
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Pipeline/ChannelPipeline.cs`

**Step 1: delegates 与接口**

```csharp
namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>连接终端处理器。</summary>
    public delegate Task ChannelHandlerDelegate(ChannelContext context);
}
```

```csharp
namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>连接中间件，只允许包装 Transport，不读取业务数据。</summary>
    public delegate Task ChannelMiddleware(ChannelContext context, ChannelHandlerDelegate next);
}
```

```csharp
namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>每连接处理管线。</summary>
    public interface IChannelPipeline
    {
        /// <summary>添加中间件，按注册顺序由外向内执行。</summary>
        IChannelPipeline Use(ChannelMiddleware middleware);

        /// <summary>设置终端处理器，只能调用一次。</summary>
        void Run(ChannelHandlerDelegate handler);
    }
}
```

**Step 2: `ChannelPipeline.cs`**

```csharp
namespace Microsoft.Extensions.Hosting.Net
{
    internal sealed class ChannelPipeline : IChannelPipeline
    {
        private readonly List<ChannelMiddleware> _middlewares = new();
        private ChannelHandlerDelegate? _handler;
        private ChannelHandlerDelegate? _built;

        public IChannelPipeline Use(ChannelMiddleware middleware)
        {
            ArgumentNullException.ThrowIfNull(middleware);

            if (_built is not null)
                throw new InvalidOperationException("管线已构建，不能再添加中间件。");

            _middlewares.Add(middleware);
            return this;
        }

        public void Run(ChannelHandlerDelegate handler)
        {
            ArgumentNullException.ThrowIfNull(handler);

            if (_handler is not null)
                throw new InvalidOperationException("Run 只能调用一次。");

            _handler = handler;
        }

        internal ChannelHandlerDelegate Build()
        {
            if (_built is not null)
                return _built;

            if (_handler is null)
                throw new InvalidOperationException("管线缺少 Run 终端处理器。");

            var next = _handler;
            for (var i = _middlewares.Count - 1; i >= 0; i--)
            {
                var middleware = _middlewares[i];
                var captured = next;
                next = context => middleware(context, captured);
            }

            _built = next;
            return _built;
        }
    }
}
```

**Step 3: 运行管线测试**

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~PipelineTests"`
Expected: 3 个测试通过。

## 4. 连接契约与 Kestrel 内核

- [x] 4.1 重写 ChannelContext（完成）

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/ChannelContext.cs`（整体替换）

**Step 1: 用以下内容替换文件**

```csharp
using System.IO.Pipelines;
using System.Net;
using Microsoft.AspNetCore.Connections;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>只读连接视图。构造为 internal，外部不可伪造。</summary>
    public sealed class ChannelContext : IAsyncDisposable
    {
        private readonly ConnectionContext _connection;
        private readonly CancellationTokenSource _connectionClosed = new();
        private readonly CancellationToken _connectionClosedToken;
        private Exception? _abortReason;
        private int _closedNotified;
        private int _completed;

        internal ChannelContext(string connectionId, ConnectionContext connection, IServiceProvider services)
        {
            ConnectionId = connectionId;
            _connection = connection;
            Services = services;
            _connectionClosedToken = _connectionClosed.Token;
        }

        /// <summary>连接标识，用于日志与追踪关联。</summary>
        public string ConnectionId { get; }

        public EndPoint? LocalEndPoint => _connection.LocalEndPoint;

        public EndPoint? RemoteEndPoint => _connection.RemoteEndPoint;

        /// <summary>原始字节传输。</summary>
        public IDuplexPipe Transport => _connection.Transport;

        public PipeReader Reader => Transport.Input;

        public PipeWriter Writer => Transport.Output;

        /// <summary>连接关闭令牌，恰好触发一次。</summary>
        public CancellationToken ConnectionClosed => _connectionClosedToken;

        /// <summary>每连接的服务提供程序。</summary>
        public IServiceProvider Services { get; }

        /// <summary>连接内杂项状态，不放服务。</summary>
        public IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        internal bool IsAborted => _abortReason is not null;

        /// <summary>立即中止连接。</summary>
        public void Abort(Exception? reason = null)
        {
            if (reason is not null)
                Interlocked.CompareExchange(ref _abortReason, reason, null);

            NotifyClosed();

            try
            {
                _connection.Abort(new ConnectionAbortedException(_abortReason?.Message ?? "连接已中止。", _abortReason));
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>幂等释放；已中止的连接不会再走优雅完成。</summary>
        public ValueTask DisposeAsync() => CompleteAsync(IsAborted ? _abortReason : null);

        internal async ValueTask CompleteAsync(Exception? error)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;

            if (error is not null)
            {
                Abort(error);
            }
            else
            {
                try
                {
                    Transport.Output.Complete();
                }
                catch (Exception)
                {
                }
            }

            NotifyClosed();

            try
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            _connectionClosed.Dispose();
        }

        private void NotifyClosed()
        {
            if (Interlocked.Exchange(ref _closedNotified, 1) != 0)
                return;

            try
            {
                _connectionClosed.Cancel();
            }
            catch (AggregateException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
```

**Step 2: 删除死代码**

确认 `AbstractChannelHandler` 不再存在（本次整体替换已删除），全仓库检索：

Run: `Select-String -Path src\*\*.cs -Pattern "AbstractChannelHandler" -Recurse`
Expected: 无输出。

- [x] 4.2 重写 IChannelServer（完成）

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/IChannelServer.cs`（整体替换）

**Step 1: 写入**

```csharp
using System.Net;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>监听器生命周期契约。</summary>
    public interface IChannelServer : IAsyncDisposable, IDisposable
    {
        /// <summary>绑定成功后的本地端点；未启动时为 null。</summary>
        EndPoint? LocalEndPoint { get; }

        /// <summary>绑定并开始接受连接后返回。</summary>
        Task StartAsync(CancellationToken ct);

        /// <summary>启动后阻塞到停机；取消视为正常停机。</summary>
        Task RunAsync(CancellationToken ct);

        /// <summary>停止接受连接、等待在途连接、超时后强制中止。</summary>
        Task StopAsync(CancellationToken ct);
    }
}
```

- [x] 4.3 新增 Kestrel 监听适配层（完成）

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Transports/KestrelChannelListener.cs`
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Transports/EmptyServiceScope.cs`

**Step 1: `KestrelChannelListener.cs`**

```csharp
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting.Net
{
    internal sealed class KestrelChannelListener : IAsyncDisposable
    {
        private readonly IConnectionListener _listener;

        private KestrelChannelListener(IConnectionListener listener) => _listener = listener;

        public EndPoint? LocalEndPoint => _listener.EndPoint;

        public static async Task<KestrelChannelListener> BindAsync(
            ChannelServerOptions options,
            ILoggerFactory? loggerFactory,
            CancellationToken ct)
        {
            var factory = new SocketTransportFactory(
                Options.Create(CreateTransportOptions(options)),
                loggerFactory ?? NullLoggerFactory.Instance);

            var listener = await factory.BindAsync(options.EndPoint, ct).ConfigureAwait(false);
            return new KestrelChannelListener(listener);
        }

        private static SocketTransportOptions CreateTransportOptions(ChannelServerOptions options) => new()
        {
            Backlog = options.Backlog,
            NoDelay = options.NoDelay,
            MaxReadBufferSize = options.MaxReadBufferSize,
            MaxWriteBufferSize = options.MaxWriteBufferSize,
            IOQueueCount = options.IOQueueCount,
            CreateBoundListenSocket = endPoint => CreateBoundSocket(endPoint, options),
        };

        private static Socket CreateBoundSocket(EndPoint endPoint, ChannelServerOptions options)
        {
            var socket = options.CreateBoundListenSocket is { } factory
                ? factory(endPoint)
                : new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            ApplySocketOptions(socket, options);

            if (options.CreateBoundListenSocket is null)
                socket.Bind(endPoint);

            return socket;
        }

        private static void ApplySocketOptions(Socket socket, ChannelServerOptions options)
        {
            socket.NoDelay = options.NoDelay;

            if (options.LingerState is not null)
                socket.LingerState = options.LingerState;

            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, options.ReuseAddress);

            if (socket.AddressFamily == AddressFamily.InterNetworkV6 && options.DualMode)
                socket.DualMode = true;

            if (options.KeepAlive is { Enabled: true } keepAlive)
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                try
                {
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
                catch (SocketException)
                {
                }
            }
        }

        public ValueTask<ConnectionContext?> AcceptAsync(CancellationToken ct) => _listener.AcceptAsync(ct);

        public ValueTask<ConnectionContext?> UnbindAsync(CancellationToken ct) => _listener.UnbindAsync(ct);

        public ValueTask DisposeAsync() => _listener.DisposeAsync();
    }
}
```

**Step 2: `EmptyServiceScope.cs`**

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.Hosting.Net
{
    internal sealed class EmptyServiceScope : IServiceScope, IAsyncDisposable
    {
        public static readonly EmptyServiceScope Instance = new();

        private EmptyServiceScope()
        {
        }

        public IServiceProvider ServiceProvider => EmptyServiceProvider.Instance;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();

        private EmptyServiceProvider()
        {
        }

        public object? GetService(Type serviceType) => null;
    }
}
```

- [x] 4.4 重写 ChannelServerBase（删除旧内核）（完成）

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/ChannelServerBase.cs`（整体替换）

**Step 1: 用以下内容替换文件**

```csharp
using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>监听器基类：绑定、接受连接、执行管线、优雅停机。</summary>
    public abstract class ChannelServerBase : IChannelServer
    {
        private readonly ChannelServerOptions _options;
        private readonly ConcurrentDictionary<string, ChannelContext> _activeConnections = new();
        private readonly ConcurrentDictionary<string, Task> _activeTasks = new();
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private KestrelChannelListener? _listener;
        private ChannelHandlerDelegate? _pipeline;
        private CancellationTokenSource? _stopping;
        private Task? _acceptLoop;
        private int _connectionCount;
        private int _started;
        private int _stopStarted;
        private int _disposed;

        protected ChannelServerBase(EndPoint endPoint)
            : this(new ChannelServerOptions { EndPoint = endPoint })
        {
        }

        protected ChannelServerBase(ChannelServerOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        internal IServiceScopeFactory? ScopeFactory { get; set; }

        internal ILoggerFactory? LoggerFactory { get; set; }

        public EndPoint? LocalEndPoint { get; private set; }

        /// <summary>默认管线：直接调用 <see cref="ChannelHandleAsync"/>。</summary>
        protected virtual void Configure(IChannelPipeline pipeline) => pipeline.Run(ChannelHandleAsync);

        protected abstract Task ChannelHandleAsync(ChannelContext context);

        public async Task StartAsync(CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
                return;

            try
            {
                _options.Validate();
                var pipeline = BuildPipeline();
                var listener = await KestrelChannelListener.BindAsync(_options, LoggerFactory, ct).ConfigureAwait(false);

                _pipeline = pipeline;
                _listener = listener;
                LocalEndPoint = listener.LocalEndPoint ?? _options.EndPoint;
                _stopping = new CancellationTokenSource();
                _acceptLoop = AcceptLoopAsync(_stopping.Token);
            }
            catch
            {
                Volatile.Write(ref _started, 0);
                throw;
            }
        }

        public async Task RunAsync(CancellationToken ct)
        {
            await StartAsync(ct).ConfigureAwait(false);

            try
            {
                var acceptLoop = _acceptLoop;
                if (acceptLoop is not null)
                    await acceptLoop.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        public async Task StopAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _stopStarted, 1) == 0)
            {
                try
                {
                    await StopCoreAsync().ConfigureAwait(false);
                }
                finally
                {
                    _stopped.TrySetResult();
                }
            }

            await _stopped.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            _stopping?.Dispose();
        }

        private ChannelHandlerDelegate BuildPipeline()
        {
            var pipeline = new ChannelPipeline();
            Configure(pipeline);
            return pipeline.Build();
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            var logger = CreateLogger();

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var connection = await _listener!.AcceptAsync(ct).ConfigureAwait(false);
                    if (connection is null)
                        break;

                    if (_options.MaxConnections > 0 && Volatile.Read(ref _connectionCount) >= _options.MaxConnections)
                    {
                        logger?.LogWarning("连接数已达到上限 {MaxConnections}，拒绝新连接。", _options.MaxConnections);

                        try
                        {
                            connection.Abort(new ConnectionAbortedException("连接数已达到上限。"));
                        }
                        catch (ObjectDisposedException)
                        {
                        }

                        await connection.DisposeAsync().ConfigureAwait(false);
                        continue;
                    }

                    var id = Guid.NewGuid().ToString("N");
                    var scope = ScopeFactory?.CreateScope() ?? EmptyServiceScope.Instance;
                    var context = new ChannelContext(id, connection, scope.ServiceProvider);

                    _activeConnections[id] = context;

                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _activeTasks[id] = completion.Task;
                    _ = ProcessConnectionAsync(id, context, scope, completion);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "接受连接循环异常终止。");
            }
        }

        private async Task ProcessConnectionAsync(
            string id,
            ChannelContext context,
            IServiceScope scope,
            TaskCompletionSource completion)
        {
            Interlocked.Increment(ref _connectionCount);
            Exception? error = null;

            try
            {
                await _pipeline!(context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error = ex;
                CreateLogger()?.LogError(ex, "连接 {ConnectionId} 的处理管线抛出异常。", id);
            }
            finally
            {
                try
                {
                    await context.CompleteAsync(error).ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        await DisposeScopeAsync(scope).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _connectionCount);
                        _activeConnections.TryRemove(id, out _);
                        _activeTasks.TryRemove(id, out _);
                        completion.TrySetResult();
                    }
                }
            }
        }

        private async Task StopCoreAsync()
        {
            _stopping?.Cancel();

            if (_listener is not null)
            {
                try
                {
                    await _listener.UnbindAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            var acceptLoop = _acceptLoop;
            if (acceptLoop is not null)
            {
                try
                {
                    await acceptLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            var tasks = _activeTasks.Values.ToArray();
            if (tasks.Length > 0)
            {
                var all = Task.WhenAll(tasks);
                using var timeoutCts = new CancellationTokenSource(_options.ShutdownTimeout);
                var timeout = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);

                if (await Task.WhenAny(all, timeout).ConfigureAwait(false) != all)
                {
                    var reason = new TimeoutException(
                        $"在 {_options.ShutdownTimeout} 内仍有连接未完成，强制中止。");

                    foreach (var context in _activeConnections.Values)
                        context.Abort(reason);

                    try
                    {
                        await all.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }
                }
                else
                {
                    timeoutCts.Cancel();
                    await all.ConfigureAwait(false);
                }
            }

            if (_listener is not null)
            {
                try
                {
                    await _listener.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }
        }

        private static async ValueTask DisposeScopeAsync(IServiceScope scope)
        {
            if (scope is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                return;
            }

            scope.Dispose();
        }

        private ILogger? CreateLogger()
            => LoggerFactory?.CreateLogger("Microsoft.Extensions.Hosting.Net.ChannelServer");
    }
}
```

- [x] 4.5 构建 Primitives 并修复编译错误（完成）

**Files:**
- Modify: 按编译器提示定位

**Step 1: 构建**

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: `Build succeeded`，0 warnings 0 errors（若出现 CS1591 说明 XML 注释缺失，补 `<summary>`）。

**Step 2: 常见错误处理**

- `Microsoft.AspNetCore.*` 未找到 → 确认 Task 1.2 的 `FrameworkReference` 已保存。
- `ChannelPipeline` 不可访问 → 确认测试项目只通过 internal 类型访问，且 `InternalsVisibleTo` 名称与测试程序集名一致。
- `TaskCompletionSource` 无泛型 → 使用非泛型 `TaskCompletionSource`（.NET 8+ 支持）。

## 5. 集成测试

- [x] 5.1 服务器生命周期测试（完成）

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/ServerLifecycleTests.cs`

**Step 1: 写入测试**

```csharp
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class ServerLifecycleTests
{
    [Fact]
    public async Task StartAsync_ReturnsAfterBinding_AndExposesLocalEndPoint()
    {
        await using var handle = await TestServer.StartAsync(_ => Task.CompletedTask);

        Assert.NotNull(handle.Server.LocalEndPoint);
        Assert.IsType<IPEndPoint>(handle.Server.LocalEndPoint);
        Assert.NotEqual(0, handle.EndPoint.Port);
    }

    [Fact]
    public async Task StartAsync_ThrowsSocketException_WhenPortInUse()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;

        var server = new TestChannelServer(
            new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, port) },
            _ => Task.CompletedTask);

        await Assert.ThrowsAsync<SocketException>(() => server.StartAsync(CancellationToken.None));
        Assert.Null(server.LocalEndPoint);
    }

    [Fact]
    public async Task RunAsync_BlocksUntilCancelled_AndStopsServer()
    {
        var server = new TestChannelServer(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ShutdownTimeout = TimeSpan.FromSeconds(1),
            },
            TestHandlers.EchoAsync);

        using var cts = new CancellationTokenSource();
        var runTask = server.RunAsync(cts.Token);
        await TestClient.WaitUntilListeningAsync(server);

        using (var client = await TestClient.ConnectAsync((IPEndPoint)server.LocalEndPoint!))
        {
            var stream = client.GetStream();
            await stream.WriteAsync("ping"u8.ToArray());
            var echoed = await TestClient.ReadExactlyAsync(stream, 4);
            Assert.Equal("ping", System.Text.Encoding.UTF8.GetString(echoed));
        }

        cts.Cancel();
        await runTask.WaitAsync(TestWaits.DefaultTimeout);
        Assert.True(runTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StartAsync_IsIdempotent()
    {
        await using var handle = await TestServer.StartAsync(_ => Task.CompletedTask);

        await handle.Server.StartAsync(CancellationToken.None);
        Assert.NotNull(handle.Server.LocalEndPoint);
    }
}
```

**Step 2: 运行**

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ServerLifecycleTests"`
Expected: 4 个测试通过。

- [x] 5.2 传输与部分发送测试（完成）

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/TransportTests.cs`

**Step 1: 写入测试**

```csharp
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class TransportTests
{
    [Fact]
    public async Task Echo_ReturnsSameBytes()
    {
        await using var handle = await TestServer.StartAsync(TestHandlers.EchoAsync);
        using var client = await TestClient.ConnectAsync(handle.EndPoint);
        var stream = client.GetStream();

        await stream.WriteAsync("hello"u8.ToArray());
        var echoed = await TestClient.ReadExactlyAsync(stream, 5);

        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(echoed));
    }

    [Fact]
    public async Task Echo_HandlesOneMegabyteInBothDirections()
    {
        await using var handle = await TestServer.StartAsync(TestHandlers.EchoAsync);
        using var client = await TestClient.ConnectAsync(handle.EndPoint);
        var stream = client.GetStream();

        var payload = new byte[1024 * 1024];
        Random.Shared.NextBytes(payload);

        var readTask = TestClient.ReadExactlyAsync(stream, payload.Length);
        await stream.WriteAsync(payload);
        var echoed = await readTask;

        Assert.Equal(payload, echoed);
    }

    [Fact]
    public async Task Framework_CompletesTransport_WhenHandlerReturns()
    {
        await using var handle = await TestServer.StartAsync(_ => Task.CompletedTask);
        using var client = await TestClient.ConnectAsync(handle.EndPoint);
        var stream = client.GetStream();

        var read = await TestClient.ReadOneByteAsync(stream);

        Assert.Equal(0, read);
    }

    [Fact]
    public async Task MaxConnections_RejectsExcessConnections()
    {
        var release = new CancellationTokenSource();
        await using var handle = await TestServer.StartAsync(
            context => TestHandlers.HoldAsync(context, release.Token),
            options => options.MaxConnections = 1);

        using var first = await TestClient.ConnectAsync(handle.EndPoint);
        await Task.Delay(50);

        using var second = await TestClient.ConnectAsync(handle.EndPoint);
        var read = await TestClient.ReadOneByteAsync(second.GetStream());

        Assert.Equal(0, read);

        release.Cancel();
    }
}
```

**Step 2: 运行**

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~TransportTests"`
Expected: 4 个测试通过。

- [x] 5.3 连接身份、关闭顺序与作用域测试（完成）

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/ConnectionLifecycleTests.cs`

**Step 1: 写入测试**

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class ConnectionLifecycleTests
{
    [Fact]
    public async Task EachConnection_GetsUniqueId()
    {
        var ids = new ConcurrentBag<string>();
        await using var handle = await TestServer.StartAsync(context =>
        {
            ids.Add(context.ConnectionId);
            return Task.CompletedTask;
        });

        using (var first = await TestClient.ConnectAsync(handle.EndPoint))
        {
            await TestClient.ReadOneByteAsync(first.GetStream());
        }

        using (var second = await TestClient.ConnectAsync(handle.EndPoint))
        {
            await TestClient.ReadOneByteAsync(second.GetStream());
        }

        await WaitForAsync(() => ids.Count == 2);

        Assert.Equal(2, ids.Distinct().Count());
        Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
    }

    [Fact]
    public async Task ConnectionClosed_FiresOnce_BeforeScopeDisposal()
    {
        var factory = new TrackingScopeFactory();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closedCount = 0;

        await using var handle = await TestServer.StartAsync(
            context =>
            {
                context.ConnectionClosed.Register(() =>
                {
                    factory.Record("closed");
                    Interlocked.Increment(ref closedCount);
                    closed.TrySetResult();
                });
                return Task.CompletedTask;
            },
            scopeFactory: factory);

        using (var client = await TestClient.ConnectAsync(handle.EndPoint))
        {
            await TestClient.ReadOneByteAsync(client.GetStream());
        }

        await closed.Task.WaitAsync(TestWaits.DefaultTimeout);
        await factory.Disposed.Task.WaitAsync(TestWaits.DefaultTimeout);

        Assert.Equal(1, Volatile.Read(ref closedCount));
        Assert.Equal(new[] { "closed", "scope-disposed" }, factory.Events);
        Assert.Equal(1, Volatile.Read(ref factory.CreatedCount));
    }

    [Fact]
    public async Task PeerDisconnect_TriggersConnectionClosed()
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var handle = await TestServer.StartAsync(async context =>
        {
            context.ConnectionClosed.Register(() => closed.TrySetResult());
            await TestHandlers.EchoAsync(context);
        });

        using (var client = await TestClient.ConnectAsync(handle.EndPoint))
        {
            client.Close();
        }

        await closed.Task.WaitAsync(TestWaits.DefaultTimeout);
        Assert.True(closed.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task HandlerException_AbortsOnlyThatConnection_AndServerKeepsServing()
    {
        var first = 1;
        await using var handle = await TestServer.StartAsync(async context =>
        {
            if (Interlocked.Exchange(ref first, 0) == 1)
                throw new InvalidOperationException("boom");

            await TestHandlers.EchoAsync(context);
        });

        using (var failing = await TestClient.ConnectAsync(handle.EndPoint))
        {
            Assert.Equal(0, await TestClient.ReadOneByteAsync(failing.GetStream()));
        }

        using var second = await TestClient.ConnectAsync(handle.EndPoint);
        var stream = second.GetStream();
        await stream.WriteAsync("ok"u8.ToArray());
        Assert.Equal("ok", System.Text.Encoding.UTF8.GetString(await TestClient.ReadExactlyAsync(stream, 2)));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TestWaits.DefaultTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("条件在超时时间内未满足。");

            await Task.Delay(10);
        }
    }
}
```

**Step 2: 运行**

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ConnectionLifecycleTests"`
Expected: 4 个测试通过。

- [x] 5.4 优雅停机测试（完成）

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/ShutdownTests.cs`

**Step 1: 写入测试**

```csharp
using System.Net.Sockets;
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class ShutdownTests
{
    [Fact]
    public async Task StopAsync_WaitsForInflightConnection()
    {
        using var release = new CancellationTokenSource();
        await using var handle = await TestServer.StartAsync(
            context => TestHandlers.HoldAsync(context, release.Token),
            options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));

        using var client = await TestClient.ConnectAsync(handle.EndPoint);
        var stopTask = handle.Server.StopAsync(CancellationToken.None);

        await Task.Delay(100);
        Assert.False(stopTask.IsCompleted);

        release.Cancel();
        await stopTask.WaitAsync(TestWaits.DefaultTimeout);
        Assert.True(stopTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StopAsync_AbortsConnections_WhenShutdownTimeoutExceeded()
    {
        using var never = new CancellationTokenSource();
        await using var handle = await TestServer.StartAsync(
            context => TestHandlers.HoldAsync(context, never.Token),
            options => options.ShutdownTimeout = TimeSpan.FromMilliseconds(200));

        using var client = await TestClient.ConnectAsync(handle.EndPoint);
        var stream = client.GetStream();

        var stopTask = handle.Server.StopAsync(CancellationToken.None);
        await stopTask.WaitAsync(TestWaits.DefaultTimeout);

        var read = await TestClient.ReadOneByteAsync(stream);
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task StopAsync_And_DisposeAsync_AreIdempotent()
    {
        var handle = await TestServer.StartAsync(_ => Task.CompletedTask);

        await handle.Server.StopAsync(CancellationToken.None);
        await handle.Server.StopAsync(CancellationToken.None);
        await handle.Server.DisposeAsync();
        await handle.Server.DisposeAsync();
    }

    [Fact]
    public async Task DisposeWithoutStart_DoesNotThrow()
    {
        var server = new TestChannelServer(new ChannelServerOptions(), _ => Task.CompletedTask);

        await server.DisposeAsync();
        Assert.Null(server.LocalEndPoint);
    }
}
```

**Step 2: 运行**

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ShutdownTests"`
Expected: 4 个测试通过。

- [x] 5.5 管线集成测试（完成）

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/PipelineIntegrationTests.cs`

**Step 1: 写入测试**

```csharp
using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class PipelineIntegrationTests
{
    [Fact]
    public async Task Middleware_RunsInOrder_AroundHandler()
    {
        var events = new ConcurrentQueue<string>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var server = new ConfiguredTestServer(
            new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
            pipeline =>
            {
                pipeline.Use(async (context, next) =>
                {
                    events.Enqueue("A-in");
                    await next(context);
                    events.Enqueue("A-out");
                    completed.TrySetResult();
                });
                pipeline.Use(async (context, next) =>
                {
                    events.Enqueue("B-in");
                    await next(context);
                    events.Enqueue("B-out");
                });
                pipeline.Run(_ =>
                {
                    events.Enqueue("handler");
                    return Task.CompletedTask;
                });
            },
            _ => Task.CompletedTask);

        await server.StartAsync(CancellationToken.None);
        var endPoint = (IPEndPoint)server.LocalEndPoint!;

        using (var client = await TestClient.ConnectAsync(endPoint))
        {
            await TestClient.ReadOneByteAsync(client.GetStream());
        }

        await completed.Task.WaitAsync(TestWaits.DefaultTimeout);
        Assert.Equal(new[] { "A-in", "B-in", "handler", "B-out", "A-out" }, events);

        await server.DisposeAsync();
    }

    [Fact]
    public async Task ShortCircuitMiddleware_SkipsHandler()
    {
        var handlerCalled = false;
        var server = new ConfiguredTestServer(
            new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
            pipeline =>
            {
                pipeline.Use((context, next) => Task.CompletedTask);
                pipeline.Run(_ =>
                {
                    handlerCalled = true;
                    return Task.CompletedTask;
                });
            },
            _ => Task.CompletedTask);

        await server.StartAsync(CancellationToken.None);
        await using var handle = new TestServerHandle(server, (IPEndPoint)server.LocalEndPoint!);

        using (var client = await TestClient.ConnectAsync(handle.EndPoint))
        {
            Assert.Equal(0, await TestClient.ReadOneByteAsync(client.GetStream()));
        }

        Assert.False(handlerCalled);
    }

    [Fact]
    public async Task Configure_WithTwoRunCalls_Throws()
    {
        var server = new ConfiguredTestServer(
            new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
            pipeline =>
            {
                pipeline.Run(_ => Task.CompletedTask);
                pipeline.Run(_ => Task.CompletedTask);
            },
            _ => Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync(CancellationToken.None));
    }
}
```

**Step 2: 运行**

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~PipelineIntegrationTests"`
Expected: 3 个测试通过。

- [x] 5.6 全量测试（完成）

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
Expected: 全部通过（27 个用例），0 failed。若出现偶发失败，检查是否使用了真实 `Task.Delay` 轮询；统一改为 `WaitAsync` + `TaskCompletionSource`。

## 6. 消费方迁移与文档

- [x] 6.1 NetServer 改用 RunAsync（完成）

**Files:**
- Modify: `src/NetServer.ConsoleApp/ListenerBootstrapper.cs:12-23`

**Step 1: 修改启动调用**

将：

```csharp
                var srv = sp.GetRequiredService<SampleChannelServer>();
                await srv.StartAsync(stoppingToken);
```

改为：

```csharp
                var srv = sp.GetRequiredService<SampleChannelServer>();
                await srv.RunAsync(stoppingToken);
```

**Step 2: 构建消费方**

Run: `dotnet build src/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`
Expected: `Build succeeded`。

**Step 3: 冒烟运行**

Run: `dotnet run --project src/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`
Expected: 控制台每秒打印 `>>>>> hello <<<<<`；按 Ctrl+C 后进程正常退出，无 `NotImplementedException`。

- [ ] 6.2 编写 Primitives README

**Files:**
- Create: `src/Galosys.Foundation.Primitives/README.md`

**Step 1: 写入内容**

```markdown
# Galosys.Foundation.Primitives

服务端 socket 传输层的最小基架，命名空间 `Microsoft.Extensions.Hosting.Net`。

## 能力

- 基于 Kestrel 公共 transport（`SocketTransportFactory` + `IConnectionListener`），不复制 Kestrel 内部实现。
- `ChannelContext`：只读连接视图（`ConnectionId`、端点、`Transport`、`ConnectionClosed`、`Services`）。
- 每连接独立 `IServiceScope`，连接关闭令牌先于作用域释放。
- `ChannelServerBase.Configure(IChannelPipeline)`：中间件只包装 `Transport`，业务数据由终端处理器消费。
- 优雅停机：停止 accept → 等待在途连接 → `ShutdownTimeout` 后强制 abort。

## 最小用法

```csharp
public sealed class EchoServer : ChannelServerBase
{
    public EchoServer(EndPoint endPoint) : base(endPoint) { }

    protected override async Task ChannelHandleAsync(ChannelContext context)
    {
        while (true)
        {
            var result = await context.Reader.ReadAsync();
            if (result.IsCompleted && result.Buffer.IsEmpty) break;

            foreach (var segment in result.Buffer)
                await context.Writer.WriteAsync(segment);

            context.Reader.AdvanceTo(result.Buffer.End);
            await context.Writer.FlushAsync();
        }
    }
}

var server = new EchoServer(new IPEndPoint(IPAddress.Loopback, 8000));
await server.RunAsync(cancellationToken);
```

`StartAsync` 只负责绑定并启动接受循环，返回即可连接；`RunAsync` 是“阻塞到停机”的兼容入口。

## 配置

`ChannelServerOptions` 覆盖 `EndPoint`、`Backlog`、`MaxConnections`、`NoDelay`、`KeepAlive`、`LingerState`、`ReuseAddress`、`DualMode`、`ShutdownTimeout`、读写缓冲、`IOQueueCount`、`CreateBoundListenSocket`。非法值在绑定前抛出 `ArgumentException`。

## 约束

- 处理器不得 `Complete` 或释放 `Transport`，收尾由框架完成。
- 中间件只能包装 `Transport`，不得读取业务字节。
- 一个连接只有一个 Reader 消费者、一个 Writer 生产者。
- `ConnectionClosed` 恰好触发一次，且在 scope 释放之前。

## 构建与测试

```powershell
dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj
dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj
```

## 阶段

P1（本阶段）：服务端内核、Options、管线、每连接 scope、优雅停机。消息层、TLS、客户端、NamedPipe/Diagnostics 在后续阶段。
```

- [x] 6.3 最终验证（完成）

**Step 1: 三个项目分别构建**

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: `Build succeeded`。

Run: `dotnet build src/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`
Expected: `Build succeeded`。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
Expected: 全部通过，0 failed。

**Step 2: 记录结果**

在 `openspec/changes/primitives-socket-p1-1/tasks.md` 勾选所有任务，并把测试数量与耗时写入执行记录。
