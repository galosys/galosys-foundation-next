# P1.5a-2 删除继承入口并迁移示例 Implementation Plan

> **REQUIRED SUB-SKILL:** 使用 `executing-plans` 或 `opsx-apply-x` 逐任务执行本计划。
> 本仓库虽有 git，但按约定不自动提交；步骤中的“验证”统一用构建 + 测试，不执行 git commit。

**Goal:** 删除 `ChannelServerBase`、`AddChannelServer<T>` 与 `ChannelServerHostedService<T>`，把测试辅助类型和 `NetServer.ConsoleApp` 全部迁到组合式 `ChannelServer`。

**Architecture:** 测试辅助类保留 `TestChannelServer` / `ConfiguredTestServer` 名称，但内部持有 `ChannelServer`，因此大多数测试不用改。宿主集成由调用方注册 `ChannelServer` 工厂和一个 `IHostedService` runner；sample 的 runner 必须先于 `SenderBootstrapper` 注册。

**Tech Stack:** .NET 10、Microsoft.Extensions.Hosting/DI/Logging、xunit 2.9.3、`System.Net.Sockets`。

---

## 1. 删除公开基类与泛型 DI 入口

- [x] 1.1 增加“基类与 DI 扩展已删除”的失败测试 — 已完成：删除前 red 验证通过。

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/NamespaceConventionTests.cs`

在 `NamespaceConventionTests` 中追加：

```csharp
[Fact]
public void RemovedServerBaseAndDiExtensions_AreNotPublic()
{
    var assembly = typeof(ChannelServer).Assembly;

    Assert.Null(assembly.GetType("Microsoft.Extensions.Hosting.Net.ChannelServerBase"));
    Assert.Null(assembly.GetType("Microsoft.Extensions.DependencyInjection.ChannelServerServiceCollectionExtensions"));
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~RemovedServerBaseAndDiExtensions_AreNotPublic`
Expected: 失败，`ChannelServerBase` 或 DI 扩展仍存在（这是本任务的“红”）。

- [x] 1.2 把 `TestSupport` 的辅助类改为 `ChannelServer` 包装 — 已完成：辅助类保留名称并转发。

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/TestSupport.cs`

**Step 1: 文件顶部增加 Hosting using**

```csharp
using Microsoft.Extensions.Hosting;
```

**Step 2: 用以下代码替换 `TestChannelServer` 与 `ConfiguredTestServer` 的类定义**

```csharp
internal sealed class TestChannelServer : IChannelServer
{
    private readonly ChannelServer _server;

    public TestChannelServer(
        ChannelServerOptions options,
        Func<ChannelContext, Task> handler,
        IServiceScopeFactory? scopeFactory = null)
    {
        _server = new ChannelServer(
            options,
            pipeline => pipeline.Run(handler),
            loggerFactory: null,
            scopeFactory: scopeFactory);
    }

    public EndPoint? LocalEndPoint => _server.LocalEndPoint;

    public Task StartAsync(CancellationToken ct) => _server.StartAsync(ct);

    public Task RunAsync(CancellationToken ct) => _server.RunAsync(ct);

    public Task StopAsync(CancellationToken ct) => _server.StopAsync(ct);

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    public void Dispose() => _server.Dispose();
}

internal sealed class ConfiguredTestServer : IChannelServer
{
    private readonly ChannelServer _server;

    public ConfiguredTestServer(
        ChannelServerOptions options,
        Action<IChannelPipeline> configure,
        Func<ChannelContext, Task>? handler = null)
    {
        _ = handler;
        _server = new ChannelServer(options, configure);
    }

    public EndPoint? LocalEndPoint => _server.LocalEndPoint;

    public Task StartAsync(CancellationToken ct) => _server.StartAsync(ct);

    public Task RunAsync(CancellationToken ct) => _server.RunAsync(ct);

    public Task StopAsync(CancellationToken ct) => _server.StopAsync(ct);

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    public void Dispose() => _server.Dispose();
}

internal sealed class TestChannelServerRunner : IHostedService
{
    private readonly ChannelServer _server;

    public TestChannelServerRunner(ChannelServer server) => _server = server;

    public Task StartAsync(CancellationToken ct) => _server.StartAsync(ct);

    public Task StopAsync(CancellationToken ct) => _server.StopAsync(CancellationToken.None);
}
```

**Step 3: 把 `TestServerHandle` 改为接口类型**

```csharp
internal sealed class TestServerHandle : IAsyncDisposable
{
    public TestServerHandle(IChannelServer server, IPEndPoint endPoint)
    {
        Server = server;
        EndPoint = endPoint;
    }

    public IChannelServer Server { get; }

    public IPEndPoint EndPoint { get; }

    public async ValueTask DisposeAsync() => await Server.DisposeAsync().ConfigureAwait(false);
}
```

**Step 4: 修改 `TestServer.StartAsync` 的创建代码**

```csharp
var server = new TestChannelServer(options, handler, scopeFactory);

await server.StartAsync(CancellationToken.None).ConfigureAwait(false);
return new TestServerHandle(server, (IPEndPoint)server.LocalEndPoint!);
```

**Step 5: 修改等待方法签名**

```csharp
public static async Task WaitUntilListeningAsync(IChannelServer server)
```

- [x] 1.3 用 `ChannelServer` 重写 `HostingTests` — 已完成：宿主 runner 与超时停机测试。

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/HostingTests.cs`

**Step 1: 删除 `EchoHostedServer`、`HoldHostedServer` 与 `AddChannelServer_RegistersSameServerInstance`。**

**Step 2: 用以下内容替换测试类主体**

```csharp
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class HostingTests
{
    [Fact]
    public async Task Host_StartAsync_BindsServer_AndEchoes()
    {
        using var host = BuildHost(TestHandlers.EchoAsync, options =>
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));

        await host.StartAsync();

        var server = host.Services.GetRequiredService<ChannelServer>();
        Assert.NotNull(server.LocalEndPoint);

        using (var client = await TestClient.ConnectAsync((IPEndPoint)server.LocalEndPoint!))
        {
            var stream = client.GetStream();
            await stream.WriteAsync("ping"u8.ToArray());
            var echoed = await TestClient.ReadExactlyAsync(stream, 4);

            Assert.Equal("ping", System.Text.Encoding.UTF8.GetString(echoed));
        }

        await host.StopAsync();
    }

    [Fact]
    public async Task Host_StartAsync_Throws_WhenPortInUse()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;

        using var host = BuildHost(TestHandlers.EchoAsync, options =>
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, port));

        await Assert.ThrowsAsync<SocketException>(() => host.StartAsync());
    }

    [Fact]
    public async Task Host_StartAsync_Throws_WhenEndPointMissing()
    {
        using var host = BuildHost(TestHandlers.EchoAsync, _ => { });

        await Assert.ThrowsAsync<ArgumentException>(() => host.StartAsync());
    }

    [Fact]
    public async Task Host_StopAsync_WaitsForInflightConnection()
    {
        using var release = new CancellationTokenSource();

        using var host = BuildHost(
            context => TestHandlers.HoldAsync(context, release.Token),
            options =>
            {
                options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
                options.ShutdownTimeout = TimeSpan.FromSeconds(5);
            });

        await host.StartAsync();
        var server = host.Services.GetRequiredService<ChannelServer>();

        using var client = await TestClient.ConnectAsync((IPEndPoint)server.LocalEndPoint!);

        var stopTask = host.StopAsync();
        await Task.Delay(100);
        Assert.False(stopTask.IsCompleted);

        release.Cancel();
        await stopTask.WaitAsync(TestWaits.DefaultTimeout);
        Assert.True(stopTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Host_Dispose_StopsServer()
    {
        var host = BuildHost(TestHandlers.EchoAsync, options =>
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));

        await host.StartAsync();
        var server = host.Services.GetRequiredService<ChannelServer>();
        var endPoint = (IPEndPoint)server.LocalEndPoint!;

        host.Dispose();

        Assert.Null(server.LocalEndPoint);
        Assert.False(await TestClient.TryConnectAsync(endPoint));
    }

    private static IHost BuildHost(
        Func<ChannelContext, Task> handler,
        Action<ChannelServerOptions> configure)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(sp =>
        {
            var options = new ChannelServerOptions();
            configure(options);

            return new ChannelServer(
                options,
                pipeline => pipeline.Run(handler),
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<IServiceScopeFactory>());
        });
        builder.Services.AddHostedService<TestChannelServerRunner>();
        return builder.Build();
    }
}
```

- [x] 1.4 用 `ChannelServer` 工厂重写 `ScopedHostingTests` — 已完成：作用域工厂显式传入。

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ScopedHostingTests.cs`

**Step 1: 删除 `ScopedProbeServer` 类。**

**Step 2: 用以下内容替换测试方法中的宿主装配**

```csharp
using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

internal sealed class ScopedProbe : IDisposable
{
    public static readonly ConcurrentBag<ScopedProbe> Instances = new();

    private static int _disposedCount;

    public ScopedProbe() => Instances.Add(this);

    public static int DisposedCount => Volatile.Read(ref _disposedCount);

    public static void Reset()
    {
        Instances.Clear();
        Interlocked.Exchange(ref _disposedCount, 0);
    }

    public void Dispose() => Interlocked.Increment(ref _disposedCount);
}

public sealed class ScopedHostingTests
{
    [Fact]
    public async Task EachConnection_GetsIndependentScopedInstance_AndItIsDisposed()
    {
        ScopedProbe.Reset();

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddScoped<ScopedProbe>();
        builder.Services.AddSingleton(sp =>
        {
            var options = new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ShutdownTimeout = TimeSpan.FromSeconds(1),
            };

            return new ChannelServer(
                options,
                pipeline => pipeline.Run(context =>
                {
                    _ = context.Services.GetRequiredService<ScopedProbe>();
                    return Task.CompletedTask;
                }),
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<IServiceScopeFactory>());
        });
        builder.Services.AddHostedService<TestChannelServerRunner>();

        using var host = builder.Build();
        await host.StartAsync();

        var server = host.Services.GetRequiredService<ChannelServer>();
        var endPoint = (IPEndPoint)server.LocalEndPoint!;

        for (var i = 0; i < 2; i++)
        {
            using var client = await TestClient.ConnectAsync(endPoint);
            Assert.Equal(0, await TestClient.ReadOneByteAsync(client.GetStream()));
        }

        await WaitForAsync(() => ScopedProbe.DisposedCount == 2);

        Assert.Equal(2, ScopedProbe.Instances.Count);
        Assert.Equal(2, ScopedProbe.Instances.Distinct().Count());
        Assert.Equal(2, ScopedProbe.DisposedCount);

        await host.StopAsync();
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

- [x] 1.5 更新剩余测试中的基类引用 — 已完成：Shutdown/Dependency/Namespace 测试迁移。

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ShutdownTests.cs`
- Modify: `tests/Galosys.Foundation.Primitives.Tests/DependencyBoundaryTests.cs`
- Modify: `tests/Galosys.Foundation.Primitives.Tests/NamespaceConventionTests.cs`

**Step 1: `ShutdownTests.cs` 把局部变量改为接口**

```csharp
IChannelServer? server = null;
```

**Step 2: `DependencyBoundaryTests.cs` 换程序集锚点**

```csharp
var references = typeof(ChannelServer).Assembly.GetReferencedAssemblies();
```

**Step 3: `NamespaceConventionTests.cs` 用以下内容替换**

```csharp
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class NamespaceConventionTests
{
    [Fact]
    public void OptionsTypes_UseMicrosoftExtensionsOptionsNamespace()
    {
        var assembly = typeof(ChannelServer).Assembly;

        Assert.NotNull(assembly.GetType("Microsoft.Extensions.Options.ChannelServerOptions"));
        Assert.NotNull(assembly.GetType("Microsoft.Extensions.Options.ChannelKeepAliveOptions"));
        Assert.Null(assembly.GetType("Microsoft.Extensions.Hosting.Net.ChannelServerOptions"));
        Assert.Null(assembly.GetType("Microsoft.Extensions.Hosting.Net.ChannelKeepAliveOptions"));
    }

    [Fact]
    public void RemovedServerBaseAndDiExtensions_AreNotPublic()
    {
        var assembly = typeof(ChannelServer).Assembly;

        Assert.Null(assembly.GetType("Microsoft.Extensions.Hosting.Net.ChannelServerBase"));
        Assert.Null(assembly.GetType("Microsoft.Extensions.DependencyInjection.ChannelServerServiceCollectionExtensions"));
    }
}
```

- [x] 1.6 删除基类与 DI 文件 — 已完成：3 个源文件已删除。

**Files:**
- Delete: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/ChannelServerBase.cs`
- Delete: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/DependencyInjection/ChannelServerServiceCollectionExtensions.cs`
- Delete: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Hosting/ChannelServerHostedService.cs`

Run: `Remove-Item -LiteralPath 'src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/ChannelServerBase.cs','src/Galosys.Foundation.Primitives/Microsoft/Extensions/DependencyInjection/ChannelServerServiceCollectionExtensions.cs','src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Hosting/ChannelServerHostedService.cs'`

- [x] 1.7 构建并跑测试项目 — 已完成：构建 0 错误，77 个测试通过。

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
Expected: 全部通过；`RemovedServerBaseAndDiExtensions_AreNotPublic` 通过。

## 2. 迁移 `NetServer.ConsoleApp`

- [x] 2.1 新增示例 runner — 已完成：`ChannelServerRunner`。

**Files:**
- Create: `samples/NetServer.ConsoleApp/ChannelServerRunner.cs`

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Net;

namespace NetServer.ConsoleApp
{
    /// <summary>把 ChannelServer 绑定到宿主启动/停机。</summary>
    public sealed class ChannelServerRunner : IHostedService
    {
        private readonly ChannelServer _server;

        public ChannelServerRunner(ChannelServer server) => _server = server;

        public Task StartAsync(CancellationToken cancellationToken)
            => _server.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken)
            => _server.StopAsync(CancellationToken.None);
    }
}
```

- [x] 2.2 把 `SampleChannelServer` 改为普通 handler — 已完成：`SampleChannelHandler`。

**Files:**
- Delete: `samples/NetServer.ConsoleApp/SampleChannelServer.cs`
- Create: `samples/NetServer.ConsoleApp/SampleChannelHandler.cs`

```csharp
using System.Text;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Logging;

namespace NetServer.ConsoleApp
{
    public sealed class SampleChannelHandler
    {
        private readonly ILogger<SampleChannelHandler> _logger;

        public SampleChannelHandler(ILogger<SampleChannelHandler> logger)
        {
            _logger = logger;
        }

        public async Task HandleAsync(ChannelContext context)
        {
            var reader = context.Reader;

            while (true)
            {
                var rt = await reader.ReadAsync();
                var buffer = rt.Buffer;

                if (!buffer.IsEmpty)
                {
                    var str = Encoding.UTF8.GetString(buffer);
                    _logger.LogInformation(">>>>> {str} <<<<<", str);
                }

                reader.AdvanceTo(buffer.End);

                if (rt.IsCompleted || rt.IsCanceled)
                    break;
            }
        }
    }
}
```

- [x] 2.3 重写 `Program.cs` — 已完成：工厂注册 + runner 顺序正确。

**Files:**
- Modify: `samples/NetServer.ConsoleApp/Program.cs`

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetServer.ConsoleApp;
using System.Net;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<SampleChannelHandler>();
builder.Services.AddSingleton(sp =>
{
    var options = new ChannelServerOptions
    {
        EndPoint = new IPEndPoint(IPAddress.Any, 8000),
    };
    var handler = sp.GetRequiredService<SampleChannelHandler>();

    return new ChannelServer(
        options,
        pipeline => pipeline.Run(handler.HandleAsync),
        sp.GetRequiredService<ILoggerFactory>(),
        sp.GetRequiredService<IServiceScopeFactory>());
});
builder.Services.AddHostedService<ChannelServerRunner>();
builder.Services.AddHostedService<SenderBootstrapper>();

var host = builder.Build();
host.Run();
```

- [x] 2.4 构建并手动验证示例 — 已完成：构建成功，运行输出 `>>>>> hello <<<<<`；宿主停机/超时路径由测试覆盖。

Run: `dotnet build samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`
Expected: 0 errors；允许既有 `CS9113` 警告。

Run: `dotnet run --project samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`
Expected: 每秒输出 `>>>>> hello <<<<<`；Ctrl+C 后进程退出，无未处理异常。

## 3. README 与最终验证

- [x] 3.1 更新 Primitives README — 已完成：删除继承与 `AddChannelServer` 文档，改为组合式用法。

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/README.md`

**Step 1: 第一段与能力列表改为组合式描述**

```markdown
服务端 socket 传输层的最小基架：核心类型位于 `Microsoft.Extensions.Hosting.Net`；`*Options` 位于 `Microsoft.Extensions.Options`。P1.5a 使用组合式 `ChannelServer`，不提供库内 DI 注册。

## 能力

- 自包含 socket transport：基于 `System.Net.Sockets` + `System.IO.Pipelines`，核心程序集不依赖 `Microsoft.AspNetCore.*` 或 Kestrel。
- `ChannelContext`：连接视图（`ConnectionId`、端点、`Transport`、`ConnectionClosed`、`Services`）；公开属性无 setter，`Items` 为连接内可变字典。
- `ChannelServer`：sealed 组合入口，通过构造函数接收 `ChannelServerOptions`、pipeline 配置回调和可选的 `ILoggerFactory` / `IServiceScopeFactory`。
- 每连接独立 `IServiceScope`（构造 `ChannelServer` 时传入 `IServiceScopeFactory` 后生效），连接关闭令牌先于作用域释放。
- `Action<IChannelPipeline>`：中间件只包装 `Transport`，业务数据由终端处理器消费。
- 优雅停机：停止 accept → 等待在途连接 → `ShutdownTimeout` 后强制 abort。
- 传输层处理部分发送、输入/输出 Pipe 背压、TCP keep-alive、半关闭与连接重置。
- 正常关闭会先排空已提交输出，并短暂读空内核接收缓冲区后再关闭，避免带未读入站数据触发 RST。
```

**Step 2: 用以下内容替换“最小用法”与“宿主接入”两节**

````markdown
## 最小用法

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

`StartAsync` 只负责绑定、监听并启动接受循环，返回即可连接；`RunAsync` 是“启动并阻塞到停机”的兼容入口。`pipeline` 回调在启动时执行一次，必须调用 `Run`。

## 宿主接入

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<SampleHandler>();
builder.Services.AddSingleton(sp =>
{
    var options = new ChannelServerOptions
    {
        EndPoint = new IPEndPoint(IPAddress.Any, 8000),
        ShutdownTimeout = TimeSpan.FromSeconds(10),
    };
    var handler = sp.GetRequiredService<SampleHandler>();

    return new ChannelServer(
        options,
        pipeline => pipeline.Run(handler.HandleAsync),
        sp.GetRequiredService<ILoggerFactory>(),
        sp.GetRequiredService<IServiceScopeFactory>());
});
builder.Services.AddHostedService<ChannelServerRunner>();

var host = builder.Build();
host.Run();
```

`ChannelServerRunner` 用 `IHostedService` 把启动/停机映射到 `ChannelServer`：

```csharp
internal sealed class ChannelServerRunner : IHostedService
{
    private readonly ChannelServer _server;

    public ChannelServerRunner(ChannelServer server) => _server = server;

    public Task StartAsync(CancellationToken ct) => _server.StartAsync(ct);

    public Task StopAsync(CancellationToken ct) => _server.StopAsync(CancellationToken.None);
}
```

- 不提供 `AddChannelServer<T>`；options 由调用方为每个 `ChannelServer` 单独创建。
- runner 必须先于会立即连接服务器的 hosted service 注册，保证绑定先完成。
- 每连接从传入的 `IServiceScopeFactory` 创建独立作用域，可在处理器中通过 `context.Services` 解析 Scoped 服务。
- 宿主 `HostOptions.ShutdownTimeout` 应大于 `ChannelServerOptions.ShutdownTimeout`，否则宿主会先截断停机等待。
- 未配置 `EndPoint` 或配置非法时，`StartAsync()` 抛出 `ArgumentException`，不会进入监听状态。
````

**Step 3: 更新约束与阶段**

```markdown
- 处理器不得 `Complete` 或释放 `Transport`，收尾由框架完成。
- 中间件只能包装 `Transport`，不得读取业务字节。
- 一个连接只有一个 Reader 消费者、一个 Writer 生产者。
- `ConnectionClosed` 令牌的取消发生在 scope 释放之前；通过 `Register` 注册的回调由 `CancellationTokenSource` 调度，不保证全部在 scope 释放前执行完毕。
- 核心库不得引入 `Microsoft.AspNetCore.App` FrameworkReference 或任何 `Microsoft.AspNetCore.*` 程序集。

## 阶段

P1.5a（本阶段）：组合式服务器入口，删除继承与泛型 DI 入口。多服务器托管、IoC 注册在 P1.5b；消息层、TLS、客户端、NamedPipe/Diagnostics 在后续阶段。
```

- [x] 3.2 更新 sample README — 已完成：结构表与说明改为 `ChannelServerRunner` / `SampleChannelHandler`。

**Files:**
- Modify: `samples/NetServer.ConsoleApp/README.md`

用以下内容替换：

```markdown
# NetServer.ConsoleApp

`Galosys.Foundation.Primitives` 的服务端示例：监听 `0.0.0.0:8000`，打印收到的 UTF-8 文本，并每秒发送 `hello` 到本机端口 8000。

## 运行

```powershell
dotnet run --project samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj
```

预期输出（每秒一条）：

```text
>>>>> hello <<<<<
```

按 Ctrl+C 触发宿主优雅停机。

## 结构

| 文件 | 作用 |
|---|---|
| `Program.cs` | 注册 `ChannelServer` 工厂、`ChannelServerRunner` 与 `SenderBootstrapper` |
| `SampleChannelHandler.cs` | 服务端处理器：读取并打印字节 |
| `ChannelServerRunner.cs` | 把 `ChannelServer` 启停映射到宿主 |
| `SenderBootstrapper.cs` | 后台客户端：每秒发送 `hello`（P4 才迁移到框架客户端） |

## 说明

- `ChannelServer` 由工厂创建，不再使用 `AddChannelServer<T>`。
- `ChannelServerRunner` 必须先于 `SenderBootstrapper` 注册，确保 sender 连接前监听已就绪。
- 宿主 `ShutdownTimeout` 默认 30 秒，服务器 `ShutdownTimeout` 默认 10 秒。
- 端口 8000 被占用时，`host.Run()` 在启动阶段失败并输出异常。
```

- [x] 3.3 全量构建与测试 — 已完成：Primitives/NetServer 构建成功，77 个测试通过。

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

Run: `dotnet build samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`
Expected: 0 errors。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
Expected: 全部通过，且没有残留的 `ChannelServerBase` / `AddChannelServer` 测试引用。

- [x] 3.4 校验 OpenSpec artifact — 已完成：`openspec validate --strict` 通过（修正 MODIFIED 场景遗漏）。

Run: `openspec validate "primitives-server-composition-2" --strict --no-interactive`
Expected: `Change 'primitives-server-composition-2' is valid`。
