# P1.5a-1 服务器内核抽取与组合入口 Implementation Plan

> **REQUIRED SUB-SKILL:** 使用 `executing-plans` 或 `opsx-apply-x` 逐任务执行本计划。
> 本仓库虽有 git，但按约定不自动提交；步骤中的“验证”统一用构建 + 测试，不执行 git commit。

**Goal:** 在不破坏现有 `ChannelServerBase`、sample 和 tests 的前提下，抽出 internal `ChannelServerCore`，新增 sealed `ChannelServer` 组合入口。

**Architecture:** `ChannelServerCore` 拥有 accept loop、连接跟踪、每连接 scope、优雅停机与释放逻辑；`ChannelServer` 和 `ChannelServerBase` 都只持有 core 并转发。`ChannelServerBase` 仍把 `Configure` 作为 pipeline 回调传入 core，因此现有子类零改动。

**Tech Stack:** .NET 10、`System.Net.Sockets`、`System.IO.Pipelines`、xunit 2.9.3、Microsoft.Extensions.DependencyInjection/Logging Abstractions。

---

## 1. 测试先行：组合入口回显

- [x] 1.1 写 `ChannelServer` 回显失败测试 — 已完成：`ChannelServerTests` 覆盖回显、生命周期与 pipeline 配置。

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/ChannelServerTests.cs`

**Step 1: 写入最小测试**

```csharp
using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class ChannelServerTests
{
    [Fact]
    public async Task StartAsync_BindsAndEchoes()
    {
        var server = new ChannelServer(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ShutdownTimeout = TimeSpan.FromSeconds(2),
            },
            pipeline => pipeline.Run(TestHandlers.EchoAsync));

        await server.StartAsync(CancellationToken.None);

        try
        {
            var endPoint = (IPEndPoint)server.LocalEndPoint!;
            using (var client = await TestClient.ConnectAsync(endPoint))
            {
                var stream = client.GetStream();

                await stream.WriteAsync("ping"u8.ToArray());
                var echoed = await TestClient.ReadExactlyAsync(stream, 4);

                Assert.Equal("ping", Encoding.UTF8.GetString(echoed));
            }
        }
        finally
        {
            await server.DisposeAsync();
        }
    }
}
```

**Step 2: 运行测试，确认编译失败**

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~ChannelServerTests`
Expected: 编译失败，提示 `ChannelServer` 不存在（这是本任务的“红”）。

## 2. 抽取内核并接入组合入口

- [x] 2.1 创建 internal `ChannelServerCore` — 已完成：生命周期内核已抽取。

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/ChannelServerCore.cs`

**Step 1: 复制生命周期字段**

从 `ChannelServerBase.cs` 复制以下成员到 `ChannelServerCore`：

- `AcceptLoopStopTimeout`、`AbortGracePeriod`；
- `_options`、`_activeConnections`、`_activeTasks`、`_stopped`、`_disposeCompleted`、`_startLock`、`_listener`、`_pipeline`、`_stopping`、`_acceptLoop`、`_acceptLoopError`、`_connectionCount`、`_stopStarted`、`_disposed`；
- `LocalEndPoint`；
- `StartAsync`、`RunAsync`、`StopAsync`、`Dispose`、`DisposeAsync`；
- `BuildPipeline`、`AcceptLoopAsync`、`ThrowIfAcceptLoopTerminated`、`ProcessConnectionAsync`、`StopCoreAsync`、`WaitForInflightAsync`、`DisposeScopeAsync`、`CreateLogger`。

**Step 2: 建立构造函数与属性**

文件使用与 `ChannelServerBase.cs` 相同的 `namespace Microsoft.Extensions.Hosting.Net { ... }` 块命名空间。

```csharp
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

internal sealed class ChannelServerCore
{
    private readonly ChannelServerOptions _options;
    private readonly Action<IChannelPipeline> _configurePipeline;

    public ChannelServerCore(
        ChannelServerOptions options,
        Action<IChannelPipeline> configurePipeline,
        ILoggerFactory? loggerFactory,
        IServiceScopeFactory? scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configurePipeline);

        _options = options.Clone();
        _configurePipeline = configurePipeline;
        LoggerFactory = loggerFactory;
        ScopeFactory = scopeFactory;
    }

    internal IServiceScopeFactory? ScopeFactory { get; set; }

    internal ILoggerFactory? LoggerFactory { get; set; }

    public EndPoint? LocalEndPoint { get; private set; }
}
```

**Step 3: 替换 pipeline 构建**

把原 `BuildPipeline` 中的：

```csharp
Configure(pipeline);
```

改为：

```csharp
_configurePipeline(pipeline);
```

**Step 4: 保留原有控制流**

其余方法体按原样搬移，不重命名日志文本、异常类型、超时值或 `ThreadPool.UnsafeQueueUserWorkItem` 的停机逻辑。

- [x] 2.2 新增 sealed `ChannelServer` — 已完成：组合式入口全部转发 core。

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/ChannelServer.cs`

```csharp
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>基于组合的服务器入口。</summary>
    public sealed class ChannelServer : IChannelServer
    {
        private readonly ChannelServerCore _core;

        /// <summary>创建组合式服务器。</summary>
        public ChannelServer(
            ChannelServerOptions options,
            Action<IChannelPipeline> configurePipeline,
            ILoggerFactory? loggerFactory = null,
            IServiceScopeFactory? scopeFactory = null)
        {
            _core = new ChannelServerCore(options, configurePipeline, loggerFactory, scopeFactory);
        }

        /// <inheritdoc />
        public EndPoint? LocalEndPoint => _core.LocalEndPoint;

        /// <inheritdoc />
        public Task StartAsync(CancellationToken ct) => _core.StartAsync(ct);

        /// <inheritdoc />
        public Task RunAsync(CancellationToken ct) => _core.RunAsync(ct);

        /// <inheritdoc />
        public Task StopAsync(CancellationToken ct) => _core.StopAsync(ct);

        /// <inheritdoc />
        public ValueTask DisposeAsync() => _core.DisposeAsync();

        /// <inheritdoc />
        public void Dispose() => _core.Dispose();
    }
}
```

- [x] 2.3 把 `ChannelServerBase` 改为 core 适配器 — 已完成：基类保留签名并委托 core。

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/ChannelServerBase.cs`

**Step 1: 删除已搬走的字段、private 方法和接口实现**

删除所有已搬走的字段（包括 `AcceptLoopStopTimeout` / `AbortGracePeriod`）、private 方法和接口实现；保留 XML 文档和公共/受保护签名。

**Step 2: 用以下结构替换类主体**

```csharp
public abstract class ChannelServerBase : IChannelServer
{
    private readonly ChannelServerCore _core;

    protected ChannelServerBase(EndPoint endPoint)
        : this(new ChannelServerOptions { EndPoint = endPoint })
    {
    }

    protected ChannelServerBase(ChannelServerOptions options)
    {
        _core = new ChannelServerCore(options, Configure, loggerFactory: null, scopeFactory: null);
    }

    internal IServiceScopeFactory? ScopeFactory
    {
        get => _core.ScopeFactory;
        set => _core.ScopeFactory = value;
    }

    internal ILoggerFactory? LoggerFactory
    {
        get => _core.LoggerFactory;
        set => _core.LoggerFactory = value;
    }

    public EndPoint? LocalEndPoint => _core.LocalEndPoint;

    protected virtual void Configure(IChannelPipeline pipeline) => pipeline.Run(ChannelHandleAsync);

    protected abstract Task ChannelHandleAsync(ChannelContext context);

    public Task StartAsync(CancellationToken ct) => _core.StartAsync(ct);

    public Task RunAsync(CancellationToken ct) => _core.RunAsync(ct);

    public Task StopAsync(CancellationToken ct) => _core.StopAsync(ct);

    public ValueTask DisposeAsync() => _core.DisposeAsync();

    public void Dispose() => _core.Dispose();
}
```

**Step 3: 清理 using**

只保留 `System.Net`、`Microsoft.Extensions.DependencyInjection`、`Microsoft.Extensions.Logging`、`Microsoft.Extensions.Options` 中实际使用的项。

- [x] 2.4 构建并跑首个组合入口测试 — 已完成：构建 0 错误，`ChannelServerTests` 通过。

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~ChannelServerTests`
Expected: `StartAsync_BindsAndEchoes` 通过。

## 3. 补齐组合入口行为测试

- [x] 3.1 增加端口占用、缺少 Run、独立 options、RunAsync、pipeline 配置测试 — 已完成：7 个测试覆盖。

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ChannelServerTests.cs`

**Step 1: 在测试类中追加以下测试**

```csharp
[Fact]
public async Task StartAsync_ThrowsSocketException_WhenPortInUse()
{
    using var blocker = new TcpListener(IPAddress.Loopback, 0);
    blocker.Start();
    var port = ((IPEndPoint)blocker.LocalEndpoint).Port;

    var server = new ChannelServer(
        new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, port) },
        pipeline => pipeline.Run(TestHandlers.EchoAsync));

    await Assert.ThrowsAsync<SocketException>(() => server.StartAsync(CancellationToken.None));
    Assert.Null(server.LocalEndPoint);

    await server.DisposeAsync();
}

[Fact]
public async Task StartAsync_Throws_WhenPipelineHasNoRun()
{
    var server = new ChannelServer(
        new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
        _ => { });

    await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync(CancellationToken.None));
    Assert.Null(server.LocalEndPoint);

    await server.DisposeAsync();
}

[Fact]
public async Task Instances_UseIndependentOptions()
{
    var firstOptions = new ChannelServerOptions
    {
        EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        ShutdownTimeout = TimeSpan.FromSeconds(2),
    };
    var secondOptions = new ChannelServerOptions
    {
        EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        ShutdownTimeout = TimeSpan.FromSeconds(3),
    };

    var first = new ChannelServer(firstOptions, pipeline => pipeline.Run(TestHandlers.EchoAsync));
    var second = new ChannelServer(secondOptions, pipeline => pipeline.Run(TestHandlers.EchoAsync));

    await first.StartAsync(CancellationToken.None);
    await second.StartAsync(CancellationToken.None);

    try
    {
        var firstEndPoint = (IPEndPoint)first.LocalEndPoint!;
        var secondEndPoint = (IPEndPoint)second.LocalEndPoint!;

        Assert.NotEqual(firstEndPoint.Port, secondEndPoint.Port);

        await first.StopAsync(CancellationToken.None);
        Assert.Null(first.LocalEndPoint);
        Assert.NotNull(second.LocalEndPoint);
    }
    finally
    {
        await first.DisposeAsync();
        await second.DisposeAsync();
    }
}

[Fact]
public async Task Options_AreClonedAtConstruction()
{
    var options = new ChannelServerOptions
    {
        EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
    };
    var server = new ChannelServer(options, pipeline => pipeline.Run(TestHandlers.EchoAsync));

    options.EndPoint = new IPEndPoint(IPAddress.Loopback, 1);

    await server.StartAsync(CancellationToken.None);

    try
    {
        Assert.NotEqual(1, ((IPEndPoint)server.LocalEndPoint!).Port);
    }
    finally
    {
        await server.DisposeAsync();
    }
}

[Fact]
public async Task RunAsync_Cancel_StopsServer()
{
    using var cts = new CancellationTokenSource();
    var server = new ChannelServer(
        new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
        pipeline => pipeline.Run(TestHandlers.EchoAsync));

    var runTask = server.RunAsync(cts.Token);
    await TestClient.WaitUntilAsync(
        () => Task.FromResult(server.LocalEndPoint is not null),
        "服务器未在超时时间内绑定。");

    cts.Cancel();
    await runTask.WaitAsync(TestWaits.DefaultTimeout);

    Assert.Null(server.LocalEndPoint);
    await server.DisposeAsync();
}

[Fact]
public async Task PipelineConfiguration_IsApplied()
{
    var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    var server = new ChannelServer(
        new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
        pipeline =>
        {
            pipeline.Use((context, next) =>
            {
                invoked.TrySetResult();
                return next(context);
            });
            pipeline.Run(_ => Task.CompletedTask);
        });

    await server.StartAsync(CancellationToken.None);

    try
    {
        using var client = await TestClient.ConnectAsync((IPEndPoint)server.LocalEndPoint!);
        await invoked.Task.WaitAsync(TestWaits.DefaultTimeout);
    }
    finally
    {
        await server.DisposeAsync();
    }
}
```

**Step 2: 补 using**

文件顶部需要 `using System.Net.Sockets;`。

- [x] 3.2 运行组合入口测试 — 已完成：全部通过。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~ChannelServerTests`
Expected: 全部 `ChannelServerTests` 通过。

## 4. 回归与文档

- [x] 4.1 更新 Primitives README 的组合式用法 — 已完成：组合式入口与宿主接入示例已写入。

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/README.md`

在“## 最小用法”之前增加：

```markdown
## 组合式用法（P1.5a）

```csharp
var server = new ChannelServer(
    new ChannelServerOptions
    {
        EndPoint = new IPEndPoint(IPAddress.Loopback, 8000),
    },
    pipeline => pipeline.Run(HandleAsync),
    loggerFactory);

await server.RunAsync(cancellationToken);
```

`ChannelServer` 是 sealed 类型，通过构造函数接收 options 与 pipeline 配置，不再需要继承。
`ChannelServerBase` 在本阶段暂时保留，内部与 `ChannelServer` 共享同一个 `ChannelServerCore`；
删除基类与 `AddChannelServer<T>` 在下一个 change 完成。
```

- [x] 4.2 跑全量测试与示例构建 — 已完成：76 个测试通过，NetServer 构建成功。

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

Run: `dotnet build samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`
Expected: 成功；允许既有 `CS9113` 警告。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
Expected: 现有 67 个测试 + 新增 `ChannelServerTests` 全部通过。

- [x] 4.3 校验 OpenSpec artifact — 已完成：`openspec validate --strict` 通过（拆分过长 requirement）。

Run: `openspec validate "primitives-server-composition-1" --strict --no-interactive`
Expected: `Change 'primitives-server-composition-1' is valid`。
