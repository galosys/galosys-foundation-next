# P1-2 宿主集成与示例迁移 Implementation Plan

> **REQUIRED SUB-SKILL:** 使用 `executing-plans` 或 `opsx-apply-x` 逐任务执行本计划。
> 本仓库不是 git 仓库，计划中的“提交”步骤统一替换为“构建 + 测试”验证，不执行 `git` 命令。

**Goal:** 把 P1-1 的服务端内核接入通用主机：`AddChannelServer<T>`、`ChannelServerHostedService`、每连接容器 scope，并迁移 `NetServer.ConsoleApp`、更新文档。

**Architecture:** `AddChannelServer<T>` 注册 Options 工厂、服务器单例与 hosted service；hosted service 构造时把容器的 `IServiceScopeFactory`/`ILoggerFactory` 写入服务器内部属性，`StartAsync`/`StopAsync` 直接映射到服务器生命周期；示例改为由宿主托管，删除手工 scope 包裹。

**Tech Stack:** .NET 10、Microsoft.Extensions.Hosting/DI、xunit 2.9.3（复用 p1-1 测试项目）。

---

## 1. 宿主层（先红）

- [ ] 1.1 测试项目启用 ASP.NET Core 共享框架

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`

**Step 1: 在 `<ItemGroup>`（`Using Include="Xunit"` 所在组之前或之后）追加**

```xml
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
```

**Step 2: 还原**

Run: `dotnet restore tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
Expected: `Restored ...`。

- [ ] 1.2 写宿主注册与生命周期测试

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/HostingTests.cs`

**Step 1: 写入测试**

```csharp
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

internal sealed class EchoHostedServer : ChannelServerBase
{
    public EchoHostedServer(ChannelServerOptions options)
        : base(options)
    {
    }

    protected override Task ChannelHandleAsync(ChannelContext context) => TestHandlers.EchoAsync(context);
}

internal sealed class HoldHostedServer : ChannelServerBase
{
    public static CancellationTokenSource Release { get; set; } = new();

    public HoldHostedServer(ChannelServerOptions options)
        : base(options)
    {
    }

    protected override Task ChannelHandleAsync(ChannelContext context)
        => TestHandlers.HoldAsync(context, Release.Token);
}

public sealed class HostingTests
{
    [Fact]
    public async Task AddChannelServer_RegistersSameServerInstance()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddChannelServer<EchoHostedServer>(options =>
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));

        using var host = builder.Build();

        var server = host.Services.GetRequiredService<EchoHostedServer>();
        var contract = host.Services.GetRequiredService<IChannelServer>();

        Assert.Same(server, contract);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Host_StartAsync_BindsServer_AndEchoes()
    {
        using var host = BuildHost<EchoHostedServer>(options =>
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));

        await host.StartAsync();

        var server = host.Services.GetRequiredService<EchoHostedServer>();
        Assert.NotNull(server.LocalEndPoint);

        using var client = await TestClient.ConnectAsync((IPEndPoint)server.LocalEndPoint!);
        var stream = client.GetStream();
        await stream.WriteAsync("ping"u8.ToArray());
        var echoed = await TestClient.ReadExactlyAsync(stream, 4);

        Assert.Equal("ping", System.Text.Encoding.UTF8.GetString(echoed));

        await host.StopAsync();
    }

    [Fact]
    public async Task Host_StartAsync_Throws_WhenPortInUse()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;

        using var host = BuildHost<EchoHostedServer>(options =>
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, port));

        await Assert.ThrowsAsync<SocketException>(() => host.StartAsync());
    }

    [Fact]
    public async Task Host_StartAsync_Throws_WhenEndPointMissing()
    {
        using var host = BuildHost<EchoHostedServer>(_ => { });

        await Assert.ThrowsAsync<ArgumentException>(() => host.StartAsync());
    }

    [Fact]
    public async Task Host_StopAsync_WaitsForInflightConnection()
    {
        HoldHostedServer.Release = new CancellationTokenSource();

        using var host = BuildHost<HoldHostedServer>(options =>
        {
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            options.ShutdownTimeout = TimeSpan.FromSeconds(5);
        });

        await host.StartAsync();
        var server = host.Services.GetRequiredService<HoldHostedServer>();

        using var client = await TestClient.ConnectAsync((IPEndPoint)server.LocalEndPoint!);

        var stopTask = host.StopAsync();
        await Task.Delay(100);
        Assert.False(stopTask.IsCompleted);

        HoldHostedServer.Release.Cancel();
        await stopTask.WaitAsync(TestWaits.DefaultTimeout);
        Assert.True(stopTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Host_Dispose_StopsServer()
    {
        var host = BuildHost<EchoHostedServer>(options =>
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));

        await host.StartAsync();
        var server = host.Services.GetRequiredService<EchoHostedServer>();
        var endPoint = (IPEndPoint)server.LocalEndPoint!;

        host.Dispose();

        using var probe = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(
            () => probe.ConnectAsync(endPoint.Address, endPoint.Port).WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static IHost BuildHost<TChannelServer>(Action<ChannelServerOptions> configure)
        where TChannelServer : ChannelServerBase
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddChannelServer<TChannelServer>(configure);
        return builder.Build();
    }
}
```

**Step 2: 构建，确认失败**

Run: `dotnet build tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
Expected: 编译错误 `AddChannelServer` 不存在。

- [ ] 1.3 写每连接作用域测试

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/ScopedHostingTests.cs`

**Step 1: 写入测试**

```csharp
using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Net;

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

internal sealed class ScopedProbeServer : ChannelServerBase
{
    public ScopedProbeServer(ChannelServerOptions options)
        : base(options)
    {
    }

    protected override Task ChannelHandleAsync(ChannelContext context)
    {
        _ = context.Services.GetRequiredService<ScopedProbe>();
        return Task.CompletedTask;
    }
}

public sealed class ScopedHostingTests
{
    [Fact]
    public async Task EachConnection_GetsIndependentScopedInstance_AndItIsDisposed()
    {
        ScopedProbe.Reset();

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddScoped<ScopedProbe>();
        builder.Services.AddChannelServer<ScopedProbeServer>(options =>
        {
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            options.ShutdownTimeout = TimeSpan.FromSeconds(1);
        });

        using var host = builder.Build();
        await host.StartAsync();

        var server = host.Services.GetRequiredService<ScopedProbeServer>();
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

**Step 2: 运行，确认失败**

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~HostingTests|FullyQualifiedName~ScopedHostingTests"`
Expected: 编译失败（`AddChannelServer` 缺失）。

## 2. 宿主层实现

- [ ] 2.1 实现 ChannelServerHostedService

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Hosting/ChannelServerHostedService.cs`

**Step 1: 写入**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>把服务器生命周期映射到通用宿主。</summary>
    internal sealed class ChannelServerHostedService<TChannelServer> : IHostedService
        where TChannelServer : ChannelServerBase
    {
        private readonly TChannelServer _server;

        public ChannelServerHostedService(
            TChannelServer server,
            IServiceScopeFactory scopeFactory,
            ILoggerFactory loggerFactory)
        {
            _server = server;
            _server.ScopeFactory = scopeFactory;
            _server.LoggerFactory = loggerFactory;
        }

        public Task StartAsync(CancellationToken cancellationToken) => _server.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) => _server.StopAsync(CancellationToken.None);
    }
}
```

- [ ] 2.2 实现 AddChannelServer 注册扩展

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Hosting/ChannelServerServiceCollectionExtensions.cs`

**Step 1: 写入**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>服务器 DI 注册扩展。</summary>
    public static class ChannelServerServiceCollectionExtensions
    {
        /// <summary>
        /// 注册服务器 Options、单例与 hosted service。
        /// <typeparamref name="TChannelServer"/> 的构造函数必须可解析
        /// <see cref="ChannelServerOptions"/>（例如 <c>(ChannelServerOptions, ILogger&lt;T&gt;)</c>）。
        /// </summary>
        public static IServiceCollection AddChannelServer<TChannelServer>(
            this IServiceCollection services,
            Action<ChannelServerOptions>? configure = null)
            where TChannelServer : ChannelServerBase
        {
            ArgumentNullException.ThrowIfNull(services);

            services.AddSingleton(_ =>
            {
                var options = new ChannelServerOptions();
                configure?.Invoke(options);
                options.Validate();
                return options;
            });

            services.AddSingleton<TChannelServer>();
            services.AddSingleton<IChannelServer>(sp => sp.GetRequiredService<TChannelServer>());
            services.AddHostedService<ChannelServerHostedService<TChannelServer>>();

            return services;
        }
    }
}
```

**Step 2: 构建**

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: `Build succeeded`。若提示 `AddHostedService` 不可访问，确认已 `using Microsoft.Extensions.Hosting;`。

- [ ] 2.3 运行宿主测试

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~HostingTests|FullyQualifiedName~ScopedHostingTests"`
Expected: 7 个测试通过（HostingTests 6 + ScopedHostingTests 1）。

**排查：**
- `Host_StartAsync_Throws_WhenEndPointMissing` 未抛异常 → 确认 Options 工厂内调用了 `Validate()`，而不是只在 `StartAsync` 校验。
- `Host_Dispose_StopsServer` 未抛 `SocketException` → 确认 `channelServerHostedService.StopAsync` 调用了服务器停机；`Dispose` 后端口应关闭。
- Scoped 实例数为 1 → 确认 hosted service 构造函数确实写入了 `ScopeFactory`，且 `AddScoped` 在 `AddChannelServer` 之前注册（注册顺序不影响解析，但便于阅读）。

## 3. 示例迁移

- [ ] 3.1 拆出 SampleChannelServer 并删除 ListenerBootstrapper

**Files:**
- Create: `src/NetServer.ConsoleApp/SampleChannelServer.cs`
- Delete: `src/NetServer.ConsoleApp/ListenerBootstrapper.cs`

**Step 1: 新建 `SampleChannelServer.cs`**

```csharp
using Microsoft.Extensions.Hosting.Net;
using System.Text;

namespace NetServer.ConsoleApp
{
    public sealed class SampleChannelServer : ChannelServerBase
    {
        private readonly ILogger<SampleChannelServer> _logger;

        public SampleChannelServer(ChannelServerOptions options, ILogger<SampleChannelServer> logger)
            : base(options)
        {
            _logger = logger;
        }

        protected override async Task ChannelHandleAsync(ChannelContext context)
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

                if (rt.IsCompleted)
                    break;
            }
        }
    }
}
```

**Step 2: 删除 `ListenerBootstrapper.cs`**

Run: `Remove-Item src\NetServer.ConsoleApp\ListenerBootstrapper.cs`
Expected: 文件不存在；`SampleChannelServer` 仅保留在新文件中。

- [ ] 3.2 改写 Program.cs 使用 AddChannelServer

**Files:**
- Modify: `src/NetServer.ConsoleApp/Program.cs`（整体替换）

**Step 1: 写入**

```csharp
using NetServer.ConsoleApp;
using System.Net;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddChannelServer<SampleChannelServer>(options =>
{
    options.EndPoint = new IPEndPoint(IPAddress.Any, 8000);
});
builder.Services.AddHostedService<SenderBootstrapper>();

var host = builder.Build();
host.Run();
```

**Step 2: 构建**

Run: `dotnet build src/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`
Expected: `Build succeeded`。

- [ ] 3.3 冒烟运行示例

**Step 1: 启动**

Run: `dotnet run --project src/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`
Expected: 每秒打印 `>>>>> hello <<<<<`；无异常堆栈。

**Step 2: Ctrl+C**

在运行终端按 Ctrl+C。
Expected: 进程在数秒内退出，退出码 0；无 `NotImplementedException`。

## 4. 文档

- [ ] 4.1 补充 Primitives README 的宿主接入章节

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/README.md`

**Step 1: 在“## 配置”之前插入**

```markdown
## 宿主接入

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddChannelServer<MyServer>(options =>
{
    options.EndPoint = new IPEndPoint(IPAddress.Any, 8000);
    options.ShutdownTimeout = TimeSpan.FromSeconds(10);
});

var host = builder.Build();
host.Run();
```

- `MyServer` 的构造函数必须能解析 `ChannelServerOptions`，例如 `(ChannelServerOptions, ILogger<MyServer>)`。
- 每连接从容器创建独立 `IServiceScope`，可在处理器中通过 `context.Services` 解析 Scoped 服务。
- 宿主 `HostOptions.ShutdownTimeout` 应大于 `ChannelServerOptions.ShutdownTimeout`，否则宿主会先截断停机等待。
- 未配置 `EndPoint` 或配置非法时，`host.StartAsync()` 抛出 `ArgumentException`，不会进入监听状态。
```

**Step 2: 确认代码块闭合**

Run: `Select-String -Path src\Galosys.Foundation.Primitives\README.md -Pattern '^```' | Measure-Object`
Expected: `Count` 为偶数。

- [ ] 4.2 新建 NetServer README

**Files:**
- Create: `src/NetServer.ConsoleApp/README.md`

**Step 1: 写入**

```markdown
# NetServer.ConsoleApp

`Galosys.Foundation.Primitives` 的服务端示例：监听 `0.0.0.0:8000`，打印收到的 UTF-8 文本，并每秒发送 `hello` 到本机端口 8000。

## 运行

```powershell
dotnet run --project src/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj
```

预期输出（每秒一条）：

```text
>>>>> hello <<<<<
```

按 Ctrl+C 触发宿主优雅停机。

## 结构

| 文件 | 作用 |
|---|---|
| `Program.cs` | 注册 `AddChannelServer<SampleChannelServer>` 与 `SenderBootstrapper` |
| `SampleChannelServer.cs` | 服务端处理器：读取并打印字节 |
| `SenderBootstrapper.cs` | 后台客户端：每秒发送 `hello`（P4 才迁移到框架客户端） |

## 说明

- `SampleChannelServer` 构造函数接收 `ChannelServerOptions`，由 DI 提供。
- 宿主 `ShutdownTimeout` 默认 30 秒，服务器 `ShutdownTimeout` 默认 10 秒。
- 端口 8000 被占用时，`host.Run()` 在启动阶段失败并输出异常。
```

- [ ] 4.3 更新根 AGENTS.md

**Files:**
- Modify: `AGENTS.md`

**Step 1: 替换“没有测试”段落**

将：

```markdown
- 仓库没有测试，`dotnet build` 是唯一的验证手段，不要去找 `dotnet test`。
```

改为：

```markdown
- 测试项目只有 `tests/Galosys.Foundation.Primitives.Tests`（xunit）。运行
  `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`；
  其余项目仍只靠 `dotnet build` 验证。
```

**Step 2: 在“从文件名看不出来的架构事实”中追加一条**

```markdown
- `Galosys.Foundation.Primitives` 的服务端基于 Kestrel 公共 transport（依赖
  `Microsoft.AspNetCore.App`），通过 `AddChannelServer<T>` 接入通用主机，每连接使用独立 DI scope；
  `NetServer.ConsoleApp` 是该库的示例宿主。
```

**Step 3: 确认没有遗留矛盾**

Run: `Select-String -Path AGENTS.md -Pattern "没有测试|唯一的验证手段|dotnet test"`
Expected: 只命中新增的测试说明，不再命中“没有测试”。

## 5. 最终验证

- [ ] 5.1 全量构建与测试

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: `Build succeeded`。

Run: `dotnet build src/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`
Expected: `Build succeeded`。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
Expected: p1-1 的 27 个用例 + 本变更 7 个用例全部通过，0 failed。

- [ ] 5.2 记录验收

**Step 1: 勾选任务**

在 `openspec/changes/primitives-socket-p1-2/tasks.md` 勾选所有完成项。

**Step 2: 写执行记录**

在文件末尾追加：

```markdown
## 执行记录

- 日期：YYYY-MM-DD
- `dotnet build`：通过
- `dotnet test`：XX passed / 0 failed
- 示例冒烟：每秒输出 `>>>>> hello <<<<<`，Ctrl+C 正常退出
```
